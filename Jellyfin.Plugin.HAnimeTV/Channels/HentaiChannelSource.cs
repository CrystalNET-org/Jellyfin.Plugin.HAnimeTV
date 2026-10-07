using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.OppaiStream;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// The hentai catalog (hanime.tv, oppai.stream and Hentai Haven) as a channel: recently uploaded, new
    /// releases, most viewed, most liked, series A–Z, genres and studios.
    /// </summary>
    public sealed class HentaiChannelSource : IChannelSource
    {
        public const string ProviderName = "Hentai";

        private readonly HentaiCatalog _catalog;
        private readonly OppaiStreamClient _oppaiStream;
        private readonly StreamLinks _links;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<HentaiChannelSource> _logger;

        public HentaiChannelSource(HentaiCatalog catalog, OppaiStreamClient oppaiStream, StreamLinks links, IMediaEncoder mediaEncoder, ILogger<HentaiChannelSource> logger)
        {
            _catalog = catalog;
            _oppaiStream = oppaiStream;
            _links = links;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }

        public string ChannelName => ProviderName;

        public string Description => "Videos from hanime.tv, oppai.stream and Hentai Haven.";

        public string HomePageUrl => "https://hanime.tv";

        public string ImageName => "hentai.png";

        public ProviderSettings Settings(PluginConfiguration config) => config.Hentai;

        public async Task<ChannelItemResult> GetItemsAsync(string? folderId, CancellationToken cancellationToken)
        {
            var config = Config;
            var catalog = await _catalog.GetAsync(config, cancellationToken).ConfigureAwait(false);
            return HentaiCatalogBrowser.GetItems(catalog, config, folderId);
        }

        public async Task<IEnumerable<ChannelItemInfo>> GetLatestAsync(CancellationToken cancellationToken)
        {
            var config = Config;
            var catalog = await _catalog.GetAsync(config, cancellationToken).ConfigureAwait(false);
            return HentaiCatalogBrowser.GetLatest(catalog, config, 24);
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(string id, CancellationToken cancellationToken)
        {
            var key = HentaiCatalogBrowser.KeyOf(id) is { } k ? HentaiVideo.ParseKey(k) : null;
            if (key is not { } video)
            {
                throw new ArgumentException("Not a video: " + id, nameof(id));
            }

            if (video.Source == HentaiSource.OppaiStream)
            {
                return [await OppaiStreamSourceAsync(video.Id, cancellationToken).ConfigureAwait(false)];
            }

            var link = video.Source == HentaiSource.Hanime ? _links.Hanime(video.Id) : _links.HentaiHaven(video.Id);
            var source = await StreamSource.CreateAsync(HentaiVideo.KeyOf(video.Source, video.Id), link, null, _mediaEncoder, _logger, cancellationToken).ConfigureAwait(false);
            return [source];
        }

        public string CacheKey(PluginConfiguration config) => string.Join(
            '\n',
            config.Hentai.HideCensored,
            config.Hentai.MaxItemsPerFolder,
            string.Join(',', config.Hentai.NormalizedHiddenTags().Order(StringComparer.OrdinalIgnoreCase)),
            config.Hentai.HanimeEnabled,
            config.Hentai.OppaiStreamEnabled,
            config.Hentai.HentaiHavenEnabled,
            config.Hentai.SearchUrl,
            config.Hentai.OppaiStreamUrl,
            config.Hentai.HentaiHavenUrl);

        /// <summary>
        /// An oppai.stream episode: an MP4 file or HLS, with the site's subtitles as external
        /// streams served through the plugin.
        /// </summary>
        private async Task<MediaSourceInfo> OppaiStreamSourceAsync(string path, CancellationToken cancellationToken)
        {
            var media = await _oppaiStream.GetMediaAsync(path, cancellationToken).ConfigureAwait(false);
            var hls = media.Streams[0].IsHls;
            var source = await StreamSource.CreateAsync(
                HentaiVideo.KeyOf(HentaiSource.OppaiStream, path),
                _links.OppaiStream(path, hls),
                null,
                _mediaEncoder,
                _logger,
                cancellationToken,
                hls ? "hls" : "mp4").ConfigureAwait(false);
            StreamSource.AddSubtitles(source, media.Subtitles.Select(s => (s, _links.OppaiStreamFile(path, new Uri(s.Url)))));
            return source;
        }

        private static HentaiSettings Config => Plugin.Instance?.Configuration.Hentai ?? new HentaiSettings();
    }
}
