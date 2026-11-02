using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RogueStreams;

/// <summary>
/// Lists Jellyfin's open Live TV streams and closes them one at a time.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Rogue Streams";

    public override string Description =>
        "Shows every open Live TV stream (including ones Active Sessions cannot see) and closes an individual stream so its tuner is released.";

    public override Guid Id => Guid.Parse("7f1c2a54-9b3d-4c6e-8a10-2c9e5d4b7f31");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "live_tv",
                EmbeddedResourcePath = ns + ".Web.roguestreams.html"
            },
            new PluginPageInfo
            {
                Name = Name + ".js",
                EmbeddedResourcePath = ns + ".Web.roguestreams.js"
            }
        };
    }
}
