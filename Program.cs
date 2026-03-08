using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using TeamsBot;
using TeamsBot.Bot;
using TeamsBot.Media;
using TeamsBot.Meetings;
using TeamsBot.Transcription;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration overrides from environment variables ────────────────────────
// NSSM injects each env var; they take precedence over appsettings.json.
builder.Configuration.AddEnvironmentVariables();

// ── Services ──────────────────────────────────────────────────────────────────

// Singletons that live for the process lifetime
builder.Services.AddSingleton<MeetingStore>();
builder.Services.AddSingleton<SpeechTranscriber>();
builder.Services.AddSingleton<GraphCallsService>();
builder.Services.AddSingleton<MediaPlatformService>();

// Bot Framework
builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();
builder.Services.AddTransient<IBot, TeamsActivityBot>();

// HttpClient factory — used by GraphCallsService
builder.Services.AddHttpClient("Graph");

// Controllers (BotController, CallsController, MeetingsController)
builder.Services.AddControllers();

// ── Build app ─────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Initialize media platform BEFORE accepting any requests ───────────────────
var mediaPlatform = app.Services.GetRequiredService<MediaPlatformService>();
await mediaPlatform.InitializeAsync();

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseStaticFiles(); // serves wwwroot/dashboard.html etc.

// ── Routes ────────────────────────────────────────────────────────────────────
app.MapControllers();

// Health check
app.MapGet("/health", () => Results.Ok(new
{
    status      = "healthy",
    version     = "2.0.0",
    runtime     = "dotnet8",
    environment = builder.Environment.EnvironmentName,
}));

// Dashboard — serves wwwroot/dashboard.html
app.MapGet("/dashboard", async context =>
{
    var file = Path.Combine(app.Environment.WebRootPath, "dashboard.html");
    if (!File.Exists(file))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("dashboard.html not found in wwwroot/");
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(file);
});

// WebSocket — live transcript push to dashboard
// Path: /ws/live/{meetingId}
app.Map("/ws/live/{meetingId}", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection required");
        return;
    }

    var meetingId = context.Request.RouteValues["meetingId"]?.ToString() ?? "";
    var store     = context.RequestServices.GetRequiredService<MeetingStore>();
    var ws        = await context.WebSockets.AcceptWebSocketAsync();

    await store.HandleWebSocketAsync(meetingId, ws, context.RequestAborted);
});

// ── Debug status endpoint ─────────────────────────────────────────────────────
app.MapGet("/debug/status", (MeetingStore store, MediaPlatformService _) => Results.Ok(new
{
    time_utc        = DateTime.UtcNow.ToString("u"),
    active_meetings = store.GetAllMeetings(),
}));

app.Logger.LogInformation("Teams Bot starting up — {Env}", builder.Environment.EnvironmentName);

app.Run();
