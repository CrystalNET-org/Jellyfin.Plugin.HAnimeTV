using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HAnimeTV.Configuration
{
    /// <summary>
    /// Plugin settings.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public const string DefaultSearchUrl = "https://guest.freeanimehentai.net/api/v11/search_hvs";

        public const string DefaultHandshakeUrl = "https://auth.hanime.tv/api/v11/handshake";

        public const string DefaultLoginUrl = "https://www.universal-cdn.com/rapi/v7/sessions";

        public const string DefaultStreamHost = "https://hanime.tv";

        /// <summary>
        /// Gets or sets the users who see the channel and can play its videos. Nobody else
        /// does, administrators included, so a new installation shows the channel to no one.
        /// </summary>
        public Guid[] AllowedUsers { get; set; } = Array.Empty<Guid>();

        /// <summary>
        /// Gets or sets a value indicating whether the channel access in the users' policies
        /// (Dashboard → Users → Access) follows <see cref="AllowedUsers"/>. Without it, the
        /// channel is only hidden from the others' channel lists, while its videos stay
        /// reachable for them by id.
        /// </summary>
        public bool EnforceAccess { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of videos in a folder. Jellyfin reads a folder's
        /// items all at once and keeps an entry per item.
        /// </summary>
        public int MaxItemsPerFolder { get; set; } = 200;

        /// <summary>
        /// Gets or sets genres (hanime.tv tags) whose videos are never shown.
        /// </summary>
        public string[] HiddenTags { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets a value indicating whether censored videos are hidden.
        /// </summary>
        public bool HideCensored { get; set; }

        /// <summary>
        /// Gets or sets the email of a hanime.tv account. Optional: guests get up to 720p,
        /// premium accounts 1080p.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the catalog endpoint (the signed search dataset). Can point to a relay.
        /// </summary>
        public string SearchUrl { get; set; } = DefaultSearchUrl;

        /// <summary>
        /// Gets or sets the endpoint that returns a video's streams. Can point to a relay.
        /// </summary>
        public string HandshakeUrl { get; set; } = DefaultHandshakeUrl;

        public string LoginUrl { get; set; } = DefaultLoginUrl;

        /// <summary>
        /// Gets or sets the host that relative stream paths are resolved against.
        /// </summary>
        public string StreamHost { get; set; } = DefaultStreamHost;

        /// <summary>
        /// Gets or sets the hours the catalog is kept before it is downloaded again.
        /// </summary>
        public int CatalogCacheHours { get; set; } = 6;

        /// <summary>
        /// Gets the hidden tags, trimmed and without empty entries.
        /// </summary>
        public IReadOnlyCollection<string> NormalizedHiddenTags() =>
            (HiddenTags ?? Array.Empty<string>())
                .Select(t => t?.Trim() ?? string.Empty)
                .Where(t => t.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public bool IsAllowed(Guid userId) => (AllowedUsers ?? Array.Empty<Guid>()).Contains(userId);
    }
}
