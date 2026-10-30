using System.Globalization;
using Jellyfin.Plugin.JellyMoldCompanion.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyMoldCompanion;

/// <summary>
/// What Jellyfin loads. Owns the settings and serves the dashboard page; the work is done by the services
/// <see cref="PluginServiceRegistrator"/> registers.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's permanent id. Never change it: it is how Jellyfin and the plugin repository know this plugin.</summary>
    public static readonly Guid PluginId = Guid.Parse("224d7c95-988f-4534-965f-69a4f6e3d802");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyMold Companion";

    public override Guid Id => PluginId;

    public override string Description =>
        "A permanent record of everything each person watches and listens to on this server, for JellyMold and any other app.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "JellyMoldCompanion",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
        },
    ];
}
