using System.Text.Json.Serialization;

namespace CheckersApi;

// All request properties are nullable so that missing fields reach our own validation (422 with a clear message)
// instead of the framework's automatic 400.

public sealed class SuggestRequest
{
    public string? GameId { get; set; }
    public GameState? State { get; set; }
    public string? Level { get; set; }
    public RequestLimits? Limits { get; set; }
}

public sealed class GameState
{
    public string? Notation { get; set; }
    public string? Position { get; set; }
}

public sealed class RequestLimits
{
    public int? MaxDepth { get; set; }
    public int? SoftTimeMs { get; set; }
    public int? HardTimeMs { get; set; }
}

public sealed class ValidateRequest
{
    public string? Position { get; set; }
    public string? Move { get; set; }
}

public sealed class PositionRequest
{
    public string? Position { get; set; }
}

public sealed record SuggestResponse
{
    [JsonPropertyName("engine")] public required string Engine { get; init; }
    [JsonPropertyName("bestMove")] public string? BestMove { get; init; }
    [JsonPropertyName("pv")] public required IReadOnlyList<string> Pv { get; init; }
    /// <summary>Engine score for the side to move (number), or "WIN"/"LOSS"/"DRAW" for a database position.</summary>
    [JsonPropertyName("scoreOrWDL")] public object? ScoreOrWdl { get; init; }
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("nodes")] public long Nodes { get; init; }
    [JsonPropertyName("positionKey")] public required string PositionKey { get; init; }
    [JsonPropertyName("info")] public required SuggestInfo Info { get; init; }
}

public sealed record SuggestInfo
{
    [JsonPropertyName("tablebaseHit")] public bool TablebaseHit { get; init; }
    [JsonPropertyName("timeMs")] public long TimeMs { get; init; }
    [JsonPropertyName("cached")] public bool Cached { get; init; }
    [JsonPropertyName("level")] public string? Level { get; init; }
    [JsonPropertyName("softTimeMs")] public int SoftTimeMs { get; init; }
    [JsonPropertyName("maxDepth")] public int MaxDepth { get; init; }
    [JsonPropertyName("book")] public bool Book { get; init; }
    [JsonPropertyName("gameOver")] public bool GameOver { get; init; }
    [JsonPropertyName("worker")] public int? Worker { get; init; }
}

public sealed record ValidateResponse(
    [property: JsonPropertyName("legal")] bool Legal,
    [property: JsonPropertyName("move"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Move = null,
    [property: JsonPropertyName("positionAfter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PositionAfter = null,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requestId")] string RequestId);

/// <summary>An error with an HTTP status, turned into an <see cref="ErrorResponse"/> by the controllers.</summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }
    public string Code { get; }

    public static ApiException Unprocessable(string code, string message) => new(StatusCodes.Status422UnprocessableEntity, code, message);
}
