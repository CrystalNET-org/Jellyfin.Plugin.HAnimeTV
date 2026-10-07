using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.HAnimeTV.Streaming
{
    /// <summary>
    /// Serves hanime.tv's HLS streams through Jellyfin: their playlists are rewritten so that
    /// every playlist, segment and key is fetched through the plugin, which adds the headers
    /// hanime.tv expects. Players (browsers or Jellyfin's ffmpeg) then need nothing but the
    /// stream link.
    /// </summary>
    /// <remarks>
    /// Rewritten links are relative ("proxy/…"), so they resolve against whatever address the
    /// player used. Each carries the upstream URL and its signature, so the proxy only fetches
    /// URLs taken from hanime.tv's playlists and is no open proxy.
    /// </remarks>
    public static partial class HlsProxy
    {
        /// <summary>
        /// Gets whether the response is a playlist (and needs rewriting) rather than media.
        /// </summary>
        public static bool IsPlaylist(string? contentType, Uri url) =>
            (contentType is not null && (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || contentType.Contains("m3u", StringComparison.OrdinalIgnoreCase)))
            || url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rewrites the URIs of a playlist (lines and URI="…" attributes) with <paramref name="link"/>.
        /// </summary>
        public static string Rewrite(string playlist, Uri baseUrl, Func<Uri, string> link)
        {
            var output = new StringBuilder(playlist.Length * 2);
            foreach (var rawLine in playlist.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    output.Append('\n');
                    continue;
                }

                if (line.StartsWith('#'))
                {
                    line = UriAttribute().Replace(line, match =>
                        Uri.TryCreate(baseUrl, match.Groups["uri"].Value, out var uri) && IsHttp(uri)
                            ? "URI=\"" + link(uri) + "\""
                            : match.Value);
                }
                else if (Uri.TryCreate(baseUrl, line.Trim(), out var uri) && IsHttp(uri))
                {
                    line = link(uri);
                }

                output.Append(line).Append('\n');
            }

            return output.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>
        /// The link for an upstream URL, relative to a playlist <paramref name="depth"/> folders
        /// below the stream's folder (0 for index.m3u8, <see cref="ProxyDepth"/> for proxied
        /// playlists). It ends with the upstream file's name: ffmpeg only reads HLS segments
        /// whose URLs end with a media extension.
        /// </summary>
        public static string Link(Uri upstream, string token, int depth) =>
            string.Concat(Enumerable.Repeat("../", depth))
            + "proxy/" + Uri.EscapeDataString(token)
            + "/" + Sign(upstream.AbsoluteUri, token)
            + "/" + Base64Url(Encoding.UTF8.GetBytes(upstream.AbsoluteUri))
            + "/" + FileName(upstream);

        /// <summary>
        /// The folders below the stream's folder that a proxied file is served from:
        /// proxy/{token}/{signature}/{url}/{name}.
        /// </summary>
        public const int ProxyDepth = 4;

        private static string FileName(Uri upstream)
        {
            var name = upstream.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            name = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            return name.Length == 0 || name.Length > 100 ? "file" : name;
        }

        /// <summary>
        /// Reads a link's upstream URL; null if its signature does not match.
        /// </summary>
        public static Uri? Resolve(string encodedUrl, string signature, string token)
        {
            string url;
            try
            {
                url = Encoding.UTF8.GetString(FromBase64Url(encodedUrl));
            }
            catch (FormatException)
            {
                return null;
            }

            var expected = Encoding.ASCII.GetBytes(Sign(url, token));
            return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature ?? string.Empty))
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHttp(uri)
                ? uri
                : null;
        }

        /// <summary>
        /// Compares a request's token with the configured one in constant time.
        /// </summary>
        public static bool IsValidToken(string? given, string expected) =>
            !string.IsNullOrEmpty(expected) && given is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

        private static string Sign(string url, string token) =>
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(url)))[..32];

        private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string value)
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
        }

        [GeneratedRegex("URI=\"(?<uri>[^\"]*)\"")]
        private static partial Regex UriAttribute();
    }
}
