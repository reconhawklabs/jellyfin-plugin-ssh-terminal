# Jellyfin SSH Plugin — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a Jellyfin plugin that provides web-based SSH terminal access through the Jellyfin UI, installable via a custom plugin repository hosted on GitHub.

**Architecture:** xterm.js in the browser connects via WebSocket to an ASP.NET Core controller inside the plugin. The controller bridges the WebSocket to an SSH.NET `ShellStream` connected to the target host. All auth flows through Jellyfin's existing session system.

**Tech Stack:** C# / .NET 8.0, Jellyfin 10.10.x plugin API, SSH.NET (Renci.SshNet), xterm.js (CDN), GitHub Releases for distribution.

---

## Task 1: Project Scaffold

**Files:**
- Create: `Jellyfin.Plugin.SshTerminal/Jellyfin.Plugin.SshTerminal.csproj`
- Create: `Jellyfin.Plugin.SshTerminal/Plugin.cs`
- Create: `Jellyfin.Plugin.SshTerminal/PluginConfiguration.cs`

**Step 1: Create the .csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>Jellyfin.Plugin.SshTerminal</RootNamespace>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.10.*" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="10.10.*" ExcludeAssets="runtime" />
    <PackageReference Include="SSH.NET" Version="2025.1.0" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Configuration\configPage.html" />
    <EmbeddedResource Include="Web\terminal.html" />
  </ItemGroup>

</Project>
```

**Step 2: Create Plugin.cs**

```csharp
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SshTerminal;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "SSH Terminal";

    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public override string Description => "Web-based SSH terminal access through Jellyfin.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "SshTerminalConfig",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "SshTerminal",
                EmbeddedResourcePath = GetType().Namespace + ".Web.terminal.html",
                EnableInMainMenu = true,
                DisplayName = "SSH Terminal",
                MenuSection = "admin",
                MenuIcon = "terminal"
            }
        };
    }
}
```

**Step 3: Create PluginConfiguration.cs**

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SshTerminal;

public class PluginConfiguration : BasePluginConfiguration
{
    public string SshHost { get; set; } = "127.0.0.1";

    public int SshPort { get; set; } = 22;

    public string SshUsername { get; set; } = string.Empty;

    public string AuthMethod { get; set; } = "password";

    public string SshPassword { get; set; } = string.Empty;

    public string SshPrivateKey { get; set; } = string.Empty;

    public string TerminalType { get; set; } = "xterm-256color";
}
```

**Step 4: Verify build**

```bash
cd Jellyfin.Plugin.SshTerminal
dotnet restore
dotnet build
```

Expected: Build succeeds with 0 errors.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: scaffold Jellyfin SSH Terminal plugin project"
```

---

## Task 2: SSH WebSocket Handler

**Files:**
- Create: `Jellyfin.Plugin.SshTerminal/SshWebSocketHandler.cs`

This is the core logic — bridges a WebSocket to an SSH ShellStream.

**Step 1: Create SshWebSocketHandler.cs**

```csharp
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
                _shellStream?.SendWindowChangeRequest(c, r, 0, 0);
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
```

**Step 2: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

**Step 3: Commit**

```bash
git add Jellyfin.Plugin.SshTerminal/SshWebSocketHandler.cs
git commit -m "feat: add SSH-to-WebSocket bridge handler"
```

---

## Task 3: ASP.NET Controller

**Files:**
- Create: `Jellyfin.Plugin.SshTerminal/SshController.cs`
- Create: `Jellyfin.Plugin.SshTerminal/ServiceRegistrator.cs`

**Step 1: Create SshController.cs**

```csharp
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
```

**Step 2: Create ServiceRegistrator.cs**

```csharp
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SshTerminal;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Controllers are auto-discovered from plugin assemblies.
        // This registrator exists for future service registration needs.
    }
}
```

**Step 3: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

**Step 4: Commit**

```bash
git add Jellyfin.Plugin.SshTerminal/SshController.cs Jellyfin.Plugin.SshTerminal/ServiceRegistrator.cs
git commit -m "feat: add SSH WebSocket controller and service registrator"
```

---

## Task 4: Configuration Page (HTML)

**Files:**
- Create: `Jellyfin.Plugin.SshTerminal/Configuration/configPage.html`

**Step 1: Create the config page**

```html
<!DOCTYPE html>
<html>
<head>
    <title>SSH Terminal</title>
</head>
<body>
    <div id="SshTerminalConfigPage" data-role="page" class="page type-interior pluginConfigurationPage"
         data-require="emby-input,emby-button,emby-select">

        <div data-role="content">
            <div class="content-primary">
                <h2>SSH Terminal Settings</h2>
                <p>Configure the SSH target host for remote terminal access.</p>

                <div class="inputContainer">
                    <label class="inputLabel" for="txtSshHost">SSH Host</label>
                    <input is="emby-input" type="text" id="txtSshHost" placeholder="192.168.1.100" />
                    <div class="fieldDescription">IP address or hostname of the SSH target.</div>
                </div>

                <div class="inputContainer">
                    <label class="inputLabel" for="txtSshPort">SSH Port</label>
                    <input is="emby-input" type="number" id="txtSshPort" placeholder="22" />
                </div>

                <div class="inputContainer">
                    <label class="inputLabel" for="txtSshUsername">Username</label>
                    <input is="emby-input" type="text" id="txtSshUsername" />
                </div>

                <div class="inputContainer">
                    <label class="inputLabel" for="selAuthMethod">Authentication Method</label>
                    <select is="emby-select" id="selAuthMethod">
                        <option value="password">Password</option>
                        <option value="privatekey">Private Key</option>
                    </select>
                </div>

                <div class="inputContainer" id="passwordContainer">
                    <label class="inputLabel" for="txtSshPassword">Password</label>
                    <input is="emby-input" type="password" id="txtSshPassword" />
                </div>

                <div class="inputContainer" id="privateKeyContainer" style="display:none;">
                    <label class="inputLabel" for="txtSshPrivateKey">Private Key (PEM format)</label>
                    <textarea id="txtSshPrivateKey" rows="6" style="width:100%; font-family:monospace; background:var(--theme-background); color:var(--theme-text-color); border:1px solid var(--theme-border-color); padding:8px;"></textarea>
                    <div class="fieldDescription">Paste your private key in PEM format (-----BEGIN ... -----)</div>
                </div>

                <div class="inputContainer">
                    <label class="inputLabel" for="txtTerminalType">Terminal Type</label>
                    <input is="emby-input" type="text" id="txtTerminalType" placeholder="xterm-256color" />
                </div>

                <div>
                    <button is="emby-button" type="button" class="raised button-submit block"
                            onclick="SshTerminalConfig.save();">
                        <span>Save</span>
                    </button>
                </div>

                <div style="margin-top:20px;">
                    <a is="emby-button" class="raised" href="web/ConfigurationPage?name=SshTerminal"
                       style="text-decoration:none;">
                        <span>Open SSH Terminal</span>
                    </a>
                </div>
            </div>
        </div>

        <script>
            var SshTerminalConfig = {
                pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',

                load: function () {
                    Dashboard.showLoadingMsg();
                    ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
                        document.getElementById('txtSshHost').value = config.SshHost || '';
                        document.getElementById('txtSshPort').value = config.SshPort || 22;
                        document.getElementById('txtSshUsername').value = config.SshUsername || '';
                        document.getElementById('selAuthMethod').value = config.AuthMethod || 'password';
                        document.getElementById('txtSshPassword').value = config.SshPassword || '';
                        document.getElementById('txtSshPrivateKey').value = config.SshPrivateKey || '';
                        document.getElementById('txtTerminalType').value = config.TerminalType || 'xterm-256color';
                        SshTerminalConfig.toggleAuthFields();
                        Dashboard.hideLoadingMsg();
                    });
                },

                toggleAuthFields: function () {
                    var method = document.getElementById('selAuthMethod').value;
                    document.getElementById('passwordContainer').style.display = method === 'password' ? '' : 'none';
                    document.getElementById('privateKeyContainer').style.display = method === 'privatekey' ? '' : 'none';
                },

                save: function () {
                    Dashboard.showLoadingMsg();
                    ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
                        config.SshHost = document.getElementById('txtSshHost').value;
                        config.SshPort = parseInt(document.getElementById('txtSshPort').value) || 22;
                        config.SshUsername = document.getElementById('txtSshUsername').value;
                        config.AuthMethod = document.getElementById('selAuthMethod').value;
                        config.SshPassword = document.getElementById('txtSshPassword').value;
                        config.SshPrivateKey = document.getElementById('txtSshPrivateKey').value;
                        config.TerminalType = document.getElementById('txtTerminalType').value || 'xterm-256color';
                        ApiClient.updatePluginConfiguration(SshTerminalConfig.pluginUniqueId, config).then(function () {
                            Dashboard.processPluginConfigurationUpdateResult();
                            Dashboard.hideLoadingMsg();
                        });
                    });
                }
            };

            document.getElementById('selAuthMethod').addEventListener('change', function () {
                SshTerminalConfig.toggleAuthFields();
            });

            document.addEventListener('viewshow', function () {
                SshTerminalConfig.load();
            });
        </script>
    </div>
</body>
</html>
```

**Step 2: Verify build (embedded resource picked up)**

```bash
dotnet build
```

Expected: Build succeeds, no warnings about missing embedded resources.

**Step 3: Commit**

```bash
git add Jellyfin.Plugin.SshTerminal/Configuration/configPage.html
git commit -m "feat: add plugin configuration page for SSH settings"
```

---

## Task 5: Terminal Page (HTML + xterm.js)

**Files:**
- Create: `Jellyfin.Plugin.SshTerminal/Web/terminal.html`

**Step 1: Create the terminal page**

```html
<!DOCTYPE html>
<html>
<head>
    <title>SSH Terminal</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css" />
    <style>
        #terminal-page {
            display: flex;
            flex-direction: column;
            height: 100vh;
            background: #1e1e1e;
        }
        #terminal-bar {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 8px 16px;
            background: #2d2d2d;
            color: #ccc;
            font-family: sans-serif;
            font-size: 14px;
        }
        #terminal-bar .status {
            display: flex;
            align-items: center;
            gap: 8px;
        }
        #terminal-bar .dot {
            width: 10px;
            height: 10px;
            border-radius: 50%;
            background: #f44;
        }
        #terminal-bar .dot.connected {
            background: #4c4;
        }
        #terminal-bar button {
            background: #444;
            color: #ccc;
            border: 1px solid #555;
            padding: 4px 12px;
            cursor: pointer;
            border-radius: 3px;
            font-size: 13px;
        }
        #terminal-bar button:hover {
            background: #555;
        }
        #terminal-container {
            flex: 1;
            padding: 4px;
        }
    </style>
</head>
<body>
    <div id="terminal-page">
        <div id="terminal-bar">
            <div class="status">
                <span class="dot" id="statusDot"></span>
                <span id="statusText">Disconnected</span>
            </div>
            <div>
                <button onclick="reconnect()">Reconnect</button>
                <button onclick="disconnect()">Disconnect</button>
            </div>
        </div>
        <div id="terminal-container"></div>
    </div>

    <script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js"></script>

    <script>
        var term = null;
        var ws = null;
        var fitAddon = null;

        function setStatus(connected, text) {
            var dot = document.getElementById('statusDot');
            var label = document.getElementById('statusText');
            dot.className = connected ? 'dot connected' : 'dot';
            label.textContent = text || (connected ? 'Connected' : 'Disconnected');
        }

        function getWebSocketUrl() {
            var loc = window.location;
            var proto = loc.protocol === 'https:' ? 'wss:' : 'ws:';
            var basePath = loc.pathname.substring(0, loc.pathname.indexOf('/web/') + 1);
            var apiKey = ApiClient.accessToken();
            return proto + '//' + loc.host + basePath + 'SshTerminal/Socket?api_key=' + encodeURIComponent(apiKey);
        }

        function connect() {
            if (ws && ws.readyState <= 1) {
                ws.close();
            }

            if (term) {
                term.dispose();
            }

            term = new Terminal({
                cursorBlink: true,
                fontSize: 14,
                fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
                theme: {
                    background: '#1e1e1e',
                    foreground: '#d4d4d4',
                    cursor: '#d4d4d4'
                },
                allowProposedApi: true
            });

            fitAddon = new FitAddon.FitAddon();
            term.loadAddon(fitAddon);

            var container = document.getElementById('terminal-container');
            while (container.firstChild) {
                container.removeChild(container.firstChild);
            }
            term.open(container);
            fitAddon.fit();

            term.write('Connecting to SSH...\r\n');
            setStatus(false, 'Connecting...');

            var url = getWebSocketUrl();
            ws = new WebSocket(url);
            ws.binaryType = 'arraybuffer';

            ws.onopen = function () {
                setStatus(true, 'Connected');
                term.clear();

                // Send initial terminal size
                var dims = fitAddon.proposeDimensions();
                if (dims) {
                    ws.send(JSON.stringify({
                        type: 'resize',
                        cols: dims.cols,
                        rows: dims.rows
                    }));
                }
            };

            ws.onmessage = function (event) {
                if (event.data instanceof ArrayBuffer) {
                    term.write(new Uint8Array(event.data));
                } else {
                    term.write(event.data);
                }
            };

            ws.onclose = function (event) {
                setStatus(false, 'Disconnected (code: ' + event.code + ')');
                term.write('\r\n\r\n[Connection closed]\r\n');
            };

            ws.onerror = function () {
                setStatus(false, 'Connection error');
            };

            // Send keystrokes to SSH
            term.onData(function (data) {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    var encoder = new TextEncoder();
                    ws.send(encoder.encode(data));
                }
            });

            // Send binary data (for pasted content, etc.)
            term.onBinary(function (data) {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    var buffer = new Uint8Array(data.length);
                    for (var i = 0; i < data.length; i++) {
                        buffer[i] = data.charCodeAt(i) & 0xff;
                    }
                    ws.send(buffer);
                }
            });

            // Handle terminal resize
            term.onResize(function (size) {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({
                        type: 'resize',
                        cols: size.cols,
                        rows: size.rows
                    }));
                }
            });

            // Refit on window resize
            window.addEventListener('resize', function () {
                if (fitAddon) {
                    fitAddon.fit();
                }
            });
        }

        function reconnect() {
            connect();
        }

        function disconnect() {
            if (ws) {
                ws.close();
            }
            setStatus(false);
        }

        // Auto-connect on page load
        // Wait for Jellyfin's ApiClient to be available
        function waitForApiClient() {
            if (typeof ApiClient !== 'undefined' && ApiClient.accessToken()) {
                connect();
            } else {
                setTimeout(waitForApiClient, 200);
            }
        }

        document.addEventListener('viewshow', function () {
            waitForApiClient();
        });

        // Fallback: if viewshow doesn't fire (direct navigation)
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            waitForApiClient();
        } else {
            document.addEventListener('DOMContentLoaded', function () {
                waitForApiClient();
            });
        }
    </script>
</body>
</html>
```

**Step 2: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

**Step 3: Commit**

```bash
git add Jellyfin.Plugin.SshTerminal/Web/terminal.html
git commit -m "feat: add xterm.js terminal page"
```

---

## Task 6: Build, Package, and Create Repository Manifest

**Files:**
- Create: `build.sh` (build + package script)
- Create: `manifest.json` (Jellyfin plugin repository manifest)

**Step 1: Create build.sh**

```bash
#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.SshTerminal"
OUTPUT_DIR="dist"

echo "Building plugin..."
dotnet publish "$PLUGIN_DIR" -c Release -o "$OUTPUT_DIR/publish"

echo "Creating plugin zip..."
mkdir -p "$OUTPUT_DIR"
cd "$OUTPUT_DIR/publish"
zip -r "../jellyfin-plugin-ssh-terminal_1.0.0.0.zip" *.dll
cd ../..

echo "Computing checksum..."
MD5=$(md5sum "$OUTPUT_DIR/jellyfin-plugin-ssh-terminal_1.0.0.0.zip" | awk '{print $1}')
echo "MD5: $MD5"

echo ""
echo "Build complete: $OUTPUT_DIR/jellyfin-plugin-ssh-terminal_1.0.0.0.zip"
echo "Update manifest.json checksum to: $MD5"
```

**Step 2: Create manifest.json**

Replace `YOUR_GITHUB_USERNAME` with your actual GitHub username before publishing.

```json
[
  {
    "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "name": "SSH Terminal",
    "description": "Web-based SSH terminal access through Jellyfin. Connect to machines on your local network via an xterm.js terminal in the browser, proxied through Jellyfin's WebSocket API.",
    "overview": "SSH terminal in your Jellyfin web UI",
    "owner": "YOUR_GITHUB_USERNAME",
    "category": "General",
    "versions": [
      {
        "version": "1.0.0.0",
        "changelog": "Initial release — password and private key auth, xterm.js terminal, WebSocket-to-SSH bridge",
        "targetAbi": "10.10.0.0",
        "sourceUrl": "https://github.com/YOUR_GITHUB_USERNAME/jellyfin-plugin-ssh-terminal/releases/download/v1.0.0/jellyfin-plugin-ssh-terminal_1.0.0.0.zip",
        "checksum": "REPLACE_WITH_MD5_FROM_BUILD",
        "timestamp": "2026-03-24T00:00:00Z"
      }
    ]
  }
]
```

**Step 3: Make build.sh executable and test**

```bash
chmod +x build.sh
./build.sh
```

Expected: Build succeeds, zip file created in `dist/`, MD5 printed.

**Step 4: Update manifest.json checksum with the MD5 from build output**

**Step 5: Commit**

```bash
git add build.sh manifest.json
git commit -m "feat: add build script and plugin repository manifest"
```

---

## Task 7: GitHub Repository Setup and Release

**Step 1: Create GitHub repository**

```bash
gh repo create jellyfin-plugin-ssh-terminal --public --source=. --push
```

**Step 2: Create a GitHub Release with the zip artifact**

```bash
gh release create v1.0.0 \
  dist/jellyfin-plugin-ssh-terminal_1.0.0.0.zip \
  --title "v1.0.0 — Initial Release" \
  --notes "Initial release of the SSH Terminal plugin for Jellyfin 10.10.x.

Features:
- Web-based SSH terminal using xterm.js
- Password and private key authentication
- Auto-connect from plugin configuration
- Terminal resize support
- WebSocket keepalive for reverse proxy compatibility"
```

**Step 3: Get the raw manifest URL**

The repository manifest URL to add in Jellyfin will be:

```
https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/jellyfin-plugin-ssh-terminal/main/manifest.json
```

**Step 4: Install in Jellyfin**

1. Open Jellyfin admin dashboard > Plugins > Repositories
2. Click "Add" and enter:
   - Name: `SSH Terminal`
   - URL: `https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/jellyfin-plugin-ssh-terminal/main/manifest.json`
3. Go to Catalog, find "SSH Terminal", click Install
4. Restart Jellyfin
5. Go to Plugins > SSH Terminal > configure SSH host/port/username/password
6. Open the terminal page from the menu or config page link

---

## Summary of Files

```
jellyfin-plugin-ssh-terminal/
├── manifest.json                              # Plugin repository manifest
├── build.sh                                   # Build + package script
├── docs/plans/
│   ├── 2026-03-24-jellyfin-ssh-plugin-design.md
│   └── 2026-03-24-jellyfin-ssh-plugin-plan.md
└── Jellyfin.Plugin.SshTerminal/
    ├── Jellyfin.Plugin.SshTerminal.csproj
    ├── Plugin.cs
    ├── PluginConfiguration.cs
    ├── ServiceRegistrator.cs
    ├── SshController.cs
    ├── SshWebSocketHandler.cs
    ├── Configuration/
    │   └── configPage.html
    └── Web/
        └── terminal.html
```
