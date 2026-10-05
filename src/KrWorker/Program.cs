using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Checkers.Core;
using KrWorker;

// KrWorker: one long-lived engine process. The API starts a few of these and talks to each over
// stdin/stdout, one JSON object per line.
//
// Requests (stdin):
//   {"op":"init"}                                              -> {"type":"ready","name":...,"dbPieces":8,"initMs":...}
//   {"op":"search","id":"r1","fen":"B:W...:B...","timeMs":250,"maxDepth":12}
//                                                              -> {"type":"result","id":"r1","bestMove":...,"pv":[...],...}
//   {"op":"stop","id":"r1"}                                    -> that search returns at once (its result follows)
//   {"op":"cmd","text":"get dbpath"}                           -> {"type":"reply","rc":1,"text":...}
//   {"op":"ping"}                                              -> {"type":"pong","busy":false}
//   {"op":"quit"}
// The worker also exits when stdin is closed, so it never outlives the API process.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

WorkerOptions opts;
try { opts = WorkerOptions.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    WorkerOptions.PrintUsage();
    return 2;
}

IBackend backend = opts.Engine == "stub" ? new StubBackend() : new KingsRowBackend(opts);
var output = new Output();
output.Send(new { type = "hello", engine = backend.Kind, pid = Environment.ProcessId });

bool initialised = false;
InitInfo? initInfo = null;
CancellationTokenSource? searchStop = null;
string? searchId = null;
object gate = new();
int searching = 0; // 1 while a search runs; cleared just before its result is sent

bool Busy() => Volatile.Read(ref searching) == 1;

string? line;
while ((line = Console.In.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? msg;
    try { msg = JsonNode.Parse(line); }
    catch (JsonException ex)
    {
        output.Send(new { type = "error", message = "Bad JSON: " + ex.Message });
        continue;
    }

    string op = msg?["op"]?.GetValue<string>() ?? "";
    switch (op)
    {
        case "init":
            if (!initialised)
            {
                try
                {
                    initInfo = backend.Init();
                    initialised = true;
                }
                catch (Exception ex)
                {
                    output.Send(new { type = "error", fatal = true, message = $"Engine initialisation failed: {ex.Message}" });
                    return 3;
                }
            }
            output.Send(new { type = "ready", engine = backend.Kind, name = initInfo!.Name, dbPieces = initInfo.DbPieces, initMs = Math.Round(initInfo.InitMs), settings = initInfo.Settings });
            break;

        case "search":
        {
            string id = msg?["id"]?.GetValue<string>() ?? "";
            if (!initialised) { output.Send(new { type = "result", id, error = "Engine not initialised." }); break; }
            if (Busy()) { output.Send(new { type = "result", id, error = "Worker is busy." }); break; }

            Position pos;
            try { pos = Pdn.ParseFen(msg?["fen"]?.GetValue<string>()); }
            catch (PdnException ex) { output.Send(new { type = "result", id, error = ex.Message }); break; }
            int timeMs = msg?["timeMs"]?.GetValue<int>() ?? 300;
            int maxDepth = msg?["maxDepth"]?.GetValue<int>() ?? 0;

            var stop = new CancellationTokenSource();
            Volatile.Write(ref searching, 1);
            var thread = new Thread(() =>
            {
                SearchOutcome r;
                try { r = backend.Search(pos, timeMs, maxDepth, stop.Token); }
                catch (Exception ex) { r = new SearchOutcome { Error = "Search failed: " + ex.Message }; }
                // Free before answering: the API may send the next search as soon as it has this result.
                Volatile.Write(ref searching, 0);
                output.Send(new
                {
                    type = "result",
                    id,
                    bestMove = r.BestMove,
                    fenAfter = r.FenAfter,
                    pv = r.Pv,
                    score = r.Score,
                    wdl = r.Wdl,
                    depth = r.Depth,
                    nodes = r.Nodes,
                    elapsedMs = Math.Round(r.ElapsedMs, 1),
                    stopped = r.Stopped,
                    book = r.Book,
                    status = r.Status,
                    error = r.Error,
                });
            }, 64 * 1024 * 1024) { IsBackground = true, Name = "search" };
            lock (gate)
            {
                searchStop = stop;
                searchId = id;
            }
            thread.Start();
            break;
        }

        case "stop":
        {
            // A stop names the search it is meant for, so a late stop cannot cancel the next search.
            string? stopId = msg?["id"]?.GetValue<string>();
            lock (gate)
                if (stopId == null || stopId == searchId) searchStop?.Cancel();
            break;
        }

        case "cmd":
            if (!initialised || Busy()) { output.Send(new { type = "reply", rc = 0, text = "? busy or not initialised" }); break; }
            var (rc, reply) = backend.Command(msg?["text"]?.GetValue<string>() ?? "");
            output.Send(new { type = "reply", rc, text = reply });
            break;

        case "ping":
            output.Send(new { type = "pong", busy = Busy() });
            break;

        case "quit":
            lock (gate) searchStop?.Cancel();
            return 0;

        default:
            output.Send(new { type = "error", message = $"Unknown op '{op}'." });
            break;
    }
}

// stdin closed: the API process is gone. Stop any search and exit.
lock (gate) searchStop?.Cancel();
return 0;

sealed class Output
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly object _lock = new();
    readonly TextWriter _out;

    public Output()
    {
        var stdout = Console.OpenStandardOutput();
        _out = new StreamWriter(stdout, new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public void Send(object message)
    {
        string text = JsonSerializer.Serialize(message, Json);
        lock (_lock) _out.WriteLine(text);
    }
}

sealed class WorkerOptions
{
    public string Engine = "kingsrow";
    public string? Dll;
    public string? Db;
    public int? DbMaxPieces;
    public int? DbCacheMb;
    public int? HashMb;
    public int? Threads;
    public int? Book;
    public string? BookFile;
    public bool ScoreFromBlack = true;

    public static WorkerOptions Parse(string[] args)
    {
        var o = new WorkerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            int NextInt() => int.TryParse(Next(), out int v) ? v : throw new ArgumentException($"{a} needs a number.");
            switch (a)
            {
                case "--engine":
                    o.Engine = Next().ToLowerInvariant();
                    if (o.Engine is not ("kingsrow" or "stub")) throw new ArgumentException("--engine must be kingsrow or stub.");
                    break;
                case "--dll": o.Dll = Next(); break;
                case "--db": o.Db = Next(); break;
                case "--db-max-pieces": o.DbMaxPieces = NextInt(); break;
                case "--db-cache-mb": o.DbCacheMb = NextInt(); break;
                case "--hash-mb": o.HashMb = NextInt(); break;
                case "--threads": o.Threads = NextInt(); break;
                case "--book": o.Book = NextInt(); break;
                case "--book-file": o.BookFile = Next(); break;
                case "--score-perspective":
                    string p = Next().ToLowerInvariant();
                    o.ScoreFromBlack = p switch
                    {
                        "black" => true,
                        "stm" or "side-to-move" => false,
                        _ => throw new ArgumentException("--score-perspective must be black or stm."),
                    };
                    break;
                default: throw new ArgumentException($"Unknown argument {a}.");
            }
        }
        if (o.Engine == "kingsrow" && string.IsNullOrWhiteSpace(o.Dll)) throw new ArgumentException("--dll is required for the kingsrow engine.");
        return o;
    }

    public static void PrintUsage() => Console.Error.WriteLine("""
        KrWorker --engine kingsrow --dll C:\engines\kingsrow\Kingsrow64.dll [--db C:\kr_english_wld]
                 [--db-max-pieces 8] [--db-cache-mb 2048] [--hash-mb 128] [--threads 2] [--book 3]
                 [--book-file path] [--score-perspective black|stm]
        KrWorker --engine stub
        """);
}
