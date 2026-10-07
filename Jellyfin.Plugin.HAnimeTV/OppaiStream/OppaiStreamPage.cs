using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.HAnimeTV.Hentai;

namespace Jellyfin.Plugin.HAnimeTV.OppaiStream
{
    /// <summary>
    /// An episode as the site's search lists it.
    /// </summary>
    public sealed record OppaiStreamListing(string Url, string Title, string? ThumbnailUrl);

    /// <summary>
    /// A stream of an episode: an MP4 file or an HLS playlist, per resolution.
    /// </summary>
    public sealed record OppaiStreamStream(string Url, string Label, int Height)
    {
        public bool IsHls => Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An episode, as read from its page.
    /// </summary>
    public sealed class OppaiStreamEpisode
    {
        public required string Url { get; init; }

        public required string Title { get; init; }

        public required string SeriesName { get; init; }

        public int Number { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

        public string? Studio { get; init; }

        public string? PosterUrl { get; init; }

        public string? ThumbnailUrl { get; init; }

        /// <summary>
        /// Gets a value indicating whether the episode's streams are files rather than HLS.
        /// </summary>
        public bool StreamIsFile { get; init; }

        public IReadOnlyList<HentaiSubtitle> Subtitles { get; init; } = Array.Empty<HentaiSubtitle>();

        /// <summary>
        /// Gets when the plugin first listed the episode: the site shows no upload dates.
        /// </summary>
        public DateTimeOffset FirstSeen { get; init; }

        public DateTimeOffset FetchedAt { get; init; }
    }

    /// <summary>
    /// What an episode's page holds.
    /// </summary>
    public sealed record OppaiStreamPageContent(
        string? Title,
        string? Description,
        IReadOnlyList<string> Genres,
        string? Studio,
        string? PosterUrl,
        IReadOnlyList<OppaiStreamStream> Streams,
        IReadOnlyList<HentaiSubtitle> Subtitles);

    /// <summary>
    /// Reads oppai.stream's pages, the way its Aniyomi extension does: the search's episode
    /// cards (<c>div.episode-shown</c>), and on an episode's page its title, description, tags,
    /// studio, the streams per resolution (<c>var availableres = {…}</c>) and the subtitle tracks.
    /// </summary>
    public static partial class OppaiStreamPage
    {
        /// <summary>
        /// Gets the episodes of a search page, in page order.
        /// </summary>
        public static IReadOnlyList<OppaiStreamListing> Listing(string html, Uri page)
        {
            var result = new List<OppaiStreamListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cards = EpisodeShown().Matches(html).Select(m => m.Index).ToList();
            for (var i = 0; i < cards.Count; i++)
            {
                var card = html[cards[i]..(i + 1 < cards.Count ? cards[i + 1] : html.Length)];
                if (Link().Match(card) is not { Success: true } link || Link(page, link) is not { } url || !seen.Add(url))
                {
                    continue;
                }

                var title = TitleEp().Match(card) is { Success: true } t ? Text(t.Groups["title"].Value) : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var thumbnail = CoverImage().Match(card) is { Success: true } img ? Absolute(page, Attribute(img.Value, "src") ?? Attribute(img.Value, "data-src") ?? string.Empty) : null;
                result.Add(new OppaiStreamListing(url, title, thumbnail));
            }

            return result;
        }

        /// <summary>
        /// Reads an episode's page.
        /// </summary>
        public static OppaiStreamPageContent Episode(string html, Uri page)
        {
            var info = html.IndexOf("episode-info", StringComparison.Ordinal);
            var infoBlock = info >= 0 ? html[info..Math.Min(html.Length, info + 5000)] : string.Empty;
            var title = Heading().Match(infoBlock) is { Success: true } h1 ? Text(h1.Groups["title"].Value) : null;
            var studio = RedLink().Matches(infoBlock).Select(m => Text(m.Groups["text"].Value)).FirstOrDefault(t => t.Length > 0);

            string? description = null;
            if (DescriptionBlock().Match(html) is { Success: true } d)
            {
                description = Text(d.Groups["content"].Value);
                // The site ends its descriptions with "Watch … on Oppai Stream"
                var watch = description.LastIndexOf(" Watch ", StringComparison.Ordinal);
                description = (watch > 0 ? description[..watch] : description).Trim();
            }

            var genres = TagsBlock().Match(html) is { Success: true } tags
                ? Anchor().Matches(tags.Groups["content"].Value).Select(a => Text(a.Groups["text"].Value)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            var poster = VideoTag().Matches(html).Select(m => m.Value).FirstOrDefault(v => Attribute(v, "id") == "episode") is { } video
                ? Absolute(page, Attribute(video, "poster") ?? string.Empty)
                : null;

            var subtitles = Track().Matches(html)
                .Select(m => m.Value)
                .Where(t => Attribute(t, "kind") is "captions" or "subtitles")
                .Select(t => (Url: Absolute(page, Attribute(t, "src") ?? string.Empty), Label: Attribute(t, "label") ?? string.Empty, Language: Attribute(t, "srclang")))
                .Where(t => t.Url is not null)
                .Select(t => new HentaiSubtitle(LanguageCode(t.Language, t.Label), t.Label.Length > 0 ? t.Label : "Subtitles", t.Url!))
                .DistinctBy(t => t.Url)
                .ToList();

            return new OppaiStreamPageContent(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(description) ? null : description,
                genres,
                studio,
                poster,
                Streams(html, page),
                subtitles);
        }

        /// <summary>
        /// Reads <c>var availableres = {"1080": "https:\/\/…", "4k": "…"}</c>, best first.
        /// </summary>
        public static IReadOnlyList<OppaiStreamStream> Streams(string html, Uri page)
        {
            if (AvailableRes().Match(html) is not { Success: true } match)
            {
                return Array.Empty<OppaiStreamStream>();
            }

            var pairs = new List<(string Label, string Url)>();
            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
                pairs.AddRange(document.RootElement.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .Select(p => (p.Name, p.Value.GetString()!)));
            }
            catch (JsonException)
            {
                // Not JSON (single quotes, a trailing comma): split it as the extension does
                foreach (var part in match.Groups["json"].Value.Trim('{', '}', ' ').Split(','))
                {
                    var pair = part.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal)
                        .Replace("\\", string.Empty, StringComparison.Ordinal).Split(':', 2);
                    if (pair.Length == 2)
                    {
                        pairs.Add((pair[0].Trim(), pair[1].Trim()));
                    }
                }
            }

            return pairs
                .Select(p => (p.Label, Url: Absolute(page, p.Url)))
                .Where(p => p.Url is not null)
                .Select(p => new OppaiStreamStream(p.Url!, Resolution(p.Label), Height(p.Label)))
                .DistinctBy(s => s.Url)
                .OrderByDescending(s => s.Height)
                .ToList();
        }

        /// <summary>
        /// Splits "Title 3" or "Title Ep 3" into the series and the episode's number.
        /// </summary>
        public static (string Series, int? Number) SeriesAndNumber(string title)
        {
            title = title.Trim();
            if (EpisodeSuffix().Match(title) is { Success: true } m && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return (title[..m.Index].Trim(), number);
            }

            return (title, null);
        }

        /// <summary>
        /// Gets an episode link the way the site's script builds it: the episode's name in
        /// <c>?e=</c> may hold characters that must be escaped.
        /// </summary>
        internal static string? Link(Uri page, Match link)
        {
            var href = WebUtility.HtmlDecode(link.Groups["href"].Value).Trim();
            href = EpisodeName().Replace(href, m => Uri.EscapeDataString(Uri.UnescapeDataString(m.Value.Replace('+', ' '))));
            return Absolute(page, href);
        }

        /// <summary>
        /// Gets the language code of a subtitle track: its srclang, else the language its label
        /// names ("English"), else "und".
        /// </summary>
        internal static string LanguageCode(string? srclang, string label)
        {
            if (!string.IsNullOrWhiteSpace(srclang) && srclang.Trim().Length is 2 or 3)
            {
                return srclang.Trim().ToLowerInvariant();
            }

            var name = label.Trim();
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => c.TwoLetterISOLanguageName.Length == 2
                    && (string.Equals(c.EnglishName, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.NativeName, name, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(c.EnglishName + " ", StringComparison.OrdinalIgnoreCase)));
            return culture?.TwoLetterISOLanguageName ?? "und";
        }

        internal static string Text(string html) =>
            Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

        private static string Resolution(string label) => label.ToLowerInvariant() switch
        {
            "4k" => "2160p",
            var l when l.EndsWith('p') => l,
            var l => l + "p",
        };

        private static int Height(string label) =>
            label.Trim().ToLowerInvariant() == "4k" ? 2160
            : int.TryParse(label.Trim().TrimEnd('p', 'P'), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;

        private static string? Attribute(string tag, string name) =>
            Regex.Match(tag, @"\s" + Regex.Escape(name) + @"\s*=\s*([""'])(?<v>.*?)\1", RegexOptions.Singleline | RegexOptions.IgnoreCase) is { Success: true } m
                ? WebUtility.HtmlDecode(m.Groups["v"].Value).Trim()
                : null;

        private static string? Absolute(Uri page, string href)
        {
            href = WebUtility.HtmlDecode(href).Trim().Replace("\\/", "/", StringComparison.Ordinal);
            return href.Length > 0 && !href.StartsWith('#') && Uri.TryCreate(page, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.AbsoluteUri
                : null;
        }

        [GeneratedRegex(@"<div\b[^>]*class=""[^""]*\bepisode-shown\b", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeShown();

        // The card's link: its "exur" (the episode's address the site's script follows), else its href
        [GeneratedRegex(@"<a\b[^>]*?\b(?:exur)\s*=\s*""(?<href>[^""]+)""|<a\b[^>]*?\bhref\s*=\s*""(?<href>[^""#][^""]*)""", RegexOptions.IgnoreCase)]
        private static partial Regex Link();

        [GeneratedRegex(@"class=""[^""]*\btitle-ep\b[^""]*""[^>]*>(?<title>.*?)</", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex TitleEp();

        [GeneratedRegex(@"<img\b[^>]*class=""[^""]*\bcover-img-in\b[^""]*""[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex CoverImage();

        [GeneratedRegex(@"<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Heading();

        [GeneratedRegex(@"<a\b[^>]*class=""[^""]*\bred\b[^""]*""[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex RedLink();

        [GeneratedRegex(@"<div\b[^>]*class=""[^""]*\bdescription\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex DescriptionBlock();

        [GeneratedRegex(@"<div\b[^>]*class=""[^""]*\btags\b[^""]*""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex TagsBlock();

        [GeneratedRegex(@"<a\b[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Anchor();

        [GeneratedRegex(@"<video\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex VideoTag();

        [GeneratedRegex(@"<track\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Track();

        [GeneratedRegex(@"var\s+availableres\s*=\s*(?<json>\{.*?\})", RegexOptions.Singleline)]
        private static partial Regex AvailableRes();

        [GeneratedRegex(@"\s+(?:ep\.?|episode)?\s*(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeSuffix();

        // Up to "&f=" as the extension does: the name itself may hold a "&"
        [GeneratedRegex(@"(?<=[?&]e=)(?:.*?(?=&f=)|[^&]*)")]
        private static partial Regex EpisodeName();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Tag();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
