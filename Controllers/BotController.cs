using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace TeamsBot.Controllers;

/// <summary>
/// Receives Bot Framework activity messages from Azure Bot Service.
/// Path: POST /api/messages
/// </summary>
[ApiController]
[Route("api/messages")]
public sealed class BotController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly Microsoft.Bot.Builder.IBot _bot;

    public BotController(IBotFrameworkHttpAdapter adapter, Microsoft.Bot.Builder.IBot bot)
    {
        _adapter = adapter;
        _bot     = bot;
    }

    [HttpPost]
    public async Task PostAsync(CancellationToken ct) =>
        await _adapter.ProcessAsync(Request, Response, _bot, ct);
}
