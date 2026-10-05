using System.Text.Json;
using CheckersApi;
using CheckersApi.Engine;
using CheckersApi.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line (IIS captures stdout when stdoutLogEnabled is on in web.config).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

var config = builder.Configuration;
builder.Services.Configure<EngineOptions>(config.GetSection("Engine"));
builder.Services.Configure<CacheOptions>(config.GetSection("Cache"));
builder.Services.Configure<LimitsOptions>(config.GetSection("Limits"));
builder.Services.Configure<TablebaseOptions>(config.GetSection("Tablebase"));
builder.Services.Configure<RequestLogOptions>(config.GetSection("RequestLog"));
builder.Services.Configure<Dictionary<string, LevelOptions>>(config.GetSection("Levels"));
builder.Services.PostConfigure<Dictionary<string, LevelOptions>>(levels =>
{
    // Defaults from the spec, used when appsettings.json has no Levels section.
    if (levels.Count > 0) return;
    levels["weak"] = new LevelOptions { SoftTimeMs = 100, MaxDepth = 8 };
    levels["medium"] = new LevelOptions { SoftTimeMs = 250, MaxDepth = 12 };
    levels["strong"] = new LevelOptions { SoftTimeMs = 450, MaxDepth = 18 };
});

builder.Services.AddSingleton<EnginePool>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EnginePool>());
builder.Services.AddSingleton<MoveService>();
builder.Services.AddSingleton<RequestLog>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// Unreadable JSON bodies get our error shape too.
builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ctx =>
{
    string message = string.Join(" ", ctx.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)).Trim();
    return new BadRequestObjectResult(new ErrorResponse("bad_request",
        message.Length > 0 ? message : "The request body is not valid JSON.", ctx.HttpContext.TraceIdentifier));
});

string[] corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Short request id, returned in X-Request-Id and in every log line and error body.
app.Use(async (ctx, next) =>
{
    string id = ctx.Request.Headers.TryGetValue("X-Request-Id", out var given) && given.ToString().Length is > 0 and <= 64
        ? given.ToString()
        : Guid.NewGuid().ToString("N")[..12];
    ctx.TraceIdentifier = id;
    ctx.Response.Headers["X-Request-Id"] = id;
    await next();
});

if (corsOrigins.Length > 0) app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

public partial class Program { }
