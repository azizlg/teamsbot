using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;

namespace TeamsBot;

/// <summary>
/// Bot Framework adapter that adds centralized error handling.
/// Catches any unhandled exception in the bot pipeline and sends a user-friendly message.
/// </summary>
public sealed class AdapterWithErrorHandler : CloudAdapter
{
    public AdapterWithErrorHandler(
        BotFrameworkAuthentication auth,
        ILogger<CloudAdapter> logger)
        : base(auth, logger)
    {
        OnTurnError = async (turnContext, ex) =>
        {
            logger.LogError(ex, "Unhandled exception in bot turn");
            await turnContext.SendActivityAsync("⚠️ Something went wrong. Please try again.");
        };
    }
}
