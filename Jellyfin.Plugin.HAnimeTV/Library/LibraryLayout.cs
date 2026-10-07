using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// A file of the library, relative to its folder.
    /// </summary>
    public sealed record LibraryFile(string RelativePath, string Content);

    /// <summary>
    /// The library's files, in the layout Jellyfin's shows libraries expect:
    /// <c>Series/Season 01/Series S01E02.strm</c> with an NFO file next to every episode and a
    /// tvshow.nfo per series. The .strm files point at the plugin's stream endpoint; the NFO
    /// files carry hanime.tv's metadata and image URLs, which Jellyfin reads on its scan.
    /// </summary>
    public static class LibraryLayout
    {
        /// <summary>
        /// The official rating Jellyfin knows as adults only, so parental controls apply.
        /// </summary>
        public const string AdultRating = "XXX";

        public const string ProviderName = "hanime";

        private const int MaxNameLength = 120;

        /// <summary>
        /// Gets the files for the videos the settings let through.
        /// </summary>
        /// <param name="catalog">hanime.tv's catalog.</param>
        /// <param name="config">The settings, for the filters.</param>
        /// <param name="streamUrl">Gives a video's stream link from its slug.</param>
        public static IReadOnlyList<LibraryFile> Build(IReadOnlyList<HanimeVideo> catalog, PluginConfiguration config, Func<string, string> streamUrl)
        {
            var files = new List<LibraryFile>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in Series(Visible(catalog, config)))
            {
                // Two series whose names only differ in characters a file name cannot hold
                var folder = FileName(series.Name);
                for (var n = 2; !folders.Add(folder); n++)
                {
                    folder = FileName(series.Name) + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                }

                files.Add(new LibraryFile(Path.Combine(folder, "tvshow.nfo"), SeriesNfo(series)));
                foreach (var (episode, number) in series.Episodes)
                {
                    var name = Path.Combine(folder, "Season 01", string.Create(CultureInfo.InvariantCulture, $"{folder} S01E{number:00}"));
                    files.Add(new LibraryFile(name + ".strm", streamUrl(episode.Slug) + "\n"));
                    files.Add(new LibraryFile(name + ".nfo", EpisodeNfo(series, episode, number)));
                }
            }

            return files;
        }

        /// <summary>
        /// The videos the settings let through.
        /// </summary>
        public static List<HanimeVideo> Visible(IReadOnlyList<HanimeVideo> catalog, PluginConfiguration config)
        {
            var hidden = config.NormalizedHiddenTags();
            return catalog
                .Where(v => !(config.HideCensored && v.IsCensored))
                .Where(v => hidden.Count == 0 || !v.Tags.Any(hidden.Contains))
                .ToList();
        }

        /// <summary>
        /// Groups the videos into series by name ("Title 2" is episode 2 of "Title") and numbers
        /// the episodes: by the numbers in their names, or in order of release where those
        /// are missing or taken twice.
        /// </summary>
        public static IReadOnlyList<LibrarySeries> Series(IEnumerable<HanimeVideo> videos) =>
            videos
                .GroupBy(v => v.SeriesInfo().Series, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var ordered = g
                        .OrderBy(v => v.SeriesInfo().Episode)
                        .ThenBy(v => v.ReleasedAt ?? v.CreatedAt ?? DateTime.MaxValue)
                        .ThenBy(v => v.Slug, StringComparer.Ordinal)
                        .ToList();
                    var numbers = ordered.Select(v => v.SeriesInfo().Episode).ToList();
                    if (numbers.Distinct().Count() != numbers.Count)
                    {
                        numbers = Enumerable.Range(1, ordered.Count).ToList();
                    }

                    // The name as most episodes spell it
                    var name = g.Select(v => v.SeriesInfo().Series)
                        .GroupBy(n => n, StringComparer.Ordinal)
                        .OrderByDescending(n => n.Count())
                        .ThenBy(n => n.Key, StringComparer.Ordinal)
                        .First().Key;
                    return new LibrarySeries(name, ordered.Zip(numbers).ToList());
                })
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// A name that every file system takes as a file or folder name.
        /// </summary>
        public static string FileName(string name)
        {
            var builder = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                builder.Append(char.IsControl(c) || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? ' ' : c);
            }

            var result = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (result.Length > MaxNameLength)
            {
                result = result[..MaxNameLength];
            }

            // Windows drops trailing dots and spaces; "." and ".." are no names
            result = result.TrimEnd('.', ' ');
            return result.Length == 0 ? "Unnamed" : result;
        }

        private static string SeriesNfo(LibrarySeries series)
        {
            var first = series.Episodes[0].Video;
            var root = new XElement(
                "tvshow",
                new XElement("title", series.Name),
                Optional("plot", first.PlainDescription()),
                Genres(series.Episodes.SelectMany(e => e.Video.Tags)),
                series.Episodes.Select(e => e.Video.Brand).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Select(b => new XElement("studio", b)),
                Optional("premiered", Date(first.ReleasedAt)),
                Optional("year", first.ReleasedAt?.Year.ToString(CultureInfo.InvariantCulture)),
                new XElement("mpaa", AdultRating),
                Optional("dateadded", Timestamp(series.Episodes.Min(e => e.Video.CreatedAt))),
                // Portrait cover as poster, landscape poster as backdrop
                Image("poster", first.PosterUrl),
                first.ThumbnailUrl is { } backdrop ? new XElement("fanart", new XElement("thumb", backdrop)) : null);
            return Serialize(root);
        }

        private static string EpisodeNfo(LibrarySeries series, HanimeVideo video, int number)
        {
            var votes = video.Likes + video.Dislikes;
            var root = new XElement(
                "episodedetails",
                new XElement("title", video.Name),
                new XElement("showtitle", series.Name),
                new XElement("season", 1),
                new XElement("episode", number),
                Optional("plot", video.PlainDescription()),
                Optional("aired", Date(video.ReleasedAt)),
                Optional("premiered", Date(video.ReleasedAt)),
                Optional("year", video.ReleasedAt?.Year.ToString(CultureInfo.InvariantCulture)),
                Optional("studio", video.Brand),
                Genres(video.Tags),
                video.IsCensored ? new XElement("tag", "censored") : null,
                votes > 0 ? new XElement("rating", Math.Round(10.0 * video.Likes / votes, 1).ToString("0.0", CultureInfo.InvariantCulture)) : null,
                new XElement("mpaa", AdultRating),
                // The upload time: "Date added" sorts by it
                Optional("dateadded", Timestamp(video.CreatedAt)),
                new XElement("uniqueid", new XAttribute("type", ProviderName), new XAttribute("default", "true"), video.Slug),
                // Episodes show a landscape image
                Image("poster", video.ThumbnailUrl ?? video.PosterUrl));
            return Serialize(root);
        }

        private static IEnumerable<XElement> Genres(IEnumerable<string> tags) =>
            tags.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !string.Equals(t, "censored", StringComparison.OrdinalIgnoreCase) && !string.Equals(t, "uncensored", StringComparison.OrdinalIgnoreCase))
                .Select(t => new XElement("genre", Title(t)));

        /// <summary>
        /// hanime.tv's tags are lower case: "hd" and "pov" become "HD" and "POV", "big boobs"
        /// "Big Boobs".
        /// </summary>
        internal static string Title(string name) => name.Length switch
        {
            <= 3 when !name.Contains(' ', StringComparison.Ordinal) => name.ToUpperInvariant(),
            _ when name is "bdsm" or "milf" => name.ToUpperInvariant(),
            _ when char.IsLower(name[0]) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name),
            _ => name,
        };

        private static XElement? Optional(string name, string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

        private static XElement? Image(string aspect, string? url) =>
            string.IsNullOrWhiteSpace(url) ? null : new XElement("thumb", new XAttribute("aspect", aspect), url);

        private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static string? Timestamp(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Serialize(XElement root) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" + root.ToString() + "\n";
    }

    /// <summary>
    /// A series and its numbered episodes.
    /// </summary>
    public sealed record LibrarySeries(string Name, IReadOnlyList<(HanimeVideo Video, int Number)> Episodes);
}
