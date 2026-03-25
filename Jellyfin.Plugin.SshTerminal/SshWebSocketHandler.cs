using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Jellyfin.Plugin.SshTerminal;

public class SshWebSocketHandler : IDisposable
{
    private readonly ILogger _logger;
    private readonly SshClient _sshClient;
    private ShellStream? _shellStream;
    private bool _disposed;

    public SshWebSocketHandler(ILogger logger)
    {
        _logger = logger;

        var config = Plugin.Instance!.Configuration;

        ConnectionInfo connectionInfo;
        if (config.AuthMethod == "privatekey" && !string.IsNullOrEmpty(config.SshPrivateKey))
        {
            var keyBytes = Encoding.UTF8.GetBytes(config.SshPrivateKey);
            using var keyStream = new System.IO.MemoryStream(keyBytes);
            var keyFile = new PrivateKeyFile(keyStream);
            connectionInfo = new ConnectionInfo(
                config.SshHost,
                config.SshPort,
                config.SshUsername,
                new PrivateKeyAuthenticationMethod(config.SshUsername, keyFile));
        }
        else
        {
            connectionInfo = new ConnectionInfo(
                config.SshHost,
                config.SshPort,
                config.SshUsername,
                new PasswordAuthenticationMethod(config.SshUsername, config.SshPassword));
        }

        _sshClient = new SshClient(connectionInfo);
        _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    public async Task HandleAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            _sshClient.Connect();
            _shellStream = _sshClient.CreateShellStream(
                Plugin.Instance!.Configuration.TerminalType,
                80, 24, 0, 0, 4096);

            _logger.LogInformation("SSH connection established to {Host}", Plugin.Instance.Configuration.SshHost);

            var sshToWsTask = SshToWebSocketLoop(webSocket, cancellationToken);
            var wsToSshTask = WebSocketToSshLoop(webSocket, cancellationToken);

            await Task.WhenAny(sshToWsTask, wsToSshTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH session error");

            if (webSocket.State == WebSocketState.Open)
            {
                var errorBytes = Encoding.UTF8.GetBytes($"\r\n[SSH Error: {ex.Message}]\r\n");
                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorBytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Dispose();
        }
    }

    private async Task SshToWebSocketLoop(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested
               && webSocket.State == WebSocketState.Open
               && _sshClient.IsConnected)
        {
            var bytesRead = await _shellStream!.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead > 0)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, bytesRead),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WebSocketToSshLoop(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested
               && webSocket.State == WebSocketState.Open
               && _sshClient.IsConnected)
        {
            var result = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                _shellStream!.Write(buffer, 0, result.Count);
                _shellStream.Flush();
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleControlMessage(json);
            }
        }
    }

    private void HandleControlMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "resize"
                && root.TryGetProperty("cols", out var cols)
                && root.TryGetProperty("rows", out var rows))
            {
                var c = (uint)cols.GetInt32();
                var r = (uint)rows.GetInt32();
                _shellStream?.ChangeWindowSize(c, r, 0, 0);
                _logger.LogDebug("Terminal resized to {Cols}x{Rows}", c, r);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid control message: {Json}", json);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shellStream?.Dispose();

        if (_sshClient.IsConnected)
        {
            _sshClient.Disconnect();
        }

        _sshClient.Dispose();

        _logger.LogInformation("SSH session cleaned up");
    }
}
