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

            // Joining with serviceHostedMediaConfig — Graph manages the media server.
            // Real-time audio (and thus real-time transcription) requires the RMP SDK
            // which only supports .NET Framework 4.x. To enable it, retarget to net48.
            var blob = string.Empty; // unused with serviceHostedMediaConfig

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
}
