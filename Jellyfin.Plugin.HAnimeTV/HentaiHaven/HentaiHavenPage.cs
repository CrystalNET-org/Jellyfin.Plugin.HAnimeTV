using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HAnimeTV.HentaiHaven
{
    /// <summary>
    /// An episode as the site's lists show it.
    /// </summary>
    /// <param name="Number">The number on its card ("EP 2"), if any.</param>
    public sealed record HentaiHavenListing(string Url, string Title, string? ThumbnailUrl, int? Number);

    /// <summary>
    /// An episode, as read from its page.
    /// </summary>
    public sealed class HentaiHavenEpisode
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

        public DateTime? ReleasedAt { get; init; }

        public DateTime? UploadedAt { get; init; }

        public long Views { get; init; }

        public long Likes { get; init; }

        public long Dislikes { get; init; }

        public DateTimeOffset FetchedAt { get; init; }
    }

    /// <summary>
    /// What an episode's page holds.
    /// </summary>
    public sealed record HentaiHavenPageContent(
        string? Title,
        string? SeriesName,
        string? Studio,
        string? Description,
        IReadOnlyList<string> Genres,
        string? PosterUrl,
        string? ThumbnailUrl,
        DateTime? ReleasedAt,
        DateTime? UploadedAt,
        long Views,
        long Likes,
        long Dislikes,
        Uri? Player);

    /// <summary>
    /// A stream of an episode.
    /// </summary>
    /// <param name="Referer">The page the player loads it from, which its host may check.</param>
    public sealed record HentaiHavenStream(string Url, string Label, int Height, string? Referer = null)
    {
        public bool IsHls => Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads Hentai Haven's pages (a Next.js site): the lists of episodes (cards linking to
    /// <c>/watch/&lt;series&gt;-episode-&lt;n&gt;/</c>), an episode's page with its details and
    /// player, and the player's page at its video host (nhplayer), whose links carry the
    /// video's address.
    /// </summary>
    public static partial class HentaiHavenPage
    {
        /// <summary>
        /// Gets the episodes of a list page (the home page and its <c>?page=N</c>), in page order.
        /// </summary>
        public static IReadOnlyList<HentaiHavenListing> Listing(string html, Uri page)
        {
            var result = new List<HentaiHavenListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match card in Card().Matches(html))
            {
                if (Absolute(page, card.Groups["href"].Value) is not { } url || !IsEpisode(url) || !seen.Add(url))
                {
                    continue;
                }

                var body = card.Groups["body"].Value;
                var title = CardTitle().Match(body) is { Success: true } t ? Text(t.Groups["title"].Value)
                    : Image().Match(body) is { Success: true } img ? Attribute(img.Value, "alt")
                    : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var thumbnail = Image().Match(body) is { Success: true } image ? ImageUrl(image.Value, page) : null;
                var number = Badge().Match(body) is { Success: true } badge && int.TryParse(badge.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
                result.Add(new HentaiHavenListing(url, title, thumbnail, number));
            }

            return result;
        }

        /// <summary>
        /// Gets the last page the list's pagination links to, or 1.
        /// </summary>
        public static int LastPage(string html) =>
            PageLink().Matches(html).Select(m => int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1).DefaultIfEmpty(1).Max();

        /// <summary>
        /// Reads an episode's page.
        /// </summary>
        public static HentaiHavenPageContent Episode(string html, Uri page)
        {
            var title = VideoTitle().Match(html) is { Success: true } h1 ? Text(h1.Groups["title"].Value) : null;
            var details = Details().Matches(html)
                .GroupBy(m => Text(m.Groups["name"].Value).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Groups["value"].Value);

            string? Detail(string name) => details.TryGetValue(name, out var value) && Text(value) is { Length: > 0 } text ? text : null;

            var genres = Tags().Match(html) is { Success: true } tags
                ? Anchor().Matches(tags.Groups["content"].Value)
                    .Where(a => a.Groups["href"].Value.Contains("/genre/", StringComparison.Ordinal))
                    .Select(a => Text(a.Groups["text"].Value))
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var description = VideoDescription().Match(html) is { Success: true } d ? Paragraphs(d.Groups["content"].Value) : null;
            var poster = Cover().Match(html) is { Success: true } cover && Image().Match(cover.Value) is { Success: true } img ? ImageUrl(img.Value, page) : null;

            // The episode's landscape image: in the list of the series' episodes, next to its link
            string? thumbnail = null;
            foreach (Match link in LinkedImage().Matches(html))
            {
                if (Absolute(page, link.Groups["href"].Value) == page.AbsoluteUri && ImageUrl(link.Groups["img"].Value, page) is { } url)
                {
                    thumbnail = url;
                    break;
                }
            }

            Uri? player = null;
            var start = PlayerBlock().Match(html);
            if (start.Success && Iframe().Match(html, start.Index) is { Success: true } iframe && Absolute(page, iframe.Groups["src"].Value) is { } src)
            {
                player = new Uri(src);
            }

            return new HentaiHavenPageContent(
                title,
                Detail("series"),
                Detail("brand"),
                string.IsNullOrWhiteSpace(description) ? null : description,
                genres,
                poster ?? Meta(html, "og:image"),
                thumbnail,
                Date(Detail("release date")),
                Date(Detail("upload date")),
                Count(Detail("views")),
                Count(LikeCount().Match(html) is { Success: true } l ? l.Groups["n"].Value : null),
                Count(DislikeCount().Match(html) is { Success: true } dl ? dl.Groups["n"].Value : null),
                player);
        }

        /// <summary>
        /// Reads the player's page at the video host: its servers (<c>data-id="/player.php?vid=…"</c>),
        /// each with the video's address in <c>vid</c> ("address|expiry|signature" in base64).
        /// </summary>
        /// <returns>Per server, the address of its player and of the video.</returns>
        public static IReadOnlyList<(Uri Player, string? Video)> PlayerServers(string html, Uri page)
        {
            var result = new List<(Uri, string?)>();
            foreach (Match server in Server().Matches(html))
            {
                if (Absolute(page, server.Groups["path"].Value) is not { } player)
                {
                    continue;
                }

                var uri = new Uri(player);
                var vid = System.Web.HttpUtility.ParseQueryString(uri.Query)["vid"];
                result.Add((uri, DecodeVid(vid)));
            }

            return result;
        }

        /// <summary>
        /// Reads a <c>vid</c>: base64 of "address|expiry|signature"; null if it holds no address.
        /// </summary>
        internal static string? DecodeVid(string? vid)
        {
            if (string.IsNullOrWhiteSpace(vid))
            {
                return null;
            }

            try
            {
                var base64 = vid.Trim().Replace('-', '+').Replace('_', '/');
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=')));
                var address = text.Split('|')[0].Trim();
                return Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets HLS and MP4 addresses written into a page (a player's sources or script).
        /// </summary>
        public static IReadOnlyList<HentaiHavenStream> DirectStreams(string html, Uri page) =>
            MediaUrl().Matches(html)
                .Select(m => WebUtility.HtmlDecode(m.Value.Replace("\\/", "/", StringComparison.Ordinal)))
                .Select(url => Absolute(page, url))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(url => new HentaiHavenStream(url, Label(url), Height(url), page.AbsoluteUri))
                // HLS first, then the best
                .OrderByDescending(s => s.IsHls)
                .ThenByDescending(s => s.Height)
                .ToList();

        /// <summary>
        /// Splits "Title Episode 3" into the series and the episode's number.
        /// </summary>
        public static (string Series, int? Number) SeriesAndNumber(string title)
        {
            title = title.Trim();
            return EpisodeSuffix().Match(title) is { Success: true } m && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? (title[..m.Index].Trim(), number)
                : (title, null);
        }

        /// <summary>
        /// Gets the number in an episode's address ("…-episode-3/").
        /// </summary>
        public static int? NumberInUrl(string url) =>
            UrlEpisode().Match(url) is { Success: true } m && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

        /// <summary>
        /// Gets whether the page is a bot check (Cloudflare's) rather than the site.
        /// </summary>
        public static bool IsChallenge(string html) =>
            html.Contains("cf-chl", StringComparison.Ordinal)
            || html.Contains("challenge-platform", StringComparison.Ordinal)
            || html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase);

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
        /// Text without tags and entities, on one line.
        /// </summary>
        internal static string Text(string html) =>
            Whitespace().Replace(WebUtility.HtmlDecode(Comment().Replace(Tag().Replace(html, " "), string.Empty)), " ").Trim();

        internal static bool IsEpisode(string url) => url.Contains("/watch/", StringComparison.Ordinal);

        private static string Paragraphs(string html) =>
            string.Join("\n\n", Paragraph().Replace(html, "\n").Split('\n').Select(Text).Where(l => l.Length > 0));

        private static DateTime? Date(string? text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

        private static long Count(string? text) =>
            long.TryParse(new string((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static string Label(string url) => Height(url) is > 0 and var h ? h.ToString(CultureInfo.InvariantCulture) + "p" : (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "HLS" : "MP4");

        private static int Height(string url) =>
            HeightPattern().Match(url) is { Success: true } m && int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;

        private static string? ImageUrl(string tag, Uri page)
        {
            foreach (var name in (string[])["src", "data-src", "data-lazy-src"])
            {
                if (Attribute(tag, name) is { } value && !value.StartsWith("data:", StringComparison.Ordinal) && Absolute(page, value) is { } url)
                {
                    return url;
                }
            }

            return null;
        }

        private static string? Attribute(string tag, string name) =>
            Regex.Match(tag, @"\s" + Regex.Escape(name) + @"\s*=\s*([""'])(?<v>.*?)\1", RegexOptions.Singleline | RegexOptions.IgnoreCase) is { Success: true } m
                ? WebUtility.HtmlDecode(m.Groups["v"].Value).Trim()
                : null;

        private static string? Meta(string html, string property)
        {
            foreach (Match tag in MetaTag().Matches(html))
            {
                if (string.Equals(Attribute(tag.Value, "property") ?? Attribute(tag.Value, "name"), property, StringComparison.OrdinalIgnoreCase))
                {
                    return Attribute(tag.Value, "content") is { Length: > 0 } content ? content : null;
                }
            }

            return null;
        }

        private static string? Absolute(Uri page, string href)
        {
            href = WebUtility.HtmlDecode(href).Trim();
            return href.Length > 0 && !href.StartsWith('#') && Uri.TryCreate(page, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.AbsoluteUri
                : null;
        }

        [GeneratedRegex(@"<a\b[^>]*class=""[^""]*\ba_item\b[^""]*""[^>]*\bhref=""(?<href>[^""]+)""[^>]*>(?<body>.*?)</a>|<a\b[^>]*\bhref=""(?<href>[^""]+)""[^>]*class=""[^""]*\ba_item\b[^""]*""[^>]*>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Card();

        [GeneratedRegex(@"class=""[^""]*\bvideo_title\b[^""]*""[^>]*>(?<title>.*?)</", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex CardTitle();

        [GeneratedRegex(@"class=""card_badge""[^>]*>\s*EP\s*(?<n>\d{1,4})\s*<", RegexOptions.IgnoreCase)]
        private static partial Regex Badge();

        [GeneratedRegex(@"[?&]page=(?<n>\d{1,5})\b")]
        private static partial Regex PageLink();

        [GeneratedRegex(@"<h1\b[^>]*class=""[^""]*\bvideo_title\b[^""]*""[^>]*>(?<title>.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex VideoTitle();

        // <div class="r_item half"><span>Brand</span><span class="sub_r">…</span></div>
        [GeneratedRegex(@"class=""r_item[^""]*""[^>]*>\s*<span[^>]*>(?<name>[^<]*)</span>\s*<span[^>]*class=""sub_r""[^>]*>(?<value>.*?)</span>\s*</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Details();

        [GeneratedRegex(@"<div\b[^>]*class=""video_tags[^""]*""[^>]*>(?<content>.*?)(?:<div\b[^>]*class=""video_description|</div>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Tags();

        [GeneratedRegex(@"class=""video_description""[^>]*>(?<content>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex VideoDescription();

        [GeneratedRegex(@"<div\b[^>]*class=""cover""[^>]*>\s*<img\b[^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Cover();

        [GeneratedRegex(@"<div\b[^>]*class=""player""", RegexOptions.IgnoreCase)]
        private static partial Regex PlayerBlock();

        [GeneratedRegex(@"<iframe\b[^>]*?\bsrc\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Iframe();

        [GeneratedRegex(@"class=""ln""[^>]*>(?<n>[\d,.]+)<")]
        private static partial Regex LikeCount();

        [GeneratedRegex(@"class=""dn""[^>]*>(?<n>[\d,.]+)<")]
        private static partial Regex DislikeCount();

        [GeneratedRegex(@"\bdata-id\s*=\s*[""'](?<path>[^""']*player\.php\?[^""']+)[""']", RegexOptions.IgnoreCase)]
        private static partial Regex Server();

        [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Image();

        [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>\s*(?<img><img\b[^>]*>)", RegexOptions.IgnoreCase)]
        private static partial Regex LinkedImage();

        [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Anchor();

        [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex MetaTag();

        [GeneratedRegex(@"<title[^>]*>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex TitleTag();

        [GeneratedRegex(@"https?:(?:\\?/){2}[^""'\s<>()]+?\.(?:m3u8|mp4)(?:\?[^""'\s<>()]*)?(?=[""'\s<>()]|$)", RegexOptions.IgnoreCase)]
        private static partial Regex MediaUrl();

        [GeneratedRegex(@"(?<h>\d{3,4})[pP]\b")]
        private static partial Regex HeightPattern();

        [GeneratedRegex(@"\s+(?:episode|ep\.?)\s*(?<n>\d{1,4})$", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeSuffix();

        [GeneratedRegex(@"-episode-(?<n>\d{1,4})/?(?:$|[?#])", RegexOptions.IgnoreCase)]
        private static partial Regex UrlEpisode();

        [GeneratedRegex(@"<\s*(?:br\s*/?|/p|p\b[^>]*)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex Paragraph();

        [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
        private static partial Regex Comment();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Tag();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
