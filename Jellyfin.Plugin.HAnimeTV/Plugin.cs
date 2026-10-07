using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HAnimeTV
{
    /// <summary>
    /// Adult media providers (hentai from hanime.tv, oppai.stream and Hentai Haven, Pornhub) as
    /// channels or shows libraries, each for selected users only.
    /// </summary>
    /// <remarks>
    /// The plugin started as "hanime.tv"; its id and assembly stay, so installations update
    /// in place and keep their settings.
    /// </remarks>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            if (Configuration.MigrateLegacySettings())
            {
                SaveConfiguration();
            }
        }

        public static Plugin? Instance { get; private set; }

        public override Guid Id => Guid.Parse("1029189A-8A81-4419-8B08-78EB68071A0D");

        public override string Name => "Adult Media";

        public override string Description => "Hentai (hanime.tv, oppai.stream and Hentai Haven) and Pornhub as channels or shows libraries, for selected users only.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Adult Media",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                    // Linked in the dashboard sidebar, below Plugins
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
            };
        }
    }
}
