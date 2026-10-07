using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.HAnimeTV.Pornhub
{
    /// <summary>
    /// A video as Pornhub's webmasters API returns it.
    /// </summary>
    public sealed class PornhubVideo
    {
        public required string Id { get; init; }

        public required string Title { get; init; }

        public TimeSpan? Duration { get; init; }

        public long Views { get; init; }

        /// <summary>
        /// Gets the share of positive ratings, 0 to 100.
        /// </summary>
        public double? Rating { get; init; }

        public DateTime? PublishedAt { get; init; }

        /// <summary>
        /// Gets the landscape thumbnail.
        /// </summary>
        public string? Thumbnail { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Pornstars { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Reads a video of the API; null without an id or title.
        /// </summary>
        public static PornhubVideo? FromJson(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = Text(item, "video_id");
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new PornhubVideo
            {
                Id = id.Trim(),
                Title = WebUtility.HtmlDecode(title).Trim(),
                Duration = ParseDuration(Text(item, "duration")),
                Views = long.TryParse(Text(item, "views"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var views) ? views : 0,
                Rating = double.TryParse(Text(item, "rating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) ? rating : null,
                PublishedAt = DateTime.TryParse(Text(item, "publish_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published) ? published : null,
                Thumbnail = Text(item, "default_thumb") ?? Text(item, "thumb"),
                Tags = Names(item, "tags", "tag_name"),
                Categories = Names(item, "categories", "category"),
                Pornstars = Names(item, "pornstars", "pornstar_name"),
            };
        }

        /// <summary>
        /// Reads "12:34" or "1:02:03".
        /// </summary>
        internal static TimeSpan? ParseDuration(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var seconds = 0;
            foreach (var part in text.Trim().Split(':'))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    return null;
                }

                seconds = (seconds * 60) + value;
            }

            return TimeSpan.FromSeconds(seconds);
        }

        private static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            } : null;

        /// <summary>
        /// Reads [{ "tag_name": "…" }] (or plain strings) into names.
        /// </summary>
        private static string[] Names(JsonElement item, string list, string field) =>
            item.TryGetProperty(list, out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : Text(v, field))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => WebUtility.HtmlDecode(n!).Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
    }
}
