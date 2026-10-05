using System.Diagnostics;
using Checkers.Core;

namespace KrWorker;

sealed record InitInfo(string Name, int DbPieces, double InitMs, Dictionary<string, string> Settings);

sealed record SearchOutcome
{
    public string? BestMove { get; init; }
    public string? FenAfter { get; init; }
    public IReadOnlyList<string> Pv { get; init; } = Array.Empty<string>();
    /// <summary>Engine score from the side to move's point of view (engine units).</summary>
    public int? Score { get; init; }
    /// <summary>WIN, LOSS or DRAW for the side to move when the engine reports it, else null.</summary>
    public string? Wdl { get; init; }
    public int? Depth { get; init; }
    public long? Nodes { get; init; }
    public double ElapsedMs { get; init; }
    public bool Stopped { get; init; }
    public bool Book { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
}

interface IBackend
{
    string Kind { get; }
    InitInfo Init();
    SearchOutcome Search(Position pos, int timeMs, int maxDepth, CancellationToken stop);
    (int rc, string reply) Command(string text);
}

/// <summary>KingsRow through its CheckerBoard DLL interface.</summary>
sealed class KingsRowBackend : IBackend
{
    readonly WorkerOptions _o;
    KingsRowDll? _dll;

    public KingsRowBackend(WorkerOptions o) => _o = o;

    public string Kind => "kingsrow";

    KingsRowDll Dll => _dll ?? throw new InvalidOperationException("Engine not initialised.");

    public InitInfo Init()
    {
        var sw = Stopwatch.StartNew();
        string dllPath = Path.GetFullPath(_o.Dll ?? throw new ArgumentException("--dll is required for the kingsrow engine."));
        string dllDir = Path.GetDirectoryName(dllPath)!;

        // egdb64.dll normally sits next to the engine; in a CheckerBoard install it is one folder up.
        string helperDir = dllDir;
        string? parent = Path.GetDirectoryName(dllDir);
        if (!File.Exists(Path.Combine(dllDir, "egdb64.dll")) && parent != null && File.Exists(Path.Combine(parent, "egdb64.dll")))
            helperDir = parent;

        Directory.SetCurrentDirectory(dllDir);
        _dll = KingsRowDll.Load(dllPath, helperDir);

        var settings = new Dictionary<string, string>();
        void Set(string command)
        {
            var (rc, reply) = Dll.Command(command);
            settings[command] = $"rc={rc} {reply}";
        }

        string? bookFile = _o.BookFile;
        if (bookFile == null && File.Exists(Path.Combine(dllDir, "Kingsrow.odb"))) bookFile = Path.Combine(dllDir, "Kingsrow.odb");
        if (bookFile != null) Set($"set bookfile {bookFile}");
        if (_o.Book != null) Set($"set book {_o.Book}");
        if (_o.HashMb != null) Set($"set hashsize {_o.HashMb}");
        if (_o.Threads != null) Set($"set searchthreads {_o.Threads}");

        int dbPieces = 0;
        if (!string.IsNullOrWhiteSpace(_o.Db))
        {
            var (_, check) = Dll.Command($"set check_wld_dir {_o.Db}");
            settings["check_wld_dir"] = check;
            dbPieces = KingsRowStatus.ParseDbPieces(check) ?? 0;
            Set($"set dbpath {_o.Db}");
            Set("set enable_wld 1");
            if (_o.DbMaxPieces != null) Set($"set max_dbpieces {_o.DbMaxPieces}");
            if (_o.DbCacheMb != null) Set($"set dbmbytes {_o.DbCacheMb}");
        }
        else
        {
            Set("set enable_wld 0");
        }

        // Warm up: the first search opens the endgame databases (several seconds), so do it now.
        var warm = Pdn.ParseFen("B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12");
        string initStatus = "";
        var (_, _, status1) = Dll.GetMove(warm, 0.05, KingsRowDll.InfoResetMoves | KingsRowDll.InfoExactTime);
        initStatus = status1;
        var endgame = Pdn.ParseFen("W:WK19,K23,30:B1,5,K12");
        Dll.GetMove(endgame, 0.02, KingsRowDll.InfoResetMoves | KingsRowDll.InfoExactTime);

        if (dbPieces == 0 && !string.IsNullOrWhiteSpace(_o.Db))
            dbPieces = KingsRowStatus.ParseDbPieces(initStatus) ?? 0;
        if (_o.DbMaxPieces is int max && max > 0) dbPieces = Math.Min(dbPieces, max);

        string name = Dll.Command("name").reply;
        foreach (string q in new[] { "get dbpath", "get enable_wld", "get max_dbpieces", "get dbmbytes", "get hashsize", "get searchthreads", "get book", "get bookfile" })
            settings[q] = Dll.Command(q).reply;

        return new InitInfo(name, dbPieces, sw.Elapsed.TotalMilliseconds, settings);
    }

    public SearchOutcome Search(Position pos, int timeMs, int maxDepth, CancellationToken stop)
    {
        var legal = MoveGenerator.Generate(pos);
        if (legal.Count == 0)
            return new SearchOutcome { Wdl = "LOSS", Status = "No moves" };

        var sw = Stopwatch.StartNew();
        var (rc, after, status) = Dll.GetMove(pos, Math.Max(1, timeMs) / 1000.0,
            KingsRowDll.InfoResetMoves | KingsRowDll.InfoExactTime, stop);
        sw.Stop();

        var parsed = KingsRowStatus.Parse(status);
        var move = legal.FirstOrDefault(m => pos.Apply(m).Equals(after));
        var pv = ResolvePv(pos, parsed.Pv);
        if (move != null && (pv.Count == 0 || pv[0] != move.Notation)) pv = new List<string> { move.Notation };

        int? score = parsed.Value;
        if (score != null && _o.ScoreFromBlack && pos.SideToMove == Side.White) score = -score;

        string? wdl = rc switch { 0 => "DRAW", 1 => "WIN", 2 => "LOSS", _ => null };
        long? nodes = parsed.KiloNodesPerSecond is long knps ? (long)(knps * sw.Elapsed.TotalMilliseconds) : null;

        return new SearchOutcome
        {
            BestMove = move?.Notation,
            FenAfter = after.ToFen(),
            Pv = pv,
            Score = score,
            Wdl = wdl,
            Depth = parsed.Depth,
            Nodes = nodes,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            Stopped = stop.IsCancellationRequested,
            Book = parsed.IsBook,
            Status = status,
            Error = move == null ? $"The engine returned a position that no legal move produces: {after.ToFen()}" : null,
        };
    }

    public (int rc, string reply) Command(string text) => Dll.Command(text);

    internal static List<string> ResolvePv(Position pos, IEnumerable<string> tokens)
    {
        var list = new List<string>();
        var p = pos;
        foreach (string t in tokens)
        {
            var r = MoveNotation.Resolve(p, t, firstIfAmbiguous: true);
            if (!r.IsLegal) break;
            list.Add(r.Move!.Notation);
            p = p.Apply(r.Move!);
        }
        return list;
    }
}

/// <summary>The small built-in engine: lets the service run anywhere without KingsRow (for development and tests).</summary>
sealed class StubBackend : IBackend
{
    readonly SimpleSearch _search = new();

    public string Kind => "stub";

    public InitInfo Init() => new("stub-alphabeta", 0, 0, new Dictionary<string, string>());

    public SearchOutcome Search(Position pos, int timeMs, int maxDepth, CancellationToken stop)
    {
        var sw = Stopwatch.StartNew();
        var r = _search.Search(pos, maxDepth > 0 ? maxDepth : 64, Math.Max(1, timeMs), stop);
        sw.Stop();
        if (r.BestMove == null)
            return new SearchOutcome { Wdl = "LOSS", ElapsedMs = sw.Elapsed.TotalMilliseconds, Status = "No moves" };

        string? wdl = Math.Abs(r.Score) >= SimpleSearch.WinScore - 200 ? (r.Score > 0 ? "WIN" : "LOSS") : null;
        return new SearchOutcome
        {
            BestMove = r.BestMove.Notation,
            FenAfter = pos.Apply(r.BestMove).ToFen(),
            Pv = r.Pv.Select(m => m.Notation).ToList(),
            Score = r.Score,
            Wdl = wdl,
            Depth = r.Depth,
            Nodes = r.Nodes,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            Stopped = r.Stopped && stop.IsCancellationRequested,
            Status = $"stub depth {r.Depth} score {r.Score} nodes {r.Nodes}",
        };
    }

    public (int rc, string reply) Command(string text) => (0, "? (stub engine has no commands)");
}
