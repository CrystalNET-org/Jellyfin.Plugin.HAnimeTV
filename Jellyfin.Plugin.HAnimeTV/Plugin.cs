using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HAnimeTV
{
    /// <summary>
    /// hanime.tv as a channel, for selected users only.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override Guid Id => Guid.Parse("1029189A-8A81-4419-8B08-78EB68071A0D");

        public override string Name => "hanime.tv";

        public override string Description => "Browse and play hanime.tv as a channel, for selected users only.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "hanime.tv",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                    // Linked in the dashboard sidebar, below Plugins
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
            };
        }
    }
}
