using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CheckersApi.Engine;

public enum WorkerState { Starting, Ready, Dead }

/// <summary>Raw search result from a worker.</summary>
public sealed record WorkerResult(
    string? BestMove,
    string? FenAfter,
    IReadOnlyList<string> Pv,
    int? Score,
    string? Wdl,
    int? Depth,
    long? Nodes,
    double ElapsedMs,
    bool Stopped,
    bool Book,
    string? Status,
    string? Error);

public sealed class WorkerHungException : Exception
{
    public WorkerHungException(string message) : base(message) { }
}

public sealed class WorkerFailedException : Exception
{
    public WorkerFailedException(string message) : base(message) { }
}

/// <summary>
/// One KrWorker child process and the line protocol to it (one JSON object per line on stdin/stdout).
/// Not thread-safe for searches: the pool gives each worker to one request at a time.
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly EngineOptions _opt;
    readonly string _contentRoot;
    readonly ILogger _log;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    readonly object _writeLock = new();
    TaskCompletionSource<JsonObject> _ready = NewTcs();
    Process? _process;
    volatile WorkerState _state = WorkerState.Starting;

    public WorkerProcess(int id, EngineOptions opt, string contentRoot, ILogger log)
    {
        Id = id;
        _opt = opt;
        _contentRoot = contentRoot;
        _log = log;
    }

    public int Id { get; }
    public WorkerState State => _state;
    public string EngineName { get; private set; } = "";
    public int DbPieces { get; private set; }
    public int? ProcessId => _process?.Id;
    public double InitMs { get; private set; }
    public int Starts { get; private set; }
    public DateTime LastStartUtc { get; private set; }

    static TaskCompletionSource<JsonObject> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Start the process, initialise the engine (opens the databases) and wait until it is ready.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Kill("restart");
        _state = WorkerState.Starting;
        _ready = NewTcs();
        Starts++;
        LastStartUtc = DateTime.UtcNow;

        var psi = BuildStartInfo();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => OnExited(p);
        if (!p.Start()) throw new WorkerFailedException("Could not start the worker process.");
        _process = p;
        ChildProcessJob.Add(p, _log);
        _log.LogInformation("Worker {Worker} started: pid {Pid}, {File} {Args}", Id, p.Id, psi.FileName, string.Join(' ', psi.ArgumentList));

        _ = Task.Run(() => ReadStdoutAsync(p));
        _ = Task.Run(() => ReadStderrAsync(p));

        Send(new { op = "init" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.StartupTimeoutMs);
        JsonObject ready;
        try
        {
            ready = await _ready.Task.WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            Kill("start failed");
            throw new WorkerFailedException(ex is OperationCanceledException
                ? $"Worker {Id} did not become ready within {_opt.StartupTimeoutMs} ms."
                : $"Worker {Id} failed to start: {ex.Message}");
        }

        EngineName = ready["name"]?.GetValue<string>() ?? "";
        DbPieces = ready["dbPieces"]?.GetValue<int>() ?? 0;
        InitMs = ready["initMs"]?.GetValue<double>() ?? 0;
        _state = WorkerState.Ready;
        _log.LogInformation("Worker {Worker} ready: {Engine}, databases up to {DbPieces} pieces, init {InitMs} ms, settings {Settings}",
            Id, EngineName, DbPieces, InitMs, ready["settings"]?.ToJsonString());
    }

    ProcessStartInfo BuildStartInfo()
    {
        // A relative path is looked up in the site folder (IIS) and then next to the app's binaries (dotnet run).
        var bases = Path.IsPathRooted(_opt.WorkerPath)
            ? new[] { "" }
            : new[] { _contentRoot, AppContext.BaseDirectory };

        // Prefer the .exe; fall back to running the .dll with "dotnet" (for example on Linux, or a portable build).
        string? exe = null, dll = null;
        foreach (string b in bases)
        {
            string worker = Path.GetFullPath(Path.Combine(b, _opt.WorkerPath));
            if (worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { if (File.Exists(worker)) dll = worker; }
            else if (File.Exists(worker)) exe = worker;
            else if (File.Exists(Path.ChangeExtension(worker, ".dll"))) dll = Path.ChangeExtension(worker, ".dll");
            else if (File.Exists(Path.ChangeExtension(worker, null))) exe = Path.ChangeExtension(worker, null);
            if (exe != null || dll != null) break;
        }
        if (exe == null && dll == null)
            throw new WorkerFailedException($"Worker program not found: {_opt.WorkerPath} (looked in {string.Join(" and ", bases)}).");

        var psi = new ProcessStartInfo
        {
            FileName = exe ?? "dotnet",
            WorkingDirectory = Path.GetDirectoryName(exe ?? dll)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        if (dll != null) psi.ArgumentList.Add(dll);

        void Arg(string name, object? value)
        {
            if (value == null || (value is string s && string.IsNullOrWhiteSpace(s))) return;
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        }

        if (_opt.Type.Equals("stub", StringComparison.OrdinalIgnoreCase))
        {
            Arg("--engine", "stub");
        }
        else
        {
            Arg("--engine", "kingsrow");
            Arg("--dll", _opt.Path);
            if (!string.IsNullOrWhiteSpace(_opt.Databases))
            {
                Arg("--db", _opt.Databases);
                Arg("--db-max-pieces", _opt.DbMaxPieces);
                Arg("--db-cache-mb", _opt.DbCacheMb);
            }
            Arg("--hash-mb", _opt.HashMb);
            Arg("--threads", _opt.SearchThreads);
            Arg("--book", _opt.Book);
            Arg("--book-file", _opt.BookFile);
            Arg("--score-perspective", _opt.ScorePerspective);
        }
        return psi;
    }

    /// <summary>
    /// Search the given position. The engine is told to stop when <paramref name="ct"/> fires (hard time limit
    /// or client gone) or when it runs past softTimeMs plus a grace period. If it then does not answer
    /// within StopGraceMs the process is killed (the pool restarts it) and <see cref="WorkerHungException"/> is thrown.
    /// </summary>
    public async Task<WorkerResult> SearchAsync(string fen, int softTimeMs, int maxDepth, CancellationToken ct)
    {
        if (_state != WorkerState.Ready) throw new WorkerFailedException($"Worker {Id} is not ready.");
        string id = Guid.NewGuid().ToString("N")[..12];
        var tcs = NewTcs();
        _pending[id] = tcs;
        try
        {
            Send(new { op = "search", id, fen, timeMs = softTimeMs, maxDepth });

            // Soft limit, enforced here as well in case the engine overruns its time.
            using var softTimer = new CancellationTokenSource(softTimeMs + _opt.SoftTimeGraceMs);
            using var softReg = softTimer.Token.Register(() => TrySend(new { op = "stop", id }));

            try
            {
                return Parse(await tcs.Task.WaitAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Hard limit: ask the engine to stop, give it a moment, otherwise kill the process.
                TrySend(new { op = "stop", id });
                try
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(_opt.StopGraceMs));
                }
                catch (TimeoutException)
                {
                    Kill("did not stop after the hard time limit");
                    throw new WorkerHungException($"Worker {Id} did not stop; it was restarted.");
                }
                throw;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    static WorkerResult Parse(JsonObject r)
    {
        var pv = r["pv"] is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
        return new WorkerResult(
            r["bestMove"]?.GetValue<string>(),
            r["fenAfter"]?.GetValue<string>(),
            pv,
            r["score"]?.GetValue<int>(),
            r["wdl"]?.GetValue<string>(),
            r["depth"]?.GetValue<int>(),
            r["nodes"]?.GetValue<long>(),
            r["elapsedMs"]?.GetValue<double>() ?? 0,
            r["stopped"]?.GetValue<bool>() ?? false,
            r["book"]?.GetValue<bool>() ?? false,
            r["status"]?.GetValue<string>(),
            r["error"]?.GetValue<string>());
    }

    void Send(object message)
    {
        var p = _process ?? throw new WorkerFailedException($"Worker {Id} is not running.");
        string line = JsonSerializer.Serialize(message, Json);
        lock (_writeLock)
        {
            p.StandardInput.WriteLine(line);
            p.StandardInput.Flush();
        }
    }

    void TrySend(object message)
    {
        try { Send(message); } catch { /* process already gone */ }
    }

    async Task ReadStdoutAsync(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { _log.LogWarning("Worker {Worker}: unexpected output {Line}", Id, line); continue; }
                if (msg == null) continue;

                switch (msg["type"]?.GetValue<string>())
                {
                    case "ready":
                        _ready.TrySetResult(msg);
                        break;
                    case "result":
                        string id = msg["id"]?.GetValue<string>() ?? "";
                        if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(msg);
                        break;
                    case "error":
                        _log.LogError("Worker {Worker} error: {Message}", Id, msg["message"]?.GetValue<string>());
                        if (msg["fatal"]?.GetValue<bool>() == true)
                            _ready.TrySetException(new WorkerFailedException(msg["message"]?.GetValue<string>() ?? "fatal error"));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended.
        }
        if (ReferenceEquals(p, _process)) MarkDead("output closed");
    }

    async Task ReadStderrAsync(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                if (!string.IsNullOrWhiteSpace(line)) _log.LogWarning("Worker {Worker} stderr: {Line}", Id, line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        int code = -1;
        try { code = p.ExitCode; } catch { }
        MarkDead($"process exited with code {code}");
    }

    void MarkDead(string reason)
    {
        if (_state == WorkerState.Dead) return;
        _state = WorkerState.Dead;
        _log.LogWarning("Worker {Worker} is down: {Reason}", Id, reason);
        var error = new WorkerFailedException($"Worker {Id} stopped: {reason}");
        _ready.TrySetException(error);
        foreach (var tcs in _pending.Values) tcs.TrySetException(error);
    }

    /// <summary>Kill the process (and its children). The pool's supervisor starts a new one.</summary>
    public void Kill(string reason)
    {
        var p = _process;
        if (p == null) return;
        if (_state != WorkerState.Dead) MarkDead(reason);
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        _process = null;
        p.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p != null && !p.HasExited)
        {
            TrySend(new { op = "quit" });
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        Kill("shutdown");
    }
}
