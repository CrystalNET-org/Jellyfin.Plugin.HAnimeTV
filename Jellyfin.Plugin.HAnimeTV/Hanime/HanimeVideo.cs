using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HAnimeTV.Hanime
{
    /// <summary>
    /// A video of the catalog (an entry of hanime.tv's search dataset).
    /// </summary>
    public sealed partial class HanimeVideo
    {
        public required string Slug { get; init; }

        public required string Name { get; init; }

        public string? Description { get; init; }

        public string? Brand { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets the portrait cover. hanime.tv's names are swapped: cover_url is the poster.
        /// </summary>
        public string? PosterUrl { get; init; }

        /// <summary>
        /// Gets the landscape thumbnail (hanime.tv's poster_url).
        /// </summary>
        public string? ThumbnailUrl { get; init; }

        public long Views { get; init; }

        public long Likes { get; init; }

        public long Dislikes { get; init; }

        public bool IsCensored { get; init; }

        public DateTime? CreatedAt { get; init; }

        public DateTime? ReleasedAt { get; init; }

        public long? DurationMs { get; init; }

        /// <summary>
        /// Gets the series and episode number derived from the name ("Title 2" is episode 2 of
        /// "Title"); a name without a number is episode 1 of a series of its own.
        /// </summary>
        public (string Series, int Episode) SeriesInfo()
        {
            var match = EpisodePattern().Match(Name);
            return match.Success && int.TryParse(match.Groups["episode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episode)
                ? (match.Groups["series"].Value.Trim(), episode)
                : (Name.Trim(), 1);
        }

        /// <summary>
        /// Gets the description as plain text.
        /// </summary>
        public string? PlainDescription()
        {
            if (string.IsNullOrWhiteSpace(Description))
            {
                return null;
            }

            var text = ParagraphEnd().Replace(Description, "\n");
            text = HtmlTag().Replace(text, string.Empty);
            return WebUtility.HtmlDecode(text).Trim();
        }

        /// <summary>
        /// Reads an entry of the dataset; null if it has no slug or name.
        /// </summary>
        public static HanimeVideo? FromJson(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var slug = String(item, "slug");
            var name = String(item, "name");
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var tags = item.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                ? tagsElement.EnumerateArray()
                    .Select(t => t.ValueKind switch
                    {
                        JsonValueKind.String => t.GetString(),
                        // Older responses: [{ "text": "..." }]
                        JsonValueKind.Object => String(t, "text"),
                        _ => null,
                    })
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();

            return new HanimeVideo
            {
                Slug = slug.Trim(),
                Name = WebUtility.HtmlDecode(name).Trim(),
                Description = String(item, "description"),
                Brand = String(item, "brand"),
                Tags = tags,
                PosterUrl = String(item, "cover_url") ?? String(item, "poster_url"),
                ThumbnailUrl = String(item, "poster_url") ?? String(item, "cover_url"),
                Views = Number(item, "views") ?? 0,
                Likes = Number(item, "likes") ?? 0,
                Dislikes = Number(item, "dislikes") ?? 0,
                // The catalog marks censorship with the tag; older responses had is_censored
                IsCensored = tags.Contains("censored", StringComparer.OrdinalIgnoreCase)
                    || (item.TryGetProperty("is_censored", out var censored) && censored.ValueKind == JsonValueKind.True),
                CreatedAt = Time(item, "created_at_unix") ?? Time(item, "created_at"),
                ReleasedAt = Time(item, "released_at_unix") ?? Time(item, "released_at"),
                DurationMs = Number(item, "duration_in_ms") is > 0 and var duration ? duration : null,
            };
        }

        private static string? String(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        private static long? Number(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.Number => (long)value.GetDouble(),
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                _ => null,
            };
        }

        /// <summary>
        /// A time as unix seconds, unix milliseconds or ISO 8601.
        /// </summary>
        private static DateTime? Time(JsonElement item, string name)
        {
            if (Number(item, name) is { } unix && unix > 0)
            {
                // Seconds until the year 5138; anything larger is milliseconds
                return unix < 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime;
            }

            return String(item, name) is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.UtcDateTime
                : null;
        }

        [GeneratedRegex(@"^(?<series>.+?)(?:\s+|\s*[-–—:]\s*)(?:(?:episode|ep\.?)\s*)?(?<episode>\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex EpisodePattern();

        [GeneratedRegex(@"<\s*(?:br\s*/?|/p)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex ParagraphEnd();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex HtmlTag();
    }
}
