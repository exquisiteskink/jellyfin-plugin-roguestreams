using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RogueStreams;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the HDHomeRun base URL used to read live tuner state.
    /// Empty by default: each installation detects or enters its own tuner URL.
    /// </summary>
    public string TunerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Unused: closing is always manual. Kept so existing config XML still deserializes.
    /// </summary>
    public int GraceSeconds { get; set; } = 20;
}
