using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Library;
using Jellyfin.Plugin.HAnimeTV.Pornhub;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// Pornhub as a channel: featured, newest, most viewed, top rated, categories and the
    /// configured searches.
    /// </summary>
    /// <remarks>
    /// Folder ids are "newest", "category:&lt;name&gt;" and so on; a video's id is its folder's
    /// id, "|" and its viewkey, so that a video listed in several folders has an entry in each.
    /// </remarks>
    public sealed class PornhubChannelSource : IChannelSource
    {
        public const string ProviderName = "Pornhub";

        internal const string Featured = "featured";
        internal const string Newest = "newest";
        internal const string MostViewedWeek = "mostviewed:weekly";
        internal const string MostViewedMonth = "mostviewed:monthly";
        internal const string MostViewedAll = "mostviewed:alltime";
        internal const string TopRated = "rating:alltime";
        internal const string Categories = "categories";
        internal const string Searches = "searches";
        private const string CategoryPrefix = "category:";
        private const string SearchPrefix = "search:";
        private const char VideoSeparator = '|';

        private readonly PornhubClient _client;
        private readonly StreamLinks _links;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<PornhubChannelSource> _logger;

        public PornhubChannelSource(PornhubClient client, StreamLinks links, IMediaEncoder mediaEncoder, ILogger<PornhubChannelSource> logger)
        {
            _client = client;
            _links = links;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }

        public string ChannelName => ProviderName;

        public string Description => "Videos from Pornhub.";

        public string HomePageUrl => "https://www.pornhub.com";

        public string ImageName => "pornhub.png";

        public ProviderSettings Settings(PluginConfiguration config) => config.Pornhub;

        public async Task<ChannelItemResult> GetItemsAsync(string? folderId, CancellationToken cancellationToken)
        {
            var config = Config;
            var count = Math.Clamp(config.MaxItemsPerFolder, 10, 500);
            folderId ??= string.Empty;
            IReadOnlyList<ChannelItemInfo> items = folderId switch
            {
                "" => Root(config),
                Categories => (await _client.GetCategoriesAsync(cancellationToken).ConfigureAwait(false))
                    .Where(c => !config.NormalizedHiddenCategories().Contains(c))
                    .Select(c => Folder(CategoryPrefix + c, c))
                    .ToList(),
                Searches => config.NormalizedSearches().Order(StringComparer.OrdinalIgnoreCase).Select(s => Folder(SearchPrefix + s, s)).ToList(),
                _ when Query(folderId) is { } query => VideoItems(folderId, await _client.SearchAsync(query, count, cancellationToken).ConfigureAwait(false), config),
                _ => Array.Empty<ChannelItemInfo>(),
            };
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        public async Task<IEnumerable<ChannelItemInfo>> GetLatestAsync(CancellationToken cancellationToken) =>
            VideoItems(Newest, await _client.SearchAsync(new PornhubQuery("newest"), 24, cancellationToken).ConfigureAwait(false), Config);

        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(string id, CancellationToken cancellationToken)
        {
            var viewkey = ViewkeyOf(id) ?? throw new ArgumentException("Not a video: " + id, nameof(id));
            // Some videos only have MP4 files: those are served as files, with ranges for seeking
            var hls = (await _client.GetStreamsAsync(viewkey, cancellationToken).ConfigureAwait(false))[0].IsHls;
            var source = await StreamSource.CreateAsync("pornhub:" + viewkey, _links.Pornhub(viewkey, hls), null, _mediaEncoder, _logger, cancellationToken, hls ? "hls" : "mp4").ConfigureAwait(false);
            return [source];
        }

        public string CacheKey(PluginConfiguration config) => string.Join(
            '\n',
            config.Pornhub.MaxItemsPerFolder,
            string.Join(',', config.Pornhub.NormalizedHiddenCategories().Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(',', config.Pornhub.NormalizedSearches().Order(StringComparer.OrdinalIgnoreCase)),
            config.Pornhub.ApiUrl);

        /// <summary>
        /// Gets a video id's viewkey, or null if the id is not a video's.
        /// </summary>
        public static string? ViewkeyOf(string id)
        {
            var separator = id.LastIndexOf(VideoSeparator);
            return separator >= 0 && separator < id.Length - 1 ? id[(separator + 1)..] : null;
        }

        /// <summary>
        /// Gets the search behind a folder of videos, or null if the folder holds folders.
        /// </summary>
        internal static PornhubQuery? Query(string folderId) => folderId switch
        {
            Featured => new PornhubQuery("featured"),
            Newest => new PornhubQuery("newest"),
            MostViewedWeek => new PornhubQuery("mostviewed", "weekly"),
            MostViewedMonth => new PornhubQuery("mostviewed", "monthly"),
            MostViewedAll => new PornhubQuery("mostviewed", "alltime"),
            TopRated => new PornhubQuery("rating", "alltime"),
            _ when folderId.StartsWith(CategoryPrefix, StringComparison.Ordinal) => new PornhubQuery("mostviewed", "monthly", Category: folderId[CategoryPrefix.Length..]),
            // Most relevant first, as on the site
            _ when folderId.StartsWith(SearchPrefix, StringComparison.Ordinal) => new PornhubQuery(null, Search: folderId[SearchPrefix.Length..]),
            _ => null,
        };

        internal static List<ChannelItemInfo> Root(PornhubSettings config)
        {
            var root = new List<ChannelItemInfo>
            {
                Folder(Featured, "Featured"),
                Folder(Newest, "Newest"),
                Folder(MostViewedWeek, "Most viewed this week"),
                Folder(MostViewedMonth, "Most viewed this month"),
                Folder(MostViewedAll, "Most viewed of all time"),
                Folder(TopRated, "Top rated"),
                Folder(Categories, "Categories"),
            };
            if (config.NormalizedSearches().Count > 0)
            {
                root.Add(Folder(Searches, "Searches"));
            }

            return root;
        }

        internal static List<ChannelItemInfo> VideoItems(string folderId, IEnumerable<PornhubVideo> videos, PornhubSettings config)
        {
            var hidden = config.NormalizedHiddenCategories();
            return videos
                .Where(v => hidden.Count == 0 || !v.Categories.Any(hidden.Contains))
                .Select(v => VideoItem(folderId, v))
                .ToList();
        }

        internal static ChannelItemInfo VideoItem(string folderId, PornhubVideo video)
        {
            var overview = video.Pornstars.Count > 0 ? "With " + string.Join(", ", video.Pornstars) + "." : null;
            return new ChannelItemInfo
            {
                Id = folderId + VideoSeparator + video.Id,
                Name = video.Title,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                Overview = overview,
                Genres = video.Categories.ToList(),
                Tags = video.Tags.Concat(video.Pornstars).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                OfficialRating = LibraryLayout.AdultRating,
                CommunityRating = video.Rating is { } rating ? (float)Math.Round(rating / 10, 1) : null,
                ImageUrl = video.Thumbnail,
                PremiereDate = video.PublishedAt,
                ProductionYear = video.PublishedAt?.Year,
                DateCreated = video.PublishedAt,
                DateModified = video.PublishedAt ?? DateTime.MinValue,
                RunTimeTicks = video.Duration?.Ticks,
                HomePageUrl = "https://www.pornhub.com/view_video.php?viewkey=" + Uri.EscapeDataString(video.Id),
            };
        }

        private static ChannelItemInfo Folder(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
        };

        private static PornhubSettings Config => Plugin.Instance?.Configuration.Pornhub ?? new PornhubSettings();
    }
}
