using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HAnimeTV.HentaiHaven
{
    /// <summary>
    /// A series as a listing shows it: its page, and the episodes listed under it, which tell
    /// whether the series changed since its page was read.
    /// </summary>
    public sealed record HentaiHavenListing(string Url, string Title, IReadOnlyList<string> EpisodeUrls);

    /// <summary>
    /// A series and its episodes, as read from its page.
    /// </summary>
    public sealed class HentaiHavenSeries
    {
        public required string Url { get; init; }

        public required string Title { get; init; }

        public string? Description { get; init; }

        public string? PosterUrl { get; init; }

        public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

        public string? Studio { get; init; }

        public int? Year { get; init; }

        public IReadOnlyList<HentaiHavenEpisode> Episodes { get; init; } = Array.Empty<HentaiHavenEpisode>();

        public DateTimeOffset FetchedAt { get; init; }
    }

    /// <summary>
    /// An episode of a series.
    /// </summary>
    public sealed record HentaiHavenEpisode(string Url, string Title, int Number, DateTime? ReleasedAt, string? ThumbnailUrl);

    /// <summary>
    /// A stream of an episode.
    /// </summary>
    public sealed record HentaiHavenStream(string Url, string Label, int Height)
    {
        public bool IsHls => Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads Hentai Haven's pages. The site runs WordPress with the Madara theme, which lists
    /// series like manga and their episodes like chapters, and its "player-logic" plugin, whose
    /// player page carries the keys its API gives the streams for.
    /// </summary>
    public static partial class HentaiHavenPage
    {
        /// <summary>
        /// Gets the series of a listing page (search results or an archive), in page order.
        /// </summary>
        public static IReadOnlyList<HentaiHavenListing> Listing(string html, Uri page)
        {
            var titles = PostTitle().Matches(html).ToList();
            var result = new List<HentaiHavenListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < titles.Count; i++)
            {
                var match = titles[i];
                if (Absolute(page, match.Groups["href"].Value) is not { } url || !seen.Add(url))
                {
                    continue;
                }

                // The episodes listed under the series, up to the next series
                var end = i + 1 < titles.Count ? titles[i + 1].Index : html.Length;
                var episodes = Anchor().Matches(html[match.Index..end])
                    .Select(a => Absolute(page, a.Groups["href"].Value))
                    .OfType<string>()
                    .Where(href => href.Length > url.Length && href.StartsWith(url, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                result.Add(new HentaiHavenListing(url, Text(match.Groups["title"].Value), episodes));
            }

            return result;
        }

        /// <summary>
        /// Reads a series' page; its episodes may be missing (see <see cref="ChaptersHolderId"/>).
        /// </summary>
        public static HentaiHavenSeries Series(string html, Uri page, DateTimeOffset now)
        {
            var summary = SummaryItems(html);
            var title = SeriesTitle().Match(html) is { Success: true } h1
                ? Text(Badges().Replace(h1.Groups["title"].Value, string.Empty))
                : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = WithoutSiteName(Meta(html, "og:title") ?? (TitleTag().Match(html) is { Success: true } t ? Text(t.Groups["title"].Value) : null));
            }

            var genres = GenresContent().Match(html) is { Success: true } genresContent
                ? Anchor().Matches(genresContent.Groups["content"].Value).Select(a => Text(a.Groups["text"].Value)).ToList()
                : Find(summary, "genre")?.Links ?? new List<string>();

            var poster = SummaryImage().Match(html) is { Success: true } image ? ImageUrl(image.Groups["attrs"].Value, page) : null;
            var studio = Find(summary, "studio", "brand", "producer", "author", "artist");
            var release = Find(summary, "release", "year", "aired");
            return new HentaiHavenSeries
            {
                Url = page.AbsoluteUri,
                Title = string.IsNullOrWhiteSpace(title) ? page.Segments.Last().Trim('/') : title,
                Description = Description(html),
                PosterUrl = poster ?? Meta(html, "og:image"),
                Genres = genres.Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Studio = studio is null ? null : studio.Links.FirstOrDefault() ?? NullIfEmpty(studio.Text),
                Year = release is not null && Year().Match(release.Text) is { Success: true } year ? int.Parse(year.Value, CultureInfo.InvariantCulture) : null,
                Episodes = Episodes(html, page, now),
                FetchedAt = now,
            };
        }

        /// <summary>
        /// Reads the episodes of a series' page, or of the theme's answer to the request for
        /// them, oldest first.
        /// </summary>
        public static IReadOnlyList<HentaiHavenEpisode> Episodes(string html, Uri page, DateTimeOffset now)
        {
            var seriesUrl = page.AbsoluteUri;
            var items = Chapter().Matches(html)
                .Select(m => m.Groups["body"].Value)
                .Select(body => (Anchor: Anchor().Match(body), Body: body))
                .Where(c => c.Anchor.Success)
                .Select(c => (Url: Absolute(page, c.Anchor.Groups["href"].Value), Text: Text(c.Anchor.Groups["text"].Value), c.Body))
                .Where(c => c.Url is not null)
                .Select(c => (Url: c.Url!, c.Text, c.Body))
                .ToList();

            // Newest first, as the theme lists them
            var episodes = new List<HentaiHavenEpisode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var position = items.Count;
            foreach (var (url, text, body) in items)
            {
                position--;
                if (!seen.Add(url))
                {
                    continue;
                }

                var date = ReleaseDate().Match(body) is { Success: true } d ? ParseDate(Text(d.Groups["date"].Value), now) : null;
                var thumbnail = Image().Match(body) is { Success: true } img ? ImageUrl(img.Groups["attrs"].Value, page) : null;
                var name = text.Length > 0 ? text : url.TrimEnd('/').Split('/').Last();
                episodes.Add(new HentaiHavenEpisode(url, name, EpisodeNumber(name, url) ?? (position + 1), date, thumbnail));
            }

            if (episodes.Count == 0)
            {
                // Pages without the theme's markup: links below the series' address
                position = 0;
                foreach (Match anchor in Anchor().Matches(html))
                {
                    if (Absolute(page, anchor.Groups["href"].Value) is { } url
                        && url.Length > seriesUrl.Length && url.StartsWith(seriesUrl, StringComparison.Ordinal)
                        && EpisodePath().IsMatch(url[seriesUrl.Length..]) && seen.Add(url))
                    {
                        var text = Text(anchor.Groups["text"].Value);
                        episodes.Add(new HentaiHavenEpisode(url, text.Length > 0 ? text : url, EpisodeNumber(text, url) ?? ++position, null, null));
                    }
                }
            }

            return episodes.OrderBy(e => e.Number).ThenBy(e => e.Url, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Gets the post id the theme loads a series' episodes for, when its page lists none.
        /// </summary>
        public static string? ChaptersHolderId(string html) =>
            ChaptersHolder().Match(html) is { Success: true } holder ? holder.Groups["id"].Value
            : RatingPostId().Match(html) is { Success: true } rating ? rating.Groups["id"].Value
            : null;

        /// <summary>
        /// Gets the address of the player on an episode's page.
        /// </summary>
        public static Uri? PlayerFrame(string html, Uri page)
        {
            var start = html.IndexOf("player_logic_item", StringComparison.Ordinal);
            var match = start >= 0 ? Iframe().Match(html, start) : Match.Empty;
            if (!match.Success)
            {
                match = Iframe().Matches(html).FirstOrDefault(m => m.Groups["src"].Value.Contains("player", StringComparison.OrdinalIgnoreCase)) ?? Match.Empty;
            }

            return match.Success && Absolute(page, match.Groups["src"].Value) is { } src ? new Uri(src) : null;
        }

        /// <summary>
        /// Gets the keys on the player's page that its API takes ("en" and "iv").
        /// </summary>
        public static (string En, string Iv)? PlayerKeys(string html) =>
            En().Match(html) is { Success: true } en && Iv().Match(html) is { Success: true } iv
                ? (en.Groups["value"].Value, iv.Groups["value"].Value)
                : null;

        /// <summary>
        /// Reads the player API's answer: { "status": true, "data": { "sources": [{ "src", "type", "label" }] } }.
        /// </summary>
        public static IReadOnlyList<HentaiHavenStream> ApiSources(string json, Uri baseUrl)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<HentaiHavenStream>();
                }

                return Sorted(sources.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String)
                    .Select(s =>
                    {
                        var label = s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : string.Empty;
                        return (Url: Absolute(baseUrl, s.GetProperty("src").GetString()!), Label: label);
                    })
                    .Where(s => s.Url is not null)
                    .Select(s => new HentaiHavenStream(s.Url!, s.Label, Height(s.Label))));
            }
            catch (JsonException)
            {
                return Array.Empty<HentaiHavenStream>();
            }
        }

        /// <summary>
        /// Gets HLS and MP4 addresses written into a page, for players without the API.
        /// </summary>
        public static IReadOnlyList<HentaiHavenStream> DirectStreams(string html, Uri page) =>
            Sorted(MediaUrl().Matches(html)
                .Select(m => WebUtility.HtmlDecode(m.Value.Replace("\\/", "/", StringComparison.Ordinal)))
                .Select(url => Absolute(page, url))
                .OfType<string>()
                .Select(url => new HentaiHavenStream(url, string.Empty, Height(url))));

        /// <summary>
        /// Describes a page that is not what was expected: its title, what made it, and some of
        /// its links, so that a changed or different site can be told apart.
        /// </summary>
        public static string Describe(string html, Uri page)
        {
            var title = TitleTag().Match(html) is { Success: true } t ? Text(t.Groups["title"].Value) : null;
            var generator = Meta(html, "generator");
            var links = Anchor().Matches(html)
                .Select(a => Absolute(page, a.Groups["href"].Value))
                .OfType<string>()
                .Where(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && string.Equals(u.Host, page.Host, StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Length > 1)
                .Select(l => new Uri(l).PathAndQuery)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
            return $"{html.Length} characters"
                + (title is { Length: > 0 } ? $", titled \"{(title.Length > 80 ? title[..80] : title)}\"" : ", without a title")
                + (generator is not null ? $", made with {generator}" : string.Empty)
                + (links.Count > 0 ? ", with links such as " + string.Join(" ", links) : ", without links to its own pages");
        }

        /// <summary>
        /// Gets whether the page is a bot check (Cloudflare's) rather than the site.
        /// </summary>
        public static bool IsChallenge(string html) =>
            html.Contains("cf-chl", StringComparison.Ordinal)
            || html.Contains("challenge-platform", StringComparison.Ordinal)
            || html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads "April 3, 2021", "2021-04-03" or "3 days ago".
        /// </summary>
        internal static DateTime? ParseDate(string text, DateTimeOffset now)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (Ago().Match(text) is { Success: true } ago)
            {
                var amount = int.Parse(ago.Groups["amount"].Value, CultureInfo.InvariantCulture);
                var unit = ago.Groups["unit"].Value.ToLowerInvariant();
                var time = unit switch
                {
                    "sec" or "second" => now.AddSeconds(-amount),
                    "min" or "minute" => now.AddMinutes(-amount),
                    "hour" => now.AddHours(-amount),
                    "day" => now.AddDays(-amount),
                    "week" => now.AddDays(-7 * amount),
                    "month" => now.AddMonths(-amount),
                    _ => now.AddYears(-amount),
                };
                return time.UtcDateTime;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
        }

        /// <summary>
        /// Reads "Episode 3", "Ep. 3" or ".../episode-3/"; null without a number.
        /// </summary>
        internal static int? EpisodeNumber(string text, string url)
        {
            foreach (var candidate in new[] { text, url })
            {
                if (EpisodeWord().Match(candidate) is { Success: true } m && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    return n;
                }
            }

            return LastNumber().Match(text) is { Success: true } last && int.TryParse(last.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number < 1000
                ? number
                : null;
        }

        /// <summary>
        /// Text without tags and entities, on one line.
        /// </summary>
        internal static string Text(string html) =>
            Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

        private static string? Description(string html)
        {
            var match = SummaryContent().Match(html);
            if (!match.Success)
            {
                match = DescriptionSummary().Match(html);
            }

            string? text = null;
            if (match.Success)
            {
                // Paragraphs on lines of their own
                var content = Paragraph().Replace(match.Groups["content"].Value, "\n");
                text = string.Join("\n\n", content.Split('\n').Select(Text).Where(l => l.Length > 0));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = Meta(html, "og:description") ?? Meta(html, "description");
            }

            return string.IsNullOrWhiteSpace(text) ? null : SynopsisLabel().Replace(text, string.Empty).Trim();
        }

        private static List<SummaryItem> SummaryItems(string html) =>
            SummaryPair().Matches(html)
                .Select(m => new SummaryItem(
                    Text(m.Groups["heading"].Value).TrimEnd(':').ToLowerInvariant(),
                    Text(m.Groups["content"].Value),
                    Anchor().Matches(m.Groups["content"].Value).Select(a => Text(a.Groups["text"].Value)).Where(t => t.Length > 0).ToList()))
                .ToList();

        private static SummaryItem? Find(List<SummaryItem> items, params string[] headings) =>
            items.FirstOrDefault(i => headings.Any(h => i.Heading.Contains(h, StringComparison.Ordinal)));

        private static IReadOnlyList<HentaiHavenStream> Sorted(IEnumerable<HentaiHavenStream> streams) =>
            streams.Where(s => s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(s => s.Url)
                // HLS first: the plugin's links are HLS playlists
                .OrderByDescending(s => s.IsHls)
                .ThenByDescending(s => s.Height)
                .ToList();

        private static int Height(string text) =>
            HeightPattern().Match(text) is { Success: true } m && int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;

        private static string? ImageUrl(string attributes, Uri page)
        {
            foreach (var name in (string[])["data-src", "data-lazy-src", "data-original", "src"])
            {
                if (Attribute(attributes, name) is { } value && !value.StartsWith("data:", StringComparison.Ordinal) && Absolute(page, value) is { } url)
                {
                    return url;
                }
            }

            return Attribute(attributes, "srcset") is { } srcset && Absolute(page, srcset.Split(',')[0].Trim().Split(' ')[0]) is { } first ? first : null;
        }

        private static string? Attribute(string attributes, string name) =>
            Regex.Match(attributes, @"(?:^|\s)" + Regex.Escape(name) + @"\s*=\s*([""'])(?<v>.*?)\1", RegexOptions.Singleline) is { Success: true } m
                ? WebUtility.HtmlDecode(m.Groups["v"].Value).Trim()
                : null;

        private static string? Meta(string html, string property)
        {
            foreach (Match tag in MetaTag().Matches(html))
            {
                var attributes = tag.Groups["attrs"].Value;
                if (string.Equals(Attribute(attributes, "property") ?? Attribute(attributes, "name"), property, StringComparison.OrdinalIgnoreCase))
                {
                    return NullIfEmpty(Attribute(attributes, "content"));
                }
            }

            return null;
        }

        private static string? WithoutSiteName(string? title) =>
            title is null ? null : SiteNameSuffix().Replace(title, string.Empty).Trim();

        private static string? Absolute(Uri page, string href)
        {
            href = WebUtility.HtmlDecode(href).Trim();
            return href.Length > 0 && !href.StartsWith('#') && Uri.TryCreate(page, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.AbsoluteUri
                : null;
        }

        private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private sealed record SummaryItem(string Heading, string Text, List<string> Links);

        [GeneratedRegex(@"class=""[^""]*\bpost-title\b[^""]*""[^>]*>\s*(?:<h\d[^>]*>\s*)?(?:<span[^>]*>.*?</span>\s*)*<a[^>]*\bhref=""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex PostTitle();

        [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Anchor();

        [GeneratedRegex(@"<li\b[^>]*class=""[^""]*\bwp-manga-chapter\b[^""]*""[^>]*>(?<body>.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Chapter();

        [GeneratedRegex(@"class=""[^""]*\bchapter-release-date\b[^""]*""[^>]*>(?<date>.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex ReleaseDate();

        [GeneratedRegex(@"<img\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Image();

        [GeneratedRegex(@"class=""[^""]*\bpost-title\b[^""]*""[^>]*>\s*<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex SeriesTitle();

        [GeneratedRegex(@"<span[^>]*class=""[^""]*\bmanga-title-badges\b[^""]*""[^>]*>.*?</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Badges();

        [GeneratedRegex(@"<title[^>]*>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex TitleTag();

        [GeneratedRegex(@"class=""[^""]*\bgenres-content\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex GenresContent();

        [GeneratedRegex(@"class=""[^""]*\bsummary_image\b[^""]*""[^>]*>.*?<img\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex SummaryImage();

        [GeneratedRegex(@"class=""[^""]*\bsummary-heading\b[^""]*""[^>]*>(?<heading>.*?)</div>\s*<div[^>]*class=""[^""]*\bsummary-content\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex SummaryPair();

        [GeneratedRegex(@"class=""[^""]*\bsummary__content\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex SummaryContent();

        [GeneratedRegex(@"class=""[^""]*\bdescription-summary\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex DescriptionSummary();

        [GeneratedRegex(@"<\s*(?:br\s*/?|/p|p\b[^>]*)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex Paragraph();

        [GeneratedRegex(@"^\s*synopsis\s*:?\s*", RegexOptions.IgnoreCase)]
        private static partial Regex SynopsisLabel();

        [GeneratedRegex(@"<meta\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex MetaTag();

        [GeneratedRegex(@"\s+[-–—|]\s+[^-–—|]*\bhaven\b[^-–—|]*$", RegexOptions.IgnoreCase)]
        private static partial Regex SiteNameSuffix();

        [GeneratedRegex(@"\b(?:19|20)\d\d\b")]
        private static partial Regex Year();

        [GeneratedRegex(@"id=""manga-chapters-holder""[^>]*\bdata-id=""(?<id>\d+)""|\bdata-id=""(?<id>\d+)""[^>]*id=""manga-chapters-holder""", RegexOptions.IgnoreCase)]
        private static partial Regex ChaptersHolder();

        [GeneratedRegex(@"class=""[^""]*\brating-post-id\b[^""]*""[^>]*\bvalue=""(?<id>\d+)""", RegexOptions.IgnoreCase)]
        private static partial Regex RatingPostId();

        [GeneratedRegex(@"<iframe\b[^>]*?\bsrc\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Iframe();

        [GeneratedRegex(@"\ben\s*=\s*[""'](?<value>[^""']*)[""']")]
        private static partial Regex En();

        [GeneratedRegex(@"\biv\s*=\s*[""'](?<value>[^""']*)[""']")]
        private static partial Regex Iv();

        [GeneratedRegex(@"https?:(?:\\?/){2}[^""'\s<>]+?\.(?:m3u8|mp4)(?:\?[^""'\s<>]*)?(?=[""'\s<>])", RegexOptions.IgnoreCase)]
        private static partial Regex MediaUrl();

        [GeneratedRegex(@"(?<h>\d{3,4})\s*[pP]\b|\b(?<h>\d{3,4})[pP]?_\d+[kK]")]
        private static partial Regex HeightPattern();

        [GeneratedRegex(@"(?<amount>\d+)\s*(?<unit>sec|second|min|minute|hour|day|week|month|year)s?\s+ago", RegexOptions.IgnoreCase)]
        private static partial Regex Ago();

        [GeneratedRegex(@"(?:episode|\bep)[\s._-]*(?<n>\d{1,3})\b", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeWord();

        [GeneratedRegex(@"\d+(?=\D*$)")]
        private static partial Regex LastNumber();

        [GeneratedRegex(@"episode|ep-?\d|/\d+/?$", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodePath();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Tag();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
