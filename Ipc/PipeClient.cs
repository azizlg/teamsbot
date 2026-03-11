using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamsBot.Meetings;

namespace TeamsBot.Ipc;

/// <summary>
/// Named pipe client that runs as a background hosted service in TeamsBot.Web.
/// Connects to the TeamsBot.Media pipe server and reads transcript segments
/// (newline-delimited JSON), pushing each into MeetingStore for storage
/// and WebSocket broadcast.
/// </summary>
public sealed class PipeClient : BackgroundService
{
    private const string PipeName = "teamsbot-transcript";

    private readonly MeetingStore _store;
    private readonly ILogger<PipeClient> _logger;

    public PipeClient(MeetingStore store, ILogger<PipeClient> logger)
    {
        _store  = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PipeClient] Starting — will connect to pipe '{Pipe}'", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);

                _logger.LogInformation("[PipeClient] Connecting to pipe...");
                await pipe.ConnectAsync(stoppingToken);
                _logger.LogInformation("[PipeClient] Connected to TeamsBot.Media pipe");

                using var reader = new StreamReader(pipe, Encoding.UTF8);

                while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line is null) break; // EOF — pipe closed

                    ProcessSegment(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PipeClient] Pipe disconnected, retrying in 2s...");
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(2000, stoppingToken);
        }

        _logger.LogInformation("[PipeClient] Stopped");
    }

    private void ProcessSegment(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var meetingId  = root.GetProperty("meetingId").GetString() ?? "";
            var text       = root.GetProperty("text").GetString() ?? "";
            var speakerId  = root.TryGetProperty("speakerId", out var sp) ? sp.GetString() : null;
            var language   = root.TryGetProperty("language", out var lang) ? lang.GetString() ?? "en-US" : "en-US";
            var confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.9;
            var timestamp  = root.TryGetProperty("timestamp", out var ts)
                ? DateTimeOffset.Parse(ts.GetString()!).ToUnixTimeMilliseconds() / 1000.0
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            if (string.IsNullOrWhiteSpace(meetingId) || string.IsNullOrWhiteSpace(text))
                return;

            var segment = new TranscriptSegment(
                MeetingId:  meetingId,
                Text:       text,
                Language:   language,
                Timestamp:  timestamp,
                Confidence: confidence,
                SpeakerId:  speakerId
            );

            _store.AddSegment(segment);
            _logger.LogInformation("[PipeClient] [{MeetingId}] {Speaker}: {Text}",
                meetingId, speakerId ?? "?", text);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[PipeClient] Failed to parse segment JSON");
        }
    }
}
