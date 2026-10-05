using Checkers.Core;

namespace CheckersApi.Engine;

/// <summary>Search limits passed to the engine. Hard time is enforced by the caller's CancellationToken.</summary>
public sealed record SearchLimits(int SoftTimeMs, int MaxDepth);

/// <summary>Engine answer for the position set with <see cref="IEngineAdapter.SetPosition"/>.</summary>
public sealed record EngineSearchResult(
    string? BestMove,
    IReadOnlyList<string> Pv,
    object? ScoreOrWdl,
    int Depth,
    long Nodes,
    bool TablebaseHit,
    double EngineTimeMs,
    bool Book,
    string? FenAfter,
    string? RawStatus,
    string? Error);

/// <summary>The engine as the service sees it (the spec's EngineAdapter).</summary>
public interface IEngineAdapter
{
    int Id { get; }
    bool IsReady { get; }
    string EngineName { get; }

    /// <summary>Largest number of pieces covered by the endgame databases this engine has open (0 = none).</summary>
    int TablebasePieces { get; }

    /// <summary>Set the position to search, as PDN FEN.</summary>
    void SetPosition(string pdn);

    /// <summary>Search the current position. Cancelling the token stops the engine (hard time limit).</summary>
    Task<EngineSearchResult> SearchAsync(SearchLimits limits, CancellationToken ct);
}

/// <summary>EngineAdapter backed by one long-lived KrWorker process.</summary>
public sealed class WorkerEngineAdapter : IEngineAdapter
{
    readonly WorkerProcess _worker;
    Position? _position;

    public WorkerEngineAdapter(WorkerProcess worker) => _worker = worker;

    public int Id => _worker.Id;
    public bool IsReady => _worker.State == WorkerState.Ready;
    public string EngineName => _worker.EngineName;
    public int TablebasePieces => _worker.DbPieces;

    public void SetPosition(string pdn) => _position = Pdn.ParseFen(pdn);

    public async Task<EngineSearchResult> SearchAsync(SearchLimits limits, CancellationToken ct)
    {
        var pos = _position ?? throw new InvalidOperationException("SetPosition must be called before SearchAsync.");
        var r = await _worker.SearchAsync(pos.ToFen(), limits.SoftTimeMs, limits.MaxDepth, ct);

        bool tablebaseHit = TablebasePieces > 0 && pos.PieceCount <= TablebasePieces;
        object? scoreOrWdl = tablebaseHit && r.Wdl != null ? r.Wdl : r.Score;
        return new EngineSearchResult(
            r.BestMove,
            r.Pv,
            scoreOrWdl,
            r.Depth ?? 0,
            r.Nodes ?? 0,
            tablebaseHit,
            r.ElapsedMs,
            r.Book,
            r.FenAfter,
            r.Status,
            r.Error);
    }
}
