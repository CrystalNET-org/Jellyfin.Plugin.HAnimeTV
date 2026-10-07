using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.HAnimeTV.Streaming
{
    /// <summary>
    /// The stream links of this server: what the library's .strm files and the channels'
    /// media sources point at. Players (browsers, Jellyfin's ffmpeg and its workers) fetch them
    /// without a Jellyfin login, so they carry the plugin's stream token.
    /// </summary>
    public sealed class StreamLinks
    {
        public const string HanimePath = "/HanimeTV/Stream/";

        public const string PornhubPath = "/HanimeTV/Pornhub/";

        public const string HentaiHavenPath = "/HanimeTV/HentaiHaven/";

        public const string OppaiStreamPath = "/HanimeTV/OppaiStream/";

        private readonly IServerApplicationHost _applicationHost;
        private readonly object _tokenLock = new();

        public StreamLinks(IServerApplicationHost applicationHost)
        {
            _applicationHost = applicationHost;
        }

        /// <summary>
        /// Gets the address in the links: the configured one, or Jellyfin's guess.
        /// </summary>
        public string BaseUrl(PluginConfiguration config) =>
            (IsGuessed(config) ? _applicationHost.GetApiUrlForLocalAccess(null, false) : config.StreamBaseUrl.Trim()).TrimEnd('/');

        /// <summary>
        /// Gets whether the address is Jellyfin's guess, e.g. a pod IP in Kubernetes, which
        /// ffmpeg workers elsewhere cannot reach.
        /// </summary>
        public static bool IsGuessed(PluginConfiguration config) => string.IsNullOrWhiteSpace(config.StreamBaseUrl);

        public string Hanime(string slug) => Link(HanimePath, slug);

        /// <param name="viewkey">The video's viewkey.</param>
        /// <param name="hls">Whether the video's best stream is HLS; else the link is an MP4 file.</param>
        public string Pornhub(string viewkey, bool hls = true) => Link(PornhubPath, viewkey, hls);

        /// <param name="path">The episode page's path at Hentai Haven.</param>
        /// <param name="hls">Whether the episode's stream is HLS; else the link is an MP4 file.</param>
        public string HentaiHaven(string path, bool hls = true) => Link(HentaiHavenPath, EncodeId(path), hls);

        /// <param name="path">The episode page's address at oppai.stream.</param>
        /// <param name="hls">Whether the episode's stream is HLS; else the link is an MP4 file.</param>
        public string OppaiStream(string path, bool hls) => Link(OppaiStreamPath, EncodeId(path), hls);

        /// <summary>
        /// Gets a link through the plugin to a file of a video's source, e.g. a subtitle.
        /// </summary>
        public string OppaiStreamFile(string path, Uri file)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded");
            return BaseUrl(plugin.Configuration) + OppaiStreamPath + Uri.EscapeDataString(EncodeId(path)) + "/" + HlsProxy.Link(file, Token(plugin), 0);
        }

        /// <summary>
        /// Gets the link of a video of the hentai catalog.
        /// </summary>
        public string For(HentaiVideo video) => video.Source switch
        {
            HentaiSource.Hanime => Hanime(video.Id),
            HentaiSource.OppaiStream => OppaiStream(video.Id, !video.StreamIsFile),
            _ => HentaiHaven(video.Id, !video.StreamIsFile),
        };

        /// <summary>
        /// Encodes an id for a path segment: ids with slashes (Hentai Haven's paths) in
        /// base64url, which proxies and Jellyfin's router leave alone.
        /// </summary>
        public static string EncodeId(string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// Reads <see cref="EncodeId"/>; null if it is not.
        /// </summary>
        public static string? DecodeId(string encoded)
        {
            try
            {
                var base64 = encoded.Replace('-', '+').Replace('_', '/');
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=')));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private string Link(string path, string id, bool hls = true)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded");
            var config = plugin.Configuration;
            return BaseUrl(config) + path + Uri.EscapeDataString(id) + (hls ? "/index.m3u8" : "/video.mp4") + "?token=" + Uri.EscapeDataString(Token(plugin));
        }

        /// <summary>
        /// Gets the stream token, generating it on first use.
        /// </summary>
        public string Token(Plugin plugin)
        {
            lock (_tokenLock)
            {
                var config = plugin.Configuration;
                if (string.IsNullOrEmpty(config.StreamToken))
                {
                    config.StreamToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
                    // Not UpdateConfiguration, which would start a sync
                    plugin.SaveConfiguration(config);
                }

                return config.StreamToken;
            }
        }
    }
}
