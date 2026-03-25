# Jellyfin SSH Plugin — Design Document

## Problem

Need remote SSH access to a home desktop while away from the network. Jellyfin media server is already publicly exposed. A custom plugin can bridge the gap by providing a web-based SSH terminal within the Jellyfin UI, installable remotely via a custom plugin repository.

## Architecture

```
Browser (xterm.js) ──WebSocket──► Jellyfin Plugin ──SSH──► Desktop (sshd)
```

The plugin runs inside Jellyfin on the home network, so it has direct access to the desktop's SSH port. The only publicly exposed port remains Jellyfin's existing port.

### Components

| File | Purpose |
|------|---------|
| `Plugin.cs` | Plugin entry point — ID, name, version, singleton instance |
| `PluginConfiguration.cs` | Settings: SSH host, port, username, auth method (password/key), credentials |
| `ServiceRegistrator.cs` | Registers plugin services into Jellyfin's DI container |
| `SshController.cs` | ASP.NET Core controller — serves terminal page, handles WebSocket upgrade at `/Ssh/Terminal` |
| `SshWebSocketHandler.cs` | Per-session bridge: SSH.NET `SshClient` + `ShellStream` ↔ WebSocket, two async loops |
| `Configuration/configPage.html` | Plugin settings page — configure SSH target host and credentials |
| `Web/terminal.html` | Terminal UI — xterm.js loaded from CDN, WebSocket client, fullscreen terminal |

### Data Flow

1. Authenticated Jellyfin user navigates to the terminal page
2. Browser loads xterm.js and opens a WebSocket to `/Ssh/Terminal`
3. Plugin validates Jellyfin session token on the WebSocket upgrade request
4. Plugin creates an SSH.NET `SshClient` connection to the configured host
5. Plugin opens a `ShellStream` (PTY) on the SSH connection
6. Two async loops run concurrently:
   - **WS → SSH**: browser keystrokes → WebSocket binary frame → `ShellStream.Write()`
   - **SSH → WS**: `ShellStream.ReadAsync()` → WebSocket binary frame → xterm.js `terminal.write()`
7. Resize events: browser sends JSON text frame `{"type":"resize","cols":N,"rows":N}` → `ShellStream.SendWindowChangeRequest()`
8. Disconnect: browser closes tab → WebSocket closes → ShellStream disposed → SshClient disconnected

### WebSocket Protocol

- **Binary frames**: raw terminal I/O (both directions)
- **Text frames**: JSON control messages — `{"type":"resize","cols":80,"rows":24}`
- **Keepalive**: SSH.NET `KeepAliveInterval` set to 30s to prevent reverse proxy idle timeouts

### Reverse Proxy Compatibility

Works behind any reverse proxy that passes WebSocket connections — standard for Jellyfin deployments since Jellyfin already uses WebSockets. No additional proxy configuration needed unless the proxy has a short idle timeout (nginx: `proxy_read_timeout 3600s;`).

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| SSH implementation | SSH.NET (library-based) | Self-contained, no external service needed, installable remotely |
| Terminal emulator | xterm.js from CDN | Industry standard, zero server-side footprint |
| Authentication | Credentials stored in plugin config | User wants to open terminal and be connected immediately |
| Access control | Any authenticated Jellyfin user | `[Authorize]` attribute on controller |
| Target hosts | Single host from config | User only needs access to one desktop |
| Target Jellyfin | 10.10.x (net8.0, targetAbi 10.10.0.0) | User's current version |

## Dependencies

- **SSH.NET** (`Renci.SshNet`) — NuGet, ~500KB, pure managed C#, MIT licensed
- **xterm.js** + **xterm-addon-fit** — loaded from CDN in terminal HTML, MIT licensed
- **Jellyfin.Controller** + **Jellyfin.Model** — build-time references only, excluded from plugin output

## Distribution

- GitHub repository hosts the source code
- GitHub Releases host the built `.zip` artifact
- A `manifest.json` file in the repo (served via GitHub raw/Pages) acts as the plugin repository
- User adds the manifest URL as a custom repository in Jellyfin admin → Plugins → Repositories
- Install, restart Jellyfin, configure SSH target, use

## Security Notes

- SSH credentials stored in Jellyfin plugin config XML on disk (same trust model as all Jellyfin plugin configs)
- WebSocket endpoint requires valid Jellyfin auth token
- Inherits Jellyfin's existing TLS configuration
- Plugin only accessible to authenticated users
