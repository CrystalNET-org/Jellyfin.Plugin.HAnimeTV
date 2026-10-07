namespace Jellyfin.Plugin.HAnimeTV.Configuration
{
    /// <summary>
    /// How a provider appears in Jellyfin.
    /// </summary>
    public enum ProviderMode
    {
        /// <summary>Not shown to anyone.</summary>
        Off,

        /// <summary>As a channel, browsed by folders.</summary>
        Channel,

        /// <summary>As a shows library written by the plugin.</summary>
        Library,
    }

    /// <summary>
    /// Settings every provider has.
    /// </summary>
    public abstract class ProviderSettings
    {
        public ProviderMode Mode { get; set; }

        /// <summary>
        /// Gets or sets the users who see the provider's channel or library. Nobody else does,
        /// administrators included.
        /// </summary>
        public Guid[] AllowedUsers { get; set; } = Array.Empty<Guid>();

        /// <summary>
        /// Gets or sets the maximum number of videos in a channel folder. Jellyfin reads a
        /// folder's items all at once and keeps an entry per item.
        /// </summary>
        public int MaxItemsPerFolder { get; set; } = 200;

        public bool IsAllowed(Guid userId) => (AllowedUsers ?? Array.Empty<Guid>()).Contains(userId);

        /// <summary>
        /// Gets whether the user sees the provider in the given mode.
        /// </summary>
        public bool Grants(Guid userId, ProviderMode mode) => Mode == mode && mode != ProviderMode.Off && IsAllowed(userId);

        protected static IReadOnlyCollection<string> Normalize(string[]? values) =>
            (values ?? Array.Empty<string>())
                .Select(t => t?.Trim() ?? string.Empty)
                .Where(t => t.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hentai: hanime.tv, oppai.stream and Hentai Haven, merged into one channel or shows library.
    /// </summary>
    public class HentaiSettings : ProviderSettings
    {
        public const string DefaultSearchUrl = "https://guest.freeanimehentai.net/api/v11/search_hvs";

        public const string DefaultHandshakeUrl = "https://auth.hanime.tv/api/v11/handshake";

        public const string DefaultLoginUrl = "https://www.universal-cdn.com/rapi/v7/sessions";

        public const string DefaultStreamHost = "https://hanime.tv";

        public const string DefaultLibraryName = "Hentai";

        public const string DefaultHentaiHavenUrl = "https://hentaihaven.co";

        public const string DefaultOppaiStreamUrl = "https://oppai.stream";

        public HentaiSettings()
        {
            Mode = ProviderMode.Library;
        }

        /// <summary>
        /// Gets or sets a value indicating whether hanime.tv's videos are included.
        /// </summary>
        public bool HanimeEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether oppai.stream's videos are included. Episodes
        /// hanime.tv has too come from hanime.tv.
        /// </summary>
        public bool OppaiStreamEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets oppai.stream's address.
        /// </summary>
        public string OppaiStreamUrl { get; set; } = DefaultOppaiStreamUrl;

        /// <summary>
        /// Gets or sets a value indicating whether Hentai Haven's videos are included. Episodes
        /// another source has too come from that source.
        /// </summary>
        public bool HentaiHavenEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets Hentai Haven's address.
        /// </summary>
        public string HentaiHavenUrl { get; set; } = DefaultHentaiHavenUrl;

        /// <summary>
        /// Gets or sets genres whose videos are left out.
        /// </summary>
        public string[] HiddenTags { get; set; } = Array.Empty<string>();

        public bool HideCensored { get; set; }

        /// <summary>
        /// Gets or sets the email of a hanime.tv account. Optional: guests get up to 720p,
        /// premium accounts 1080p.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the folder the library's files are written to. Empty means
        /// "hanime.tv" in Jellyfin's data directory (the folder of versions before 0.2).
        /// </summary>
        public string LibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the plugin creates the Jellyfin library
        /// for <see cref="LibraryPath"/> if there is none.
        /// </summary>
        public bool CreateLibrary { get; set; } = true;

        public string LibraryName { get; set; } = DefaultLibraryName;

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
        /// Gets or sets the hours the catalogs are kept before they are downloaded again.
        /// </summary>
        public int CatalogCacheHours { get; set; } = 6;

        public IReadOnlyCollection<string> NormalizedHiddenTags() => Normalize(HiddenTags);
    }

    /// <summary>
    /// Pornhub: a channel.
    /// </summary>
    public class PornhubSettings : ProviderSettings
    {
        public const string DefaultApiUrl = "https://www.pornhub.com/webmasters";

        public const string DefaultSiteUrl = "https://www.pornhub.com";

        /// <summary>
        /// Gets or sets searches shown as folders of their own, e.g. a performer's name.
        /// </summary>
        public string[] Searches { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets categories whose videos are left out.
        /// </summary>
        public string[] HiddenCategories { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the public API the catalog comes from.
        /// </summary>
        public string ApiUrl { get; set; } = DefaultApiUrl;

        /// <summary>
        /// Gets or sets the site the video pages, with their streams, come from.
        /// </summary>
        public string SiteUrl { get; set; } = DefaultSiteUrl;

        public IReadOnlyCollection<string> NormalizedSearches() => Normalize(Searches);

        public IReadOnlyCollection<string> NormalizedHiddenCategories() => Normalize(HiddenCategories);
    }
}
