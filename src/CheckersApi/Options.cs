namespace CheckersApi;

/// <summary>"Engine" section of appsettings.json.</summary>
public sealed class EngineOptions
{
    /// <summary>kingsrow (Kingsrow64.dll through KrWorker) or stub (built-in engine, for development without KingsRow).</summary>
    public string Type { get; set; } = "kingsrow";

    /// <summary>Name returned in the "engine" field of responses. Defaults to the engine type.</summary>
    public string? Name { get; set; }

    /// <summary>Full path of the engine DLL, e.g. C:\engines\kingsrow\Kingsrow64.dll (egdb64.dll and Kingsrow.odb next to it).</summary>
    public string? Path { get; set; }

    /// <summary>The worker program, relative to the application folder or absolute. A .dll path is started with "dotnet".</summary>
    public string WorkerPath { get; set; } = "worker/KrWorker.exe";

    /// <summary>Number of long-lived engine processes.</summary>
    public int Workers { get; set; } = 2;

    /// <summary>Endgame database folder (KingsRow or Chinook WLD files). Empty = no databases.</summary>
    public string? Databases { get; set; }

    /// <summary>Largest database used (pieces). Positions with this many pieces or fewer are answered from the databases.</summary>
    public int DbMaxPieces { get; set; } = 8;

    /// <summary>Database cache per worker, MB. KingsRow's own default is 16 GB, which is too much for several workers.</summary>
    public int DbCacheMb { get; set; } = 2048;

    public int HashMb { get; set; } = 128;

    public int SearchThreads { get; set; } = 2;

    /// <summary>Opening book: 0 = off, 1 = all kinds of moves (random), 2 = good moves, 3 = best moves.</summary>
    public int Book { get; set; } = 3;

    /// <summary>Opening book file; default Kingsrow.odb next to the engine DLL.</summary>
    public string? BookFile { get; set; }

    /// <summary>Which side the engine's raw score refers to: black (KingsRow) or stm.</summary>
    public string ScorePerspective { get; set; } = "black";

    /// <summary>How long one worker may take to start and open its databases.</summary>
    public int StartupTimeoutMs { get; set; } = 120_000;

    /// <summary>After a stop request, how long to wait for the engine before killing and restarting the worker.</summary>
    public int StopGraceMs { get; set; } = 300;

    /// <summary>The adapter sends a stop if the engine runs this much longer than softTimeMs.</summary>
    public int SoftTimeGraceMs { get; set; } = 150;

    /// <summary>Requests allowed to wait for a free worker before new ones get 503.</summary>
    public int MaxQueue { get; set; } = 64;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Type : Name!;
}

public sealed class CacheOptions
{
    public int Capacity { get; set; } = 20_000;
    public int TtlMinutes { get; set; } = 15;
}

public sealed class LimitsOptions
{
    public int DefaultSoftTimeMs { get; set; } = 300;
    public int DefaultHardTimeMs { get; set; } = 1200;
    public int DefaultMaxDepth { get; set; } = 64;
    public int MaxSoftTimeMs { get; set; } = 10_000;
    public int MaxHardTimeMs { get; set; } = 30_000;
}

/// <summary>One strength level. Request limits override these values.</summary>
public sealed class LevelOptions
{
    public int SoftTimeMs { get; set; } = 300;

    /// <summary>Depth limit. The stub engine honours it; KingsRow's API has no depth limit, so for KingsRow the time decides.</summary>
    public int MaxDepth { get; set; } = 64;

    /// <summary>Answer positions with few pieces from the endgame databases.</summary>
    public bool UseTablebase { get; set; } = true;
}

public sealed class TablebaseOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Search time used for database positions; the engine probes the databases inside the search.</summary>
    public int TimeMs { get; set; } = 20;
}

public sealed class RequestLogOptions
{
    /// <summary>JSON-lines file per day; {date} is replaced with yyyyMMdd. Relative to the app folder. Empty = no file.</summary>
    public string? Path { get; set; } = "logs/requests-{date}.jsonl";
}
