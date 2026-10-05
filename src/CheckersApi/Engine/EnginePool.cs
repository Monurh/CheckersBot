using Microsoft.Extensions.Options;

namespace CheckersApi.Engine;

public sealed class PoolUnavailableException : Exception
{
    public PoolUnavailableException(string message) : base(message) { }
}

/// <summary>
/// A small pool of long-lived engine workers. Requests are spread round robin, each worker serves one request at a
/// time (async lock per worker). If the round-robin worker is busy but another one is free, the free one is used, so a
/// request never waits behind a busy worker while another sits idle. Dead workers are restarted in the background.
/// </summary>
public sealed class EnginePool : IHostedService, IAsyncDisposable
{
    sealed class Slot
    {
        public required WorkerProcess Worker { get; init; }
        public required WorkerEngineAdapter Adapter { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public bool Restarting;
    }

    readonly EngineOptions _opt;
    readonly ILogger<EnginePool> _log;
    readonly List<Slot> _slots = new();
    readonly CancellationTokenSource _stopping = new();
    Task? _supervisor;
    int _roundRobin = -1;
    int _waiting;

    public EnginePool(IOptions<EngineOptions> opt, IWebHostEnvironment env, ILogger<EnginePool> log, ILoggerFactory loggers)
    {
        _opt = opt.Value;
        _log = log;
        int n = Math.Max(1, _opt.Workers);
        for (int i = 1; i <= n; i++)
        {
            var w = new WorkerProcess(i, _opt, env.ContentRootPath, loggers.CreateLogger($"Worker{i}"));
            _slots.Add(new Slot { Worker = w, Adapter = new WorkerEngineAdapter(w) });
        }
    }

    public int Configured => _slots.Count;
    public int ReadyCount => _slots.Count(s => s.Adapter.IsReady);
    public string EngineName => _slots.Select(s => s.Worker.EngineName).FirstOrDefault(n => n.Length > 0) ?? "";

    /// <summary>Database size shared by the ready workers (they all use the same settings).</summary>
    public int TablebasePieces => _slots.Where(s => s.Adapter.IsReady).Select(s => s.Adapter.TablebasePieces).DefaultIfEmpty(0).Min();

    public IReadOnlyList<object> Describe() => _slots.Select(s => (object)new
    {
        id = s.Worker.Id,
        state = s.Worker.State.ToString().ToLowerInvariant(),
        pid = s.Worker.ProcessId,
        busy = s.Lock.CurrentCount == 0,
        starts = s.Worker.Starts,
        initMs = Math.Round(s.Worker.InitMs),
    }).ToList();

    /// <summary>Start every worker and wait until they are warm (databases open), then keep them alive.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("Starting {Count} {Engine} workers", _slots.Count, _opt.Type);
        await Task.WhenAll(_slots.Select(s => StartSlotAsync(s, ct)));
        _log.LogInformation("{Ready} of {Count} workers ready", ReadyCount, _slots.Count);
        _supervisor = Task.Run(SuperviseAsync);
    }

    async Task StartSlotAsync(Slot s, CancellationToken ct)
    {
        try
        {
            await s.Worker.StartAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError("Worker {Worker} could not start: {Message}", s.Worker.Id, ex.Message);
        }
    }

    async Task SuperviseAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try { await Task.Delay(2000, _stopping.Token); }
            catch (OperationCanceledException) { return; }

            foreach (var s in _slots)
            {
                if (s.Worker.State != WorkerState.Dead || s.Restarting) continue;
                // Back off if the worker keeps failing.
                if (DateTime.UtcNow - s.Worker.LastStartUtc < TimeSpan.FromSeconds(Math.Min(60, 2 * s.Worker.Starts))) continue;
                s.Restarting = true;
                _ = Task.Run(async () =>
                {
                    await s.Lock.WaitAsync();
                    try
                    {
                        _log.LogWarning("Restarting worker {Worker}", s.Worker.Id);
                        await StartSlotAsync(s, _stopping.Token);
                    }
                    finally
                    {
                        s.Lock.Release();
                        s.Restarting = false;
                    }
                });
            }
        }
    }

    /// <summary>Lease a worker for one request. Dispose the lease to give it back.</summary>
    public async Task<Lease> AcquireAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _waiting) > _opt.MaxQueue + _slots.Count)
        {
            Interlocked.Decrement(ref _waiting);
            throw new PoolUnavailableException("Too many requests are waiting for an engine; try again shortly.");
        }
        try
        {
            while (true)
            {
                var ready = _slots.Where(s => s.Adapter.IsReady).ToList();
                if (ready.Count == 0) throw new PoolUnavailableException("No engine worker is ready yet.");

                int start = (int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)ready.Count);
                for (int i = 0; i < ready.Count; i++)
                {
                    var s = ready[(start + i) % ready.Count];
                    if (s.Lock.Wait(0))
                    {
                        if (s.Adapter.IsReady) return new Lease(s.Adapter, s.Lock);
                        s.Lock.Release();
                    }
                }

                // All busy: queue on the round-robin choice.
                var chosen = ready[start];
                await chosen.Lock.WaitAsync(ct);
                if (chosen.Adapter.IsReady) return new Lease(chosen.Adapter, chosen.Lock);
                chosen.Lock.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    public sealed class Lease : IDisposable
    {
        readonly SemaphoreSlim _lock;
        bool _released;

        public Lease(IEngineAdapter adapter, SemaphoreSlim @lock)
        {
            Adapter = adapter;
            _lock = @lock;
        }

        public IEngineAdapter Adapter { get; }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping.Cancel();
        await Task.WhenAll(_slots.Select(s => s.Worker.DisposeAsync().AsTask()));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested) await StopAsync(CancellationToken.None);
        _stopping.Dispose();
    }
}
