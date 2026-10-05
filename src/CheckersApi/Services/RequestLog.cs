using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CheckersApi.Services;

/// <summary>One line of the request log.</summary>
public sealed record RequestLogEntry
{
    public DateTime Ts { get; init; } = DateTime.UtcNow;
    public required string RequestId { get; init; }
    public required string Endpoint { get; init; }
    public int Status { get; init; }
    public long TimeMs { get; init; }
    public string? Level { get; init; }
    public int? Depth { get; init; }
    public long? Nodes { get; init; }
    public bool? TablebaseHit { get; init; }
    public bool? Cached { get; init; }
    public int? Worker { get; init; }
    public string? PositionKey { get; init; }
    public string? BestMove { get; init; }
    public bool? Legal { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Writes one JSON object per request: to the application log (JSON console, captured by IIS's stdout log) and,
/// if configured, to a daily JSON-lines file.
/// </summary>
public sealed class RequestLog : IDisposable
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly ILogger<RequestLog> _log;
    readonly string? _pattern;
    readonly object _lock = new();
    StreamWriter? _writer;
    string? _currentFile;
    bool _fileFailed;

    public RequestLog(IOptions<RequestLogOptions> opt, IWebHostEnvironment env, ILogger<RequestLog> log)
    {
        _log = log;
        string? path = opt.Value.Path;
        if (!string.IsNullOrWhiteSpace(path))
            _pattern = Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
    }

    public void Write(RequestLogEntry e)
    {
        string json = JsonSerializer.Serialize(e, Json);
        _log.LogInformation(new EventId(1000, "request"),
            "request {RequestId} {Endpoint} {Status} {TimeMs} {Depth} {Nodes} {TablebaseHit} {Cached} {Level} {Worker} {PositionKey} {BestMove} {Error}",
            e.RequestId, e.Endpoint, e.Status, e.TimeMs, e.Depth, e.Nodes, e.TablebaseHit, e.Cached, e.Level, e.Worker, e.PositionKey, e.BestMove, e.Error);

        if (_pattern == null || _fileFailed) return;
        lock (_lock)
        {
            try
            {
                string file = _pattern.Replace("{date}", DateTime.UtcNow.ToString("yyyyMMdd"));
                if (file != _currentFile)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    _currentFile = file;
                }
                _writer!.WriteLine(json);
            }
            catch (Exception ex)
            {
                _fileFailed = true;
                _log.LogWarning(ex, "Cannot write the request log file {File}; continuing with the console log only. " +
                    "Give the application pool identity write access to the logs folder.", _pattern);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
