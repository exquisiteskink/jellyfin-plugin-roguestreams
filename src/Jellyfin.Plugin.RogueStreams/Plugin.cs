using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RogueStreams;

/// <summary>
/// Lists every Live TV transcode running in this server, flags the ones no session
/// and no recording accounts for, and kills them one at a time.
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
        "Shows Live TV transcodes and tuner leases, including orphans that the dashboard's session list cannot see, and kills an individual orphan.";

    public override Guid Id => Guid.Parse("7f1c2a54-9b3d-4c6e-8a10-2c9e5d4b7f31");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "roguestreams",
                EmbeddedResourcePath = GetType().Namespace + ".Web.roguestreams.html"
            }
        };
    }
}
