using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RogueStreams;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the HDHomeRun base URL used to read live tuner state.</summary>
    public string TunerUrl { get; set; } = "";

    /// <summary>
    /// Gets or sets how long a stream must stay orphaned before the UI offers to kill it.
    /// A channel change legitimately holds two tuners for a few seconds.
    /// </summary>
    public int GraceSeconds { get; set; } = 20;
}
