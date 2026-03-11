using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TeamsBot.Bot;
using TeamsBot.Meetings;
using TeamsBot.Transcription;

namespace TeamsBot.Bot;

/// <summary>
/// Teams bot activity handler.
/// Listens for messages containing a Teams meeting URL and triggers the join flow.
/// </summary>
public sealed class TeamsActivityBot : TeamsActivityHandler
{
    private static readonly System.Text.RegularExpressions.Regex _meetingUrlRe =
        new(@"https://teams\.microsoft\.com/(l/meetup-join|meet)/[^\s""'>]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly GraphCallsService _graph;
    private readonly SpeechTranscriber _transcriber;
    private readonly MeetingStore _store;
    private readonly IConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TeamsActivityBot> _logger;

    public TeamsActivityBot(
        GraphCallsService graph,
        SpeechTranscriber transcriber,
        MeetingStore store,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        ILogger<TeamsActivityBot> logger)
    {
        _graph       = graph;
        _transcriber = transcriber;
        _store       = store;
        _config      = config;
        _loggerFactory = loggerFactory;
        _logger      = logger;
    }

    protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken ct)
    {
        var text = turnContext.Activity.Text?.Trim() ?? "";

        // Look for a Teams meeting URL in the message
        var match = _meetingUrlRe.Match(text);
        if (!match.Success)
        {
            await turnContext.SendActivityAsync(
                "Send me a Teams meeting URL to join. Example:\n" +
                "`https://teams.microsoft.com/l/meetup-join/...`",
                cancellationToken: ct);
            return;
        }

        var joinUrl   = match.Value;
        var meetingId = Guid.NewGuid().ToString("N")[..12];

        await turnContext.SendActivityAsync($"⏳ Joining meeting `{meetingId}`...", cancellationToken: ct);

        try
        {
            var callbackUri = _config["TunnelUrl"]!.TrimEnd('/');

            // Fetch the appHostedMediaConfig blob from TeamsBot.Media (RMP SDK process on port 8446).
            // This MUST succeed — never fall back to serviceHostedMediaConfig (blank blob),
            // because that gives the bot zero audio frames and thus no transcription.
            var blob = await FetchMediaBlobAsync(meetingId, ct);

            // Extract organizer tenant from Teams channelData
            var organizerTenant = (turnContext.Activity.ChannelData as JObject)
                ?["tenant"]?["id"]?.ToString();

            // 2. Register meeting in store
            _store.StartMeeting(meetingId, joinUrl);

            // 3. Join via Graph
            var callId = await _graph.JoinMeetingAsync(joinUrl, meetingId, blob, callbackUri, organizerTenant, ct);

            _store.UpdateMeetingStatus(meetingId, "active", callId);

            await turnContext.SendActivityAsync(
                $"✅ Joined! call_id=`{callId}`\n" +
                $"Live transcript → {callbackUri}/dashboard",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join meeting {MeetingId}", meetingId);
            _store.UpdateMeetingStatus(meetingId, "error");
            await turnContext.SendActivityAsync(
                $"❌ Failed to join: {ex.Message[..Math.Min(ex.Message.Length, 200)]}",
                cancellationToken: ct);
        }
    }

    protected override async Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken ct)
    {
        foreach (var member in membersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(
                    "👋 Hi! I'm the Meeting Transcription Bot. " +
                    "Send me a Teams meeting URL and I'll join and transcribe it in real time.",
                    cancellationToken: ct);
            }
        }
    }

    // ── media blob helper ─────────────────────────────────────────────────────

    private static readonly System.Net.Http.HttpClient _mediaHttp =
        new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private async Task<string> FetchMediaBlobAsync(string meetingId, CancellationToken ct)
    {
        var uri = $"http://localhost:8446/session/{Uri.EscapeDataString(meetingId)}";
        _logger.LogInformation("Requesting media blob from {Uri}", uri);

        System.Net.Http.HttpResponseMessage resp;
        try
        {
            resp = await _mediaHttp.PostAsync(uri, new System.Net.Http.StringContent(""), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError("TeamsBot.Media is unreachable at {Uri}: {Error}", uri, ex.Message);
            throw new InvalidOperationException(
                $"TeamsBot.Media is unreachable (port 8446). Ensure TeamsBotMedia service is running. Error: {ex.Message}", ex);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("MediaApiServer returned {Status} for {MeetingId}: {Error}",
                resp.StatusCode, meetingId, err);
            throw new InvalidOperationException(
                $"MediaApiServer returned HTTP {(int)resp.StatusCode} for meeting {meetingId}: {err}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var blob = doc.RootElement.GetProperty("blob").GetString() ?? string.Empty;

        if (string.IsNullOrEmpty(blob))
        {
            _logger.LogError("MediaApiServer returned empty blob for {MeetingId}", meetingId);
            throw new InvalidOperationException(
                "MediaApiServer returned an empty blob. Check TeamsBotMedia logs.");
        }

        _logger.LogInformation("MediaApiServer returned blob ({Chars} chars) for {MeetingId}", blob.Length, meetingId);
        return blob;
    }
}
