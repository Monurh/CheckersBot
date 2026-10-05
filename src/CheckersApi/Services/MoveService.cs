using Checkers.Core;
using CheckersApi.Engine;
using Microsoft.Extensions.Options;

namespace CheckersApi.Services;

/// <summary>A suggest request after validation, with the limits that apply to it.</summary>
public sealed record PreparedSuggest(
    Position Position,
    string Fen,
    string? Level,
    int SoftTimeMs,
    int HardTimeMs,
    int MaxDepth,
    bool UseTablebase);

/// <summary>What happened while serving a request, for the request log.</summary>
public sealed class SuggestTrace
{
    public int? Worker;
    public string? EngineStatus;
}

/// <summary>
/// Request flow for /v1/move/suggest: parse and validate the PDN, answer from the cache if possible, use the
/// endgame databases for small positions, otherwise search with the level's limits, check that the engine's
/// move is legal, cache the answer.
/// </summary>
public sealed class MoveService
{
    public const string GameId = "checkers-8x8";

    readonly EnginePool _pool;
    readonly EngineOptions _engine;
    readonly LimitsOptions _limits;
    readonly TablebaseOptions _tablebase;
    readonly Dictionary<string, LevelOptions> _levels;
    readonly LruCache<string, SuggestResponse> _cache;
    readonly ILogger<MoveService> _log;

    public MoveService(EnginePool pool, IOptions<EngineOptions> engine, IOptions<LimitsOptions> limits,
        IOptions<TablebaseOptions> tablebase, IOptions<Dictionary<string, LevelOptions>> levels,
        IOptions<CacheOptions> cache, ILogger<MoveService> log)
    {
        _pool = pool;
        _engine = engine.Value;
        _limits = limits.Value;
        _tablebase = tablebase.Value;
        _levels = new Dictionary<string, LevelOptions>(levels.Value, StringComparer.OrdinalIgnoreCase);
        _cache = new LruCache<string, SuggestResponse>(Math.Max(1, cache.Value.Capacity), TimeSpan.FromMinutes(Math.Max(0, cache.Value.TtlMinutes)));
        _log = log;
    }

    public IReadOnlyCollection<string> Levels => _levels.Keys;

    /// <summary>Validate the request (422 on any problem) and work out its limits.</summary>
    public PreparedSuggest Prepare(SuggestRequest? req)
    {
        if (req == null) throw ApiException.Unprocessable("invalid_request", "Request body is missing.");
        if (req.GameId != null && !req.GameId.Equals(GameId, StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unprocessable("unsupported_game", $"Unsupported gameId '{req.GameId}'; only '{GameId}' (English checkers) is supported.");
        if (req.State == null || string.IsNullOrWhiteSpace(req.State.Position))
            throw ApiException.Unprocessable("invalid_pdn", "state.position is required.");
        if (req.State.Notation != null && !req.State.Notation.Equals("PDN", StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unprocessable("unsupported_notation", $"Unsupported notation '{req.State.Notation}'; use PDN.");

        var pos = ParsePosition(req.State.Position);

        LevelOptions? level = null;
        string? levelName = null;
        if (!string.IsNullOrWhiteSpace(req.Level))
        {
            if (!_levels.TryGetValue(req.Level, out level))
                throw ApiException.Unprocessable("invalid_level", $"Unknown level '{req.Level}'; use one of: {string.Join(", ", _levels.Keys)}.");
            levelName = req.Level.ToLowerInvariant();
        }

        var l = req.Limits;
        if (l?.SoftTimeMs is <= 0 || l?.HardTimeMs is <= 0 || l?.MaxDepth is <= 0)
            throw ApiException.Unprocessable("invalid_limits", "limits.softTimeMs, limits.hardTimeMs and limits.maxDepth must be positive.");

        // Explicit limits in the request override the level's values.
        int soft = Math.Min(l?.SoftTimeMs ?? level?.SoftTimeMs ?? _limits.DefaultSoftTimeMs, _limits.MaxSoftTimeMs);
        int hard = Math.Min(l?.HardTimeMs ?? _limits.DefaultHardTimeMs, _limits.MaxHardTimeMs);
        int depth = l?.MaxDepth ?? level?.MaxDepth ?? _limits.DefaultMaxDepth;
        bool useTb = _tablebase.Enabled && (level?.UseTablebase ?? true);

        return new PreparedSuggest(pos, pos.ToFen(), levelName, soft, hard, depth, useTb);
    }

    public static Position ParsePosition(string? text)
    {
        try { return Pdn.ParseAndValidate(text); }
        catch (PdnException ex) { throw ApiException.Unprocessable("invalid_pdn", ex.Message); }
    }

    public async Task<SuggestResponse> SuggestAsync(PreparedSuggest p, SuggestTrace trace, CancellationToken ct)
    {
        string positionKey = "pdn:" + p.Fen;
        var legal = MoveGenerator.Generate(p.Position);
        if (legal.Count == 0)
        {
            return new SuggestResponse
            {
                Engine = _engine.DisplayName,
                BestMove = null,
                Pv = Array.Empty<string>(),
                ScoreOrWdl = "LOSS",
                PositionKey = positionKey,
                Info = new SuggestInfo { Level = p.Level, SoftTimeMs = p.SoftTimeMs, MaxDepth = p.MaxDepth, GameOver = true },
            };
        }

        int tbPieces = Math.Min(_pool.TablebasePieces, _engine.DbMaxPieces);
        bool tablebase = p.UseTablebase && tbPieces > 0 && p.Position.PieceCount <= tbPieces;
        var limits = tablebase
            ? new SearchLimits(Math.Min(p.SoftTimeMs, _tablebase.TimeMs), p.MaxDepth)
            : new SearchLimits(p.SoftTimeMs, p.MaxDepth);

        string cacheKey = $"{p.Fen}|{p.Level}|{limits.SoftTimeMs}|{limits.MaxDepth}|{(tablebase ? "tb" : "")}";
        if (_cache.TryGet(cacheKey, out var cached))
            return cached with { Info = cached.Info with { Cached = true, Worker = null } };

        EngineSearchResult r;
        using (var lease = await _pool.AcquireAsync(ct))
        {
            trace.Worker = lease.Adapter.Id;
            lease.Adapter.SetPosition(p.Fen);
            r = await lease.Adapter.SearchAsync(limits, ct);
        }
        trace.EngineStatus = r.RawStatus;

        // Make sure the move is legal; if not, fall back to the principal variation's first legal move.
        Move? move = null;
        foreach (string? candidate in new[] { r.BestMove }.Concat(r.Pv))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var res = MoveNotation.Resolve(p.Position, legal, candidate, firstIfAmbiguous: true);
            if (res.IsLegal) { move = res.Move; break; }
        }
        if (move == null)
        {
            _log.LogError("Engine returned no legal move for {Fen}: best {Best}, pv {Pv}, error {Error}, status {Status}",
                p.Fen, r.BestMove, string.Join(' ', r.Pv), r.Error, r.RawStatus);
            throw new ApiException(StatusCodes.Status500InternalServerError, "engine_illegal_move",
                "The engine did not return a legal move.");
        }

        var pv = r.Pv.ToList();
        if (pv.Count == 0 || pv[0] != move.Notation) pv = new List<string> { move.Notation };

        var response = new SuggestResponse
        {
            Engine = _engine.DisplayName,
            BestMove = move.Notation,
            Pv = pv,
            ScoreOrWdl = r.ScoreOrWdl,
            Depth = r.Depth,
            Nodes = r.Nodes,
            PositionKey = positionKey,
            Info = new SuggestInfo
            {
                TablebaseHit = r.TablebaseHit,
                Level = p.Level,
                SoftTimeMs = limits.SoftTimeMs,
                MaxDepth = limits.MaxDepth,
                Book = r.Book,
                Worker = trace.Worker,
            },
        };
        _cache.Set(cacheKey, response);
        return response;
    }

    public ValidateResponse Validate(ValidateRequest? req)
    {
        if (req == null) throw ApiException.Unprocessable("invalid_request", "Request body is missing.");
        var pos = ParsePosition(req.Position);
        var res = MoveNotation.Resolve(pos, req.Move);
        return res.IsLegal
            ? new ValidateResponse(true, res.Move!.Notation, pos.Apply(res.Move!).ToFen())
            : new ValidateResponse(false, Reason: res.Message);
    }

    public object LegalMoves(PositionRequest? req)
    {
        if (req == null) throw ApiException.Unprocessable("invalid_request", "Request body is missing.");
        var pos = ParsePosition(req.Position);
        var moves = MoveGenerator.Generate(pos);
        return new
        {
            position = pos.ToFen(),
            sideToMove = pos.SideToMove == Side.White ? "W" : "B",
            pieces = pos.PieceCount,
            gameOver = moves.Count == 0,
            moves = moves.Select(m => new
            {
                move = m.Notation,
                from = m.From,
                to = m.To,
                path = m.Path,
                captured = m.Captured,
                positionAfter = pos.Apply(m).ToFen(),
            }),
        };
    }
}
