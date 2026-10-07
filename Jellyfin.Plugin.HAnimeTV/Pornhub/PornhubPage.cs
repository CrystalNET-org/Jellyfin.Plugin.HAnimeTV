using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HAnimeTV.Pornhub
{
    /// <summary>
    /// A stream on a video page.
    /// </summary>
    /// <param name="Url">The stream's URL: an HLS playlist, an MP4 file, or Pornhub's get_media
    /// list of MP4 files.</param>
    /// <param name="Height">Vertical resolution, 0 if unknown.</param>
    public sealed record PornhubStream(string Url, int Height)
    {
        public bool IsHls => Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

        public bool IsMediaList => Url.Contains("/video/get_media", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the streams of a video page the way yt-dlp does: the page's flashvars, or else
    /// the script variables Pornhub assembles the stream URLs from.
    /// </summary>
    public static partial class PornhubPage
    {
        /// <summary>
        /// Gets why the page has no video (removed, geo-blocked, private), or null.
        /// </summary>
        public static string? Unavailable(string html)
        {
            if (GeoBlocked().IsMatch(html))
            {
                return "Pornhub blocks this video in the server's country";
            }

            if (Removed().Match(html) is { Success: true } removed)
            {
                return "Pornhub says: " + Regex.Replace(Regex.Replace(removed.Groups["error"].Value, "<[^>]*>", " "), @"\s+", " ").Trim();
            }

            if (Locked().IsMatch(html))
            {
                return "The video is private";
            }

            return null;
        }

        /// <summary>
        /// Gets the page's streams, best first.
        /// </summary>
        public static IReadOnlyList<PornhubStream> Streams(string html)
        {
            var streams = new List<PornhubStream>();
            if (FlashVars().Match(html) is { Success: true } match)
            {
                try
                {
                    using var flashvars = JsonDocument.Parse(match.Groups["json"].Value);
                    if (flashvars.RootElement.TryGetProperty("mediaDefinitions", out var definitions) && definitions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var definition in definitions.EnumerateArray())
                        {
                            if (definition.ValueKind == JsonValueKind.Object
                                && definition.TryGetProperty("videoUrl", out var url) && url.ValueKind == JsonValueKind.String
                                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                            {
                                streams.Add(new PornhubStream(uri.AbsoluteUri, Quality(definition)));
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Falls back to the script variables
                }
            }

            if (streams.Count == 0)
            {
                foreach (var (name, value) in ScriptVariables(html))
                {
                    if (name.StartsWith("qualityItems", StringComparison.Ordinal))
                    {
                        streams.AddRange(QualityItems(value));
                    }
                    else if ((name.StartsWith("media", StringComparison.Ordinal) || name.StartsWith("quality", StringComparison.Ordinal))
                        && Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    {
                        streams.Add(new PornhubStream(uri.AbsoluteUri, HeightOf(uri.AbsoluteUri)));
                    }
                }
            }

            return streams
                .Where(s => s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(s => s.Url)
                .OrderByDescending(s => s.Height)
                .ToList();
        }

        /// <summary>
        /// Reads get_media's answer: [{ "videoUrl": "…", "quality": "720" }].
        /// </summary>
        public static IReadOnlyList<PornhubStream> MediaList(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind != JsonValueKind.Array
                    ? Array.Empty<PornhubStream>()
                    : document.RootElement.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("videoUrl", out var u) && u.ValueKind == JsonValueKind.String)
                        .Select(m => new PornhubStream(m.GetProperty("videoUrl").GetString()!, Quality(m)))
                        .Where(s => Uri.IsWellFormedUriString(s.Url, UriKind.Absolute))
                        .OrderByDescending(s => s.Height)
                        .ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<PornhubStream>();
            }
        }

        /// <summary>
        /// Evaluates "var media_0 = a + b; var a = 'https://' /* x */ + 'cdn…';" assignments:
        /// Pornhub builds stream URLs from string pieces to keep them out of the page's text.
        /// </summary>
        internal static IReadOnlyList<(string Name, string Value)> ScriptVariables(string html)
        {
            var result = new List<(string, string)>();
            if (Assignments().Match(html) is not { Success: true } match)
            {
                return result;
            }

            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in match.Groups["code"].Value.Split(';'))
            {
                var assignment = VarKeyword().Replace(raw.Trim(), string.Empty);
                var equals = assignment.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    continue;
                }

                var name = assignment[..equals].Trim();
                var value = string.Concat(Comment().Replace(assignment[(equals + 1)..], string.Empty)
                    .Split('+')
                    .Select(part => part.Trim())
                    .Select(part => variables.TryGetValue(part, out var known) ? known : Unquote(part)));
                variables[name] = value;
                result.Add((name, value));
            }

            return result;
        }

        private static IEnumerable<PornhubStream> QualityItems(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind != JsonValueKind.Array
                    ? Array.Empty<PornhubStream>()
                    : document.RootElement.EnumerateArray()
                        .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        .Select(i => i.GetProperty("url").GetString()!)
                        .Where(u => Uri.IsWellFormedUriString(u, UriKind.Absolute))
                        .Select(u => new PornhubStream(u, HeightOf(u)))
                        .ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<PornhubStream>();
            }
        }

        private static int Quality(JsonElement definition)
        {
            if (!definition.TryGetProperty("quality", out var quality))
            {
                return 0;
            }

            // A number, a string, or (for HLS masters) a list of them
            var value = quality.ValueKind switch
            {
                JsonValueKind.Number => quality.GetRawText(),
                JsonValueKind.String => quality.GetString(),
                JsonValueKind.Array => quality.EnumerateArray().Select(q => q.ValueKind == JsonValueKind.String ? q.GetString() : q.GetRawText()).LastOrDefault(),
                _ => null,
            };
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ? height : 0;
        }

        /// <summary>
        /// Reads "…/720P_4000K_….mp4" style heights.
        /// </summary>
        private static int HeightOf(string url) =>
            HeightInUrl().Match(url) is { Success: true } m && int.TryParse(m.Groups["height"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;

        private static string Unquote(string value) =>
            value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

        [GeneratedRegex(@"var\s+flashvars_\d+\s*=\s*(?<json>\{.+?\});", RegexOptions.Singleline)]
        private static partial Regex FlashVars();

        [GeneratedRegex(@"(?<code>var\s+(?:media|quality|qualityItems)_.+)")]
        private static partial Regex Assignments();

        [GeneratedRegex(@"^var\s+")]
        private static partial Regex VarKeyword();

        [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
        private static partial Regex Comment();

        [GeneratedRegex(@"(?<height>\d+)[pP]?_\d+[kK]")]
        private static partial Regex HeightInUrl();

        [GeneratedRegex(@"class=[""']geoBlocked[""']|>\s*This content is unavailable in your country")]
        private static partial Regex GeoBlocked();

        [GeneratedRegex(@"<div[^>]+class=([""'])(?:(?!\1).)*\b(?:removed|userMessageSection)\b(?:(?!\1).)*\1[^>]*>(?<error>.+?)</div>", RegexOptions.Singleline)]
        private static partial Regex Removed();

        [GeneratedRegex(@"<[^>]+\bid=[""']lockedPlayer")]
        private static partial Regex Locked();
    }
}
