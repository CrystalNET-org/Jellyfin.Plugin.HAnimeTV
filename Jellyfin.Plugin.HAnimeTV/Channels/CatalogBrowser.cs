using System.Globalization;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// The channel's folders, built from the catalog.
    /// </summary>
    /// <remarks>
    /// Folder ids are "latest", "genre:&lt;tag&gt;" and so on; a video's id is its folder's id,
    /// "|" and its slug. Jellyfin keeps one entry per id, under one parent, so a video gets an
    /// entry per folder it is listed in: with a shared id it would move between folders and go
    /// missing from the others.
    /// </remarks>
    internal static class CatalogBrowser
    {
        public const string Latest = "latest";
        public const string Released = "released";
        public const string Popular = "popular";
        public const string Liked = "liked";
        public const string Series = "series";
        public const string Genres = "genres";
        public const string Studios = "studios";

        private const string SeriesLetterPrefix = "series:";
        private const string ShowPrefix = "show:";
        private const string GenrePrefix = "genre:";
        private const string StudioPrefix = "studio:";
        private const char VideoSeparator = '|';
        private const string OtherLetter = "#";

        /// <summary>
        /// The official rating Jellyfin knows as adults only, so parental controls apply.
        /// </summary>
        public const string AdultRating = "XXX";

        public static ChannelItemResult GetItems(IReadOnlyList<HanimeVideo> catalog, PluginConfiguration config, string? folderId)
        {
            var videos = Visible(catalog, config);
            var limit = Math.Clamp(config.MaxItemsPerFolder, 10, 2000);
            folderId ??= string.Empty;

            IReadOnlyList<ChannelItemInfo> items = folderId switch
            {
                "" => Root(),
                Latest => VideoItems(folderId, videos.OrderByDescending(v => v.CreatedAt ?? DateTime.MinValue).Take(limit)),
                Released => VideoItems(folderId, videos.OrderByDescending(v => v.ReleasedAt ?? DateTime.MinValue).Take(limit)),
                Popular => VideoItems(folderId, videos.OrderByDescending(v => v.Views).Take(limit)),
                Liked => VideoItems(folderId, videos.OrderByDescending(v => v.Likes).Take(limit)),
                Series => SeriesLetters(videos),
                Genres => Groups(videos.SelectMany(v => v.Tags), GenrePrefix, config.NormalizedHiddenTags()),
                Studios => Groups(videos.Select(v => v.Brand).OfType<string>(), StudioPrefix, Array.Empty<string>()),
                _ when folderId.StartsWith(SeriesLetterPrefix, StringComparison.Ordinal) => SeriesOfLetter(videos, folderId[SeriesLetterPrefix.Length..]),
                _ when folderId.StartsWith(ShowPrefix, StringComparison.Ordinal) => VideoItems(
                    folderId,
                    videos.Where(v => string.Equals(v.SeriesInfo().Series, folderId[ShowPrefix.Length..], StringComparison.OrdinalIgnoreCase))
                        .OrderBy(v => v.SeriesInfo().Episode)
                        .ThenBy(v => v.ReleasedAt ?? DateTime.MaxValue)),
                _ when folderId.StartsWith(GenrePrefix, StringComparison.Ordinal) => VideoItems(
                    folderId,
                    Newest(videos.Where(v => v.Tags.Contains(folderId[GenrePrefix.Length..], StringComparer.OrdinalIgnoreCase))).Take(limit)),
                _ when folderId.StartsWith(StudioPrefix, StringComparison.Ordinal) => VideoItems(
                    folderId,
                    Newest(videos.Where(v => string.Equals(v.Brand, folderId[StudioPrefix.Length..], StringComparison.OrdinalIgnoreCase))).Take(limit)),
                _ => Array.Empty<ChannelItemInfo>(),
            };

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        /// <summary>
        /// The newest uploads, for Jellyfin's "Latest" row.
        /// </summary>
        public static IEnumerable<ChannelItemInfo> GetLatest(IReadOnlyList<HanimeVideo> catalog, PluginConfiguration config, int count) =>
            VideoItems(Latest, Visible(catalog, config).OrderByDescending(v => v.CreatedAt ?? DateTime.MinValue).Take(count));

        /// <summary>
        /// Gets the slug of a video's id, or null if the id is not a video's.
        /// </summary>
        public static string? SlugOf(string id)
        {
            var separator = id.LastIndexOf(VideoSeparator);
            return separator >= 0 && separator < id.Length - 1 ? id[(separator + 1)..] : null;
        }

        /// <summary>
        /// The videos the settings let through.
        /// </summary>
        internal static List<HanimeVideo> Visible(IReadOnlyList<HanimeVideo> catalog, PluginConfiguration config)
        {
            var hidden = config.NormalizedHiddenTags();
            return catalog
                .Where(v => !(config.HideCensored && v.IsCensored))
                .Where(v => hidden.Count == 0 || !v.Tags.Any(hidden.Contains))
                .ToList();
        }

        private static IOrderedEnumerable<HanimeVideo> Newest(IEnumerable<HanimeVideo> videos) =>
            videos.OrderByDescending(v => v.ReleasedAt ?? v.CreatedAt ?? DateTime.MinValue);

        private static List<ChannelItemInfo> Root() =>
        [
            Folder(Latest, "Recently uploaded"),
            Folder(Released, "New releases"),
            Folder(Popular, "Most viewed"),
            Folder(Liked, "Most liked"),
            Folder(Series, "Series A–Z"),
            Folder(Genres, "Genres"),
            Folder(Studios, "Studios"),
        ];

        private static List<ChannelItemInfo> SeriesLetters(IEnumerable<HanimeVideo> videos) =>
            videos.Select(v => v.SeriesInfo().Series)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(LetterOf)
                .OrderBy(g => g.Key == OtherLetter ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Folder(SeriesLetterPrefix + g.Key, string.Create(CultureInfo.InvariantCulture, $"{g.Key} ({g.Count()})")))
                .ToList();

        private static List<ChannelItemInfo> SeriesOfLetter(IEnumerable<HanimeVideo> videos, string letter) =>
            videos.GroupBy(v => v.SeriesInfo().Series, StringComparer.OrdinalIgnoreCase)
                .Where(g => LetterOf(g.Key) == letter)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var episodes = g.OrderBy(v => v.SeriesInfo().Episode).ToList();
                    var first = episodes[0];
                    var folder = Folder(ShowPrefix + g.Key, g.Key);
                    folder.ImageUrl = first.PosterUrl ?? first.ThumbnailUrl;
                    folder.Overview = first.PlainDescription();
                    folder.Genres = episodes.SelectMany(v => v.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    folder.Studios = episodes.Select(v => v.Brand).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    folder.OfficialRating = AdultRating;
                    folder.PremiereDate = first.ReleasedAt;
                    folder.ProductionYear = first.ReleasedAt?.Year;
                    return folder;
                })
                .ToList();

        /// <summary>
        /// A folder per genre or studio, with its number of videos.
        /// </summary>
        private static List<ChannelItemInfo> Groups(IEnumerable<string> names, string prefix, IReadOnlyCollection<string> hidden) =>
            names.Where(n => !hidden.Contains(n))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => Folder(prefix + g.Key.ToLowerInvariant(), string.Create(CultureInfo.InvariantCulture, $"{Title(g.Key)} ({g.Count()})")))
                .ToList();

        private static string LetterOf(string name)
        {
            var first = name.TrimStart().FirstOrDefault();
            return char.IsAsciiLetter(first) ? char.ToUpperInvariant(first).ToString() : OtherLetter;
        }

        /// <summary>
        /// hanime.tv's tags are lower case: "hd" becomes "HD", "big boobs" "Big Boobs".
        /// </summary>
        private static string Title(string name) => name.Length switch
        {
            <= 2 => name.ToUpperInvariant(),
            _ when char.IsLower(name[0]) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name),
            _ => name,
        };

        private static ChannelItemInfo Folder(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
        };

        private static List<ChannelItemInfo> VideoItems(string folderId, IEnumerable<HanimeVideo> videos) =>
            videos.Select(v => VideoItem(folderId, v)).ToList();

        internal static ChannelItemInfo VideoItem(string folderId, HanimeVideo video)
        {
            var (series, episode) = video.SeriesInfo();
            var votes = video.Likes + video.Dislikes;
            return new ChannelItemInfo
            {
                Id = folderId + VideoSeparator + video.Slug,
                Name = video.Name,
                SeriesName = series,
                IndexNumber = episode,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                Overview = video.PlainDescription(),
                Genres = video.Tags.Select(Title).ToList(),
                Tags = video.IsCensored ? ["censored"] : [],
                Studios = video.Brand is { } brand ? [brand] : [],
                OfficialRating = AdultRating,
                CommunityRating = votes > 0 ? (float)Math.Round(10.0 * video.Likes / votes, 1) : null,
                // Landscape: Jellyfin shows episodes as thumbnails
                ImageUrl = video.ThumbnailUrl ?? video.PosterUrl,
                PremiereDate = video.ReleasedAt,
                ProductionYear = video.ReleasedAt?.Year,
                DateCreated = video.CreatedAt,
                DateModified = video.CreatedAt ?? DateTime.MinValue,
                RunTimeTicks = video.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).Ticks : null,
                HomePageUrl = "https://hanime.tv/videos/hentai/" + Uri.EscapeDataString(video.Slug),
            };
        }
    }
}
