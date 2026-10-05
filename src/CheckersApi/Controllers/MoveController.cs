using System.Diagnostics;
using CheckersApi.Engine;
using CheckersApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CheckersApi.Controllers;

[ApiController]
[Route("v1/move")]
[Produces("application/json")]
public sealed class MoveController : ControllerBase
{
    readonly MoveService _moves;
    readonly RequestLog _requestLog;
    readonly ILogger<MoveController> _log;

    public MoveController(MoveService moves, RequestLog requestLog, ILogger<MoveController> log)
    {
        _moves = moves;
        _requestLog = requestLog;
        _log = log;
    }

    string RequestId => HttpContext.TraceIdentifier;

    /// <summary>Best move for a position. hardTimeMs is enforced here with a CancellationToken (504 when it expires).</summary>
    [HttpPost("suggest")]
    public async Task<IActionResult> Suggest([FromBody] SuggestRequest? request)
    {
        var sw = Stopwatch.StartNew();
        var trace = new SuggestTrace();
        PreparedSuggest? prepared = null;
        try
        {
            prepared = _moves.Prepare(request);
            using var hard = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            hard.CancelAfter(prepared.HardTimeMs);
            try
            {
                var response = await _moves.SuggestAsync(prepared, trace, hard.Token);
                response = response with { Info = response.Info with { TimeMs = sw.ElapsedMilliseconds } };
                Log(200, sw, prepared, response, trace, null);
                return Ok(response);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or WorkerHungException) && !HttpContext.RequestAborted.IsCancellationRequested)
            {
                return Error(StatusCodes.Status504GatewayTimeout, "timeout",
                    $"No move within hardTimeMs={prepared.HardTimeMs} ms.", sw, prepared, trace);
            }
        }
        catch (ApiException ex)
        {
            return Error(ex.Status, ex.Code, ex.Message, sw, prepared, trace);
        }
        catch (PoolUnavailableException ex)
        {
            Response.Headers.RetryAfter = "1";
            return Error(StatusCodes.Status503ServiceUnavailable, "engine_unavailable", ex.Message, sw, prepared, trace);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            Log(499, sw, prepared, null, trace, "client closed the connection");
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is WorkerFailedException or WorkerHungException)
        {
            _log.LogError(ex, "Engine failure");
            return Error(StatusCodes.Status500InternalServerError, "engine_failure", ex.Message, sw, prepared, trace);
        }
    }

    /// <summary>Is the move legal in the position? Accepts full (9x18x27) and short (9x27) capture notation.</summary>
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidateRequest? request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = _moves.Validate(request);
            _requestLog.Write(new RequestLogEntry
            {
                RequestId = RequestId, Endpoint = "validate", Status = 200, TimeMs = sw.ElapsedMilliseconds,
                Legal = r.Legal, BestMove = r.Move,
            });
            return Ok(r);
        }
        catch (ApiException ex)
        {
            _requestLog.Write(new RequestLogEntry
            {
                RequestId = RequestId, Endpoint = "validate", Status = ex.Status, TimeMs = sw.ElapsedMilliseconds, Error = ex.Message,
            });
            return StatusCode(ex.Status, new ErrorResponse(ex.Code, ex.Message, RequestId));
        }
    }

    IActionResult Error(int status, string code, string message, Stopwatch sw, PreparedSuggest? p, SuggestTrace trace)
    {
        Log(status, sw, p, null, trace, message);
        return StatusCode(status, new ErrorResponse(code, message, RequestId));
    }

    void Log(int status, Stopwatch sw, PreparedSuggest? p, SuggestResponse? r, SuggestTrace trace, string? error) =>
        _requestLog.Write(new RequestLogEntry
        {
            RequestId = RequestId,
            Endpoint = "suggest",
            Status = status,
            TimeMs = sw.ElapsedMilliseconds,
            Level = p?.Level,
            Depth = r?.Depth,
            Nodes = r?.Nodes,
            TablebaseHit = r?.Info.TablebaseHit,
            Cached = r?.Info.Cached,
            Worker = trace.Worker,
            PositionKey = p == null ? null : "pdn:" + p.Fen,
            BestMove = r?.BestMove,
            Error = error,
        });
}

[ApiController]
[Route("v1/position")]
[Produces("application/json")]
public sealed class PositionController : ControllerBase
{
    readonly MoveService _moves;

    public PositionController(MoveService moves) => _moves = moves;

    /// <summary>Legal moves of a position (used by the test board).</summary>
    [HttpPost("moves")]
    public IActionResult Moves([FromBody] PositionRequest? request)
    {
        try { return Ok(_moves.LegalMoves(request)); }
        catch (ApiException ex) { return StatusCode(ex.Status, new ErrorResponse(ex.Code, ex.Message, HttpContext.TraceIdentifier)); }
    }
}

[ApiController]
[Produces("application/json")]
public sealed class HealthController : ControllerBase
{
    readonly EnginePool _pool;

    public HealthController(EnginePool pool) => _pool = pool;

    /// <summary>
    /// 200 with ok=true while at least one engine worker is ready (degraded=true if some are restarting),
    /// 503 with ok=false when none is. Add ?details=true for engine name, databases and per-worker state.
    /// </summary>
    [HttpGet("/healthz")]
    public IActionResult Get([FromQuery] bool details = false)
    {
        int ready = _pool.ReadyCount;
        bool ok = ready > 0;
        object body;
        if (details)
            body = new { ok, workers = ready, configured = _pool.Configured, degraded = ready < _pool.Configured, engine = _pool.EngineName, tablebasePieces = _pool.TablebasePieces, pool = _pool.Describe() };
        else if (ready < _pool.Configured)
            body = new { ok, workers = ready, degraded = true };
        else
            body = new { ok, workers = ready };
        return StatusCode(ok ? 200 : 503, body);
    }
}
