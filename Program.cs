using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamsBot;
using TeamsBot.Bot;
using TeamsBot.Ipc;
using TeamsBot.Logging;
using TeamsBot.Meetings;
using TeamsBot.Transcription;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Configuration.AddEnvironmentVariables();

// Services
var logBuffer = new InMemoryLogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new InMemoryLoggerProvider(logBuffer));
builder.Services.AddSingleton<MeetingStore>();
builder.Services.AddSingleton<SpeechTranscriber>();
builder.Services.AddSingleton<GraphCallsService>();
builder.Services.AddHostedService<PipeClient>();

builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();
builder.Services.AddTransient<IBot, TeamsActivityBot>();

builder.Services.AddHttpClient("Graph");

builder.Services.AddControllers();

var app = builder.Build();

// RMP media platform runs in separate TeamsBot.Media process (.NET Framework 4.8).
// Transcript segments arrive via named pipe (PipeClient hosted service above).

// Middleware
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseStaticFiles();

app.MapControllers();

// Health check
app.MapGet("/health", (IHostEnvironment env) =>
{
    return Results.Ok(new
    {
        status = "healthy",
        version = "2.0.0",
        runtime = ".NET 8",
        environment = env.EnvironmentName
    });
});

// Dashboard
app.MapGet("/dashboard", async (HttpContext context) =>
{
    var webRoot = app.Environment.WebRootPath;

    if (string.IsNullOrWhiteSpace(webRoot))
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("WebRootPath is not configured.");
        return;
    }

    var file = Path.Combine(webRoot, "dashboard.html");

    if (!File.Exists(file))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("dashboard.html not found in wwwroot/");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(file);
});

// WebSocket endpoint
app.Map("/ws/live/{meetingId}", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connection required");
        return;
    }

    var meetingId = context.Request.RouteValues["meetingId"]?.ToString();

    if (string.IsNullOrWhiteSpace(meetingId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("meetingId is required");
        return;
    }

    var store = context.RequestServices.GetRequiredService<MeetingStore>();
    var socket = await context.WebSockets.AcceptWebSocketAsync();

    await store.HandleWebSocketAsync(meetingId, socket, context.RequestAborted);
});

// Debug status
app.MapGet("/debug/status", (MeetingStore store, IConfiguration config, IHostEnvironment env) =>
{
    var allMeetings = store.GetAllMeetings();
    var meetingsDict = allMeetings.ToDictionary(
        m => m.MeetingId,
        m => new
        {
            joined_via          = "graph",
            status              = m.Status,
            transcript_segments = m.SegmentCount,
            duration_s          = (DateTime.UtcNow - m.StartedAt).TotalSeconds,
            engine              = "speech",
            has_speech_client   = false
        });
    return Results.Ok(new
    {
        time_utc              = DateTime.UtcNow.ToString("u"),
        environment           = env.EnvironmentName,
        total_active_meetings = allMeetings.Count(m => m.Status is "active" or "joining"),
        recent_errors_5min    = 0,
        meeting_tenant_id     = config["MeetingTenantId"],
        clients               = new { graph_client = true },
        speech_config         = new { azure_speech_configured = !string.IsNullOrEmpty(config["AzureSpeechKey"]) },
        meetings              = meetingsDict
    });
});

// Debug logs
app.MapGet("/debug/logs", (InMemoryLogBuffer buf, int? last_n, string? level) =>
{
    var n = last_n ?? 300;
    var entries = buf.GetLast(n);
    var filtered = string.IsNullOrEmpty(level) || level == "DEBUG"
        ? entries
        : entries.Where(e => e.Level != "DEBUG").ToList();
    return Results.Ok(new
    {
        entries = filtered.Select(e => new
        {
            timestamp = e.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
            levelname = e.Level,
            name      = e.Category,
            message   = e.Message
        })
    });
});

app.Logger.LogInformation("Teams Bot starting up — {Env}", app.Environment.EnvironmentName);

// Crash-safety: save all recordings when the process is shutting down
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var store      = app.Services.GetRequiredService<MeetingStore>();
    var transcriber = app.Services.GetRequiredService<SpeechTranscriber>();

    app.Logger.LogWarning("Application stopping — flushing active recordings...");
    try { store.SaveAllActiveTranscriptsAsync().GetAwaiter().GetResult(); } catch { }
    try { transcriber.SaveAllActiveAudioAsync().GetAwaiter().GetResult(); } catch { }
    app.Logger.LogWarning("Recording flush complete.");
});

await app.RunAsync();