using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Jellyfin.Plugin.SshTerminal;

public class SshSession : IDisposable
{
    private readonly SshClient _sshClient;
    private readonly ShellStream _shellStream;
    private readonly ILogger _logger;
    private bool _disposed;

    public string Id { get; }

    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    public bool IsConnected => !_disposed && _sshClient.IsConnected;

    public SshSession(string id, SshClient client, ShellStream shellStream, ILogger logger)
    {
        Id = id;
        _sshClient = client;
        _shellStream = shellStream;
        _logger = logger;
    }

    public async Task<byte[]> ReadOutputAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
            .ConfigureAwait(false);

        if (bytesRead > 0)
        {
            LastActivity = DateTime.UtcNow;
            var result = new byte[bytesRead];
            Array.Copy(buffer, result, bytesRead);
            return result;
        }

        return Array.Empty<byte>();
    }

    public void WriteInput(byte[] data)
    {
        _shellStream.Write(data, 0, data.Length);
        _shellStream.Flush();
        LastActivity = DateTime.UtcNow;
    }

    public void Resize(uint cols, uint rows)
    {
        _shellStream.ChangeWindowSize(cols, rows, 0, 0);
        _logger.LogDebug("Terminal resized to {Cols}x{Rows}", cols, rows);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shellStream.Dispose();

        if (_sshClient.IsConnected)
        {
            _sshClient.Disconnect();
        }

        _sshClient.Dispose();
        _logger.LogInformation("SSH session {Id} cleaned up", Id);
    }
}

public static class SshSessionManager
{
    private static readonly ConcurrentDictionary<string, SshSession> Sessions = new();

    public static SshSession CreateSession(ILogger logger)
    {
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

        var client = new SshClient(connectionInfo);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        client.Connect();

        var shellStream = client.CreateShellStream(
            config.TerminalType, 80, 24, 0, 0, 4096);

        var sessionId = Guid.NewGuid().ToString("N");
        var session = new SshSession(sessionId, client, shellStream, logger);
        Sessions[sessionId] = session;

        logger.LogInformation("SSH session {Id} created to {Host}", sessionId, config.SshHost);
        return session;
    }

    public static SshSession? GetSession(string sessionId)
    {
        Sessions.TryGetValue(sessionId, out var session);
        if (session != null && !session.IsConnected)
        {
            RemoveSession(sessionId);
            return null;
        }

        return session;
    }

    public static void RemoveSession(string sessionId)
    {
        if (Sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
        }
    }
}
