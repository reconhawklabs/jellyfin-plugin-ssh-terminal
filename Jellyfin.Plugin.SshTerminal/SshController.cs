using System;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
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

    /// <summary>
    /// Creates a new SSH session. Returns the session ID.
    /// </summary>
    [HttpPost("SshTerminal/Connect")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult Connect()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null
            || string.IsNullOrEmpty(config.SshHost)
            || string.IsNullOrEmpty(config.SshUsername))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "SSH Terminal not configured. Set host and username in plugin settings." });
        }

        try
        {
            var session = SshSessionManager.CreateSession(_logger);
            return Ok(new { sessionId = session.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SSH session");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = $"SSH connection failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Streams SSH output as Server-Sent Events.
    /// </summary>
    [HttpGet("SshTerminal/Stream")]
    public async Task Stream([FromQuery] string sessionId)
    {
        var session = SshSessionManager.GetSession(sessionId);
        if (session == null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var cancellation = HttpContext.RequestAborted;

        try
        {
            while (!cancellation.IsCancellationRequested && session.IsConnected)
            {
                var data = await session.ReadOutputAsync(cancellation).ConfigureAwait(false);

                if (data.Length > 0)
                {
                    var base64 = Convert.ToBase64String(data);
                    await Response.WriteAsync($"data: {base64}\n\n", cancellation).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }
            }

            // Send disconnect event
            await Response.WriteAsync("event: disconnected\ndata: closed\n\n", cancellation).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH stream error for session {Id}", sessionId);
            try
            {
                await Response.WriteAsync($"event: error\ndata: {ex.Message}\n\n", cancellation).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
            }
            catch
            {
                // Client already gone
            }
        }
    }

    /// <summary>
    /// Sends input to the SSH session.
    /// </summary>
    [HttpPost("SshTerminal/Input")]
    public ActionResult Input([FromBody] SshInputRequest request)
    {
        var session = SshSessionManager.GetSession(request.SessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found or disconnected." });
        }

        try
        {
            var data = Convert.FromBase64String(request.Data);
            session.WriteInput(data);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH input error for session {Id}", request.SessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resizes the SSH terminal.
    /// </summary>
    [HttpPost("SshTerminal/Resize")]
    public ActionResult Resize([FromBody] SshResizeRequest request)
    {
        var session = SshSessionManager.GetSession(request.SessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found or disconnected." });
        }

        session.Resize((uint)request.Cols, (uint)request.Rows);
        return Ok();
    }

    /// <summary>
    /// Disconnects an SSH session.
    /// </summary>
    [HttpPost("SshTerminal/Disconnect")]
    public ActionResult Disconnect([FromBody] SshSessionRequest request)
    {
        SshSessionManager.RemoveSession(request.SessionId);
        return Ok();
    }

    /// <summary>
    /// Returns plugin configuration status.
    /// </summary>
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

public class SshInputRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;
}

public class SshResizeRequest
{
    public string SessionId { get; set; } = string.Empty;

    public int Cols { get; set; }

    public int Rows { get; set; }
}

public class SshSessionRequest
{
    public string SessionId { get; set; } = string.Empty;
}
