using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Checkers.Tests;

/// <summary>
/// End-to-end tests over HTTP, including the acceptance criteria from the spec.
///
/// By default the tests start the API themselves with the stub engine (no KingsRow needed).
/// To test a running service, for example the real KingsRow setup on Windows, set CHECKERS_API_URL:
///   set CHECKERS_API_URL=http://localhost:5080
///   dotnet run --project tests/Checkers.Tests -- api
/// </summary>
public static class ApiTests
{
    const string G = "api";
    const string SpecFen = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";
    const string MidgameFen = "B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12";

    static ApiHost? _host;
    static readonly SemaphoreSlim HostLock = new(1, 1);

    public static void Register()
    {
        TestRunner.Add(G, "acceptance: health check returns ok on startup", async () =>
        {
            var h = await Host();
            var (status, body, _) = await h.Get("/healthz");
            Assert.Equal(200, status, "HTTP status");
            Assert.Equal(true, body!["ok"]!.GetValue<bool>(), "ok");
            Assert.True(body["workers"]!.GetValue<int>() >= 1, "workers > 0");
        });

        TestRunner.Add(G, "acceptance: database position (<= 8 pieces) under 50 ms with tablebaseHit", async () =>
        {
            var h = await Host();
            var (_, health, _) = await h.Get("/healthz?details=true");
            int tb = health!["tablebasePieces"]?.GetValue<int>() ?? 0;
            if (tb == 0) throw new SkipException("the engine has no endgame databases (stub engine)");

            // Several positions so that the cache cannot answer.
            string[] fens = { "W:WK3,20,32:B10,K22", "B:WK10:B12,18,20,K26", "B:WK4,K10,15,27:B12,K21,K31", "W:WK6,9:B4,8,18,K19" };
            foreach (string fen in fens)
            {
                var (status, r, _) = await h.Suggest(fen, "strong");
                Assert.Equal(200, status, $"HTTP status for {fen}");
                Assert.Equal(true, r!["info"]!["tablebaseHit"]!.GetValue<bool>(), $"tablebaseHit for {fen}");
                long ms = r["info"]!["timeMs"]!.GetValue<long>();
                Assert.True(ms < 50, $"{fen} took {ms} ms");
            }
        });

        TestRunner.Add(G, "acceptance: midgame position, level strong, legal move under 600 ms", async () =>
        {
            var h = await Host();
            var (status, r, _) = await h.Suggest(MidgameFen, "strong");
            Assert.Equal(200, status, "HTTP status");
            string move = r!["bestMove"]!.GetValue<string>();
            long ms = r["info"]!["timeMs"]!.GetValue<long>();
            Assert.True(ms < 600, $"took {ms} ms");
            var (vs, v, _) = await h.Post("/v1/move/validate", new { position = MidgameFen, move });
            Assert.Equal(200, vs, "validate status");
            Assert.Equal(true, v!["legal"]!.GetValue<bool>(), $"{move} is legal");
        });

        TestRunner.Add(G, "acceptance: invalid PDN gets 422", async () =>
        {
            var h = await Host();
            foreach (string bad in new[] { "B:W33:B1", "nonsense", "B:W1,2:B2", "B:W1:B5", "B:W17-32:B1-4" })
            {
                var (status, r, _) = await h.Suggest(bad, "weak");
                Assert.Equal(422, status, $"status for '{bad}'");
                Assert.Equal("invalid_pdn", r!["code"]!.GetValue<string>(), $"error code for '{bad}'");
            }
            var (s2, _, _) = await h.Post("/v1/move/suggest", new { state = new { notation = "FEN", position = SpecFen } });
            Assert.Equal(422, s2, "unsupported notation");
            var (s3, _, _) = await h.Post("/v1/move/suggest", new { gameId = "chess", state = new { notation = "PDN", position = SpecFen } });
            Assert.Equal(422, s3, "unsupported gameId");
            var (s4, _, _) = await h.Post("/v1/move/suggest", new { state = new { notation = "PDN", position = SpecFen }, level = "grandmaster" });
            Assert.Equal(422, s4, "unknown level");
        });

        TestRunner.Add(G, "acceptance: timeout returns 504 and the worker recovers", async () =>
        {
            var h = await Host();
            var sw = Stopwatch.StartNew();
            var (status, r, _) = await h.Post("/v1/move/suggest", new
            {
                gameId = "checkers-8x8",
                // A middlegame with several moves, outside the opening book, so the engine really searches.
                state = new { notation = "PDN", position = "B:W13,21,23,27,29,31,32:B1,4,5,6,7,12" },
                limits = new { softTimeMs = 5000, hardTimeMs = 150 },
            });
            Assert.Equal(504, status, "HTTP status");
            Assert.Equal("timeout", r!["code"]!.GetValue<string>(), "error code");
            Assert.True(sw.ElapsedMilliseconds < 1500, $"answered in {sw.ElapsedMilliseconds} ms");

            var (s2, r2, _) = await h.Suggest(SpecFen, "weak", softTimeMs: 50);
            Assert.Equal(200, s2, "next request works");
            Assert.True(r2!["bestMove"]!.GetValue<string>() is "14x23" or "16x23", "spec position answer");
        });

        TestRunner.Add(G, "suggest: response shape for the spec example", async () =>
        {
            var h = await Host();
            var (status, r, _) = await h.Post("/v1/move/suggest", new
            {
                gameId = "checkers-8x8",
                state = new { notation = "PDN", position = SpecFen },
                level = "weak",
                limits = new { maxDepth = 12, softTimeMs = 250, hardTimeMs = 1200 },
            });
            Assert.Equal(200, status, "HTTP status");
            foreach (string field in new[] { "engine", "bestMove", "pv", "scoreOrWDL", "depth", "nodes", "positionKey", "info" })
                Assert.True(r!.ContainsKey(field), $"field {field}");
            Assert.True(r!["info"]!.AsObject().ContainsKey("tablebaseHit") && r["info"]!.AsObject().ContainsKey("timeMs"), "info.tablebaseHit and info.timeMs");
            Assert.Equal("pdn:" + SpecFen, r["positionKey"]!.GetValue<string>(), "positionKey");
            Assert.True(r["bestMove"]!.GetValue<string>() is "14x23" or "16x23", "Black must capture");
            Assert.Equal(r["bestMove"]!.GetValue<string>(), r["pv"]![0]!.GetValue<string>(), "pv starts with the best move");
        });

        TestRunner.Add(G, "suggest: the same request again comes from the cache", async () =>
        {
            var h = await Host();
            string fen = "W:W21,25,27,28,29,30,31:B3,5,7,9,10,12,18";
            var (_, first, _) = await h.Suggest(fen, "medium");
            var (_, second, _) = await h.Suggest(fen, "medium");
            Assert.Equal(false, first!["info"]!["cached"]!.GetValue<bool>(), "first not cached");
            Assert.Equal(true, second!["info"]!["cached"]!.GetValue<bool>(), "second cached");
            Assert.Equal(first["bestMove"]!.GetValue<string>(), second["bestMove"]!.GetValue<string>(), "same move");
            var (_, other, _) = await h.Suggest(fen, "weak");
            Assert.Equal(false, other!["info"]!["cached"]!.GetValue<bool>(), "a different level is not served from that cache entry");
        });

        TestRunner.Add(G, "suggest: no legal moves gives bestMove null and gameOver", async () =>
        {
            var h = await Host();
            var (status, r, _) = await h.Suggest("B:W8,11:B4", "weak");
            Assert.Equal(200, status, "HTTP status");
            Assert.True(r!["bestMove"] is null, "bestMove is null");
            Assert.Equal(true, r["info"]!["gameOver"]!.GetValue<bool>(), "gameOver");
        });

        TestRunner.Add(G, "suggest: parallel requests use both workers", async () =>
        {
            var h = await Host();
            string[] fens =
            {
                "B:W15,24,25,28,29,31,32:B3,4,5,6,7,8", "W:W21,25,27,28,29,30,31:B3,5,7,9,10,12,18",
                "W:W12,22,26,30,31:B3,4,5,7,13,17,20,21", "W:W19,22,23,27,28,29:B3,6,8,10,11,12,20,K30",
                "W:WK1,20,21,23,25,27,29,30,32:B4,5,6,8,9,10,12", "B:W13,21,23,27,29,31,32:B1,4,5,6,7,12",
            };
            var results = await Task.WhenAll(fens.Select(f => h.Suggest(f, null, softTimeMs: 150, hardTimeMs: 3000)));
            Assert.True(results.All(x => x.status == 200), "all 200: " + string.Join(",", results.Select(x => x.status)));
            var workers = results.Select(x => x.body!["info"]!["worker"]?.GetValue<int>()).Where(w => w != null).Distinct().Count();
            Assert.True(workers >= Math.Min(2, h.Workers), $"requests went to {workers} different workers");
        });

        TestRunner.Add(G, "validate: legal, illegal, short and full notation, bad PDN", async () =>
        {
            var h = await Host();
            async Task<JsonObject> V(string pos, string move)
            {
                var (s, r, _) = await h.Post("/v1/move/validate", new { position = pos, move });
                Assert.Equal(200, s, $"status for {move}");
                return r!;
            }
            Assert.Equal(true, (await V(SpecFen, "14x23"))["legal"]!.GetValue<bool>(), "14x23");
            Assert.Equal(false, (await V(SpecFen, "10-15"))["legal"]!.GetValue<bool>(), "10-15 ignores the forced capture");
            Assert.Equal(false, (await V(SpecFen, "22-18x11-7"))["legal"]!.GetValue<bool>(), "the spec's example move");
            Assert.Equal("5x14x23", (await V("B:W9,18:B5", "5x23"))["move"]!.GetValue<string>(), "short multi-jump");
            Assert.Equal(false, (await V(SpecFen, "hello"))["legal"]!.GetValue<bool>(), "malformed move");
            var (s, _, _) = await h.Post("/v1/move/validate", new { position = "B:W99:B1", move = "1-5" });
            Assert.Equal(422, s, "invalid PDN");
        });

        TestRunner.Add(G, "worker restarts after its process dies", async () =>
        {
            var h = await Host();
            if (!h.Local) throw new SkipException("only when the tests start the API themselves");
            var (_, health, _) = await h.Get("/healthz?details=true");
            int pid = health!["pool"]![0]!["pid"]!.GetValue<int>();
            Process.GetProcessById(pid).Kill();
            var sw = Stopwatch.StartNew();
            bool down = false, back = false;
            while (sw.ElapsedMilliseconds < 20000)
            {
                var (status, body, _) = await h.Get("/healthz");
                int workers = body?["workers"]?.GetValue<int>() ?? 0;
                if (workers < h.Workers) down = true;
                if (down && status == 200 && workers == h.Workers) { back = true; break; }
                await Task.Delay(200);
            }
            Assert.True(down, "health reported the dead worker");
            Assert.True(back, "the worker was restarted");
        });

        TestRunner.Add(G, "request log: one JSON line per request with the required fields", async () =>
        {
            var h = await Host();
            if (!h.Local) throw new SkipException("only when the tests start the API themselves");
            await h.Suggest("W:W13,23,25,27,28,29,30,31,32:B1,2,3,4,5,7,8,12,22", "weak");
            string file = Directory.GetFiles(h.LogDir!, "requests-*.jsonl").Single();
            string last;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                last = reader.ReadToEnd().Trim().Split('\n').Last(l => l.Contains("\"suggest\"") && l.Contains("\"status\":200"));
            var entry = JsonNode.Parse(last)!.AsObject();
            foreach (string f in new[] { "requestId", "timeMs", "depth", "nodes", "tablebaseHit" })
                Assert.True(entry.ContainsKey(f), $"log field {f} in {last}");
        });

        TestRunner.Add(G, "test board page is served", async () =>
        {
            var h = await Host();
            var res = await h.Http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode, "GET /");
            Assert.True((await res.Content.ReadAsStringAsync()).Contains("Checkers Bot"), "page content");
        });

        TestRunner.Add(G, "shutdown: worker processes exit with the API", async () =>
        {
            var h = await Host();
            if (!h.Local) throw new SkipException("only when the tests start the API themselves");
            var (_, health, _) = await h.Get("/healthz?details=true");
            var pids = health!["pool"]!.AsArray().Select(w => w!["pid"]?.GetValue<int>()).OfType<int>().ToList();
            await h.DisposeAsync();
            _host = null;
            bool Alive(int pid)
            {
                try { return !Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { return false; }
            }
            var sw = Stopwatch.StartNew();
            while (pids.Any(Alive) && sw.ElapsedMilliseconds < 5000) await Task.Delay(100);
            foreach (int pid in pids)
                Assert.True(!Alive(pid), $"worker pid {pid} still running 5 s after the API was killed");
        });
    }

    static async Task<ApiHost> Host()
    {
        await HostLock.WaitAsync();
        try { return _host ??= await ApiHost.StartAsync(); }
        finally { HostLock.Release(); }
    }

    public static async Task ShutdownAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        _host = null;
    }
}

public sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason) { }
}

/// <summary>The API under test: a process started by the tests (stub engine), or a running service from CHECKERS_API_URL.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    Process? _process;
    public required HttpClient Http { get; init; }
    public bool Local { get; init; }
    public string? LogDir { get; init; }
    public int Workers { get; set; } = 2;

    public static async Task<ApiHost> StartAsync()
    {
        string? url = Environment.GetEnvironmentVariable("CHECKERS_API_URL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var remote = new ApiHost { Http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) } };
            await remote.WaitHealthyAsync(TimeSpan.FromSeconds(10));
            return remote;
        }

        string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}") ? "Debug" : "Release";
        string root = FindRepoRoot();
        // Same target framework folder as this test program (net10.0 or net8.0, from Directory.Build.props).
        string tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        string apiDir = Path.Combine(root, "src", "CheckersApi", "bin", config, tfm);
        string apiDll = Path.Combine(apiDir, "CheckersApi.dll");
        string workerDll = Path.Combine(apiDir, "worker", "KrWorker.dll");
        if (!File.Exists(apiDll) || !File.Exists(workerDll))
            throw new Exception($"Build the solution first ({config}): {apiDll} or {workerDll} is missing.");

        int port = FreePort();
        string logDir = Path.Combine(Path.GetTempPath(), "checkers-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(apiDll);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Stub";
        psi.Environment["ASPNETCORE_CONTENTROOT"] = Path.Combine(root, "src", "CheckersApi"); // appsettings and wwwroot
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["Engine__WorkerPath"] = workerDll;
        psi.Environment["RequestLog__Path"] = Path.Combine(logDir, "requests-{date}.jsonl");
        var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();

        var host = new ApiHost
        {
            _process = p,
            Local = true,
            LogDir = logDir,
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) },
        };
        await host.WaitHealthyAsync(TimeSpan.FromSeconds(60));
        return host;
    }

    async Task WaitHealthyAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var (status, body, _) = await Get("/healthz?details=true");
                if (status == 200)
                {
                    Workers = body?["configured"]?.GetValue<int>() ?? 2;
                    return;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new Exception($"The API did not become healthy within {timeout.TotalSeconds} s.");
    }

    public async Task<(int status, JsonObject? body, long ms)> Get(string path)
    {
        var sw = Stopwatch.StartNew();
        var res = await Http.GetAsync(path);
        string text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, Parse(text), sw.ElapsedMilliseconds);
    }

    public async Task<(int status, JsonObject? body, long ms)> Post(string path, object body)
    {
        var sw = Stopwatch.StartNew();
        var res = await Http.PostAsJsonAsync(path, body);
        string text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, Parse(text), sw.ElapsedMilliseconds);
    }

    public Task<(int status, JsonObject? body, long ms)> Suggest(string fen, string? level, int? softTimeMs = null, int? hardTimeMs = null)
    {
        var limits = new Dictionary<string, int>();
        if (softTimeMs != null) limits["softTimeMs"] = softTimeMs.Value;
        if (hardTimeMs != null) limits["hardTimeMs"] = hardTimeMs.Value;
        var body = new Dictionary<string, object?>
        {
            ["gameId"] = "checkers-8x8",
            ["state"] = new { notation = "PDN", position = fen },
        };
        if (level != null) body["level"] = level;
        if (limits.Count > 0) body["limits"] = limits;
        return Post("/v1/move/suggest", body);
    }

    static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CheckersBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new Exception("CheckersBot.sln not found above the test folder.");
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        var p = _process;
        if (p == null || p.HasExited) return;
        // Ctrl+C-like shutdown is not available cross-platform for a child; kill the API only (not its tree),
        // so the test can check that the workers exit on their own when their stdin closes.
        p.Kill(entireProcessTree: false);
        await p.WaitForExitAsync();
    }
}
