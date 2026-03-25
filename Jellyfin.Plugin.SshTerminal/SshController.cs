using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SshTerminal;

[ApiController]
[Authorize]
public class SshController : ControllerBase
{
    private readonly ILogger<SshController> _logger;

    public SshController(ILogger<SshController> logger)
    {
        _logger = logger;
    }

    [HttpGet("SshTerminal/Socket")]
    public async Task<ActionResult> WebSocketEndpoint()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return BadRequest("WebSocket connections only.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrEmpty(config.SshHost)
            || string.IsNullOrEmpty(config.SshUsername))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                "SSH Terminal not configured. Set host and username in plugin settings.");
        }

        var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var handler = new SshWebSocketHandler(_logger);

        await handler.HandleAsync(webSocket, HttpContext.RequestAborted).ConfigureAwait(false);

        return new EmptyResult();
    }

    [HttpGet("SshTerminal/Status")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var configured = config != null
            && !string.IsNullOrEmpty(config.SshHost)
            && !string.IsNullOrEmpty(config.SshUsername);

        return Ok(new
        {
            configured,
            host = configured ? config!.SshHost : null,
            port = configured ? config!.SshPort : 0
        });
    }
}
