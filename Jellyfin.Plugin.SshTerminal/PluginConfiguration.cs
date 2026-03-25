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
