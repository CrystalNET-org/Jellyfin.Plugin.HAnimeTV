using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// The hanime.tv channel. Only the users selected in the settings see it.
    /// </summary>
    public class HanimeChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IHasCacheKey
    {
        /// <summary>
        /// The channel's name. Jellyfin derives the channel's id from it, so it never changes.
        /// </summary>
        public const string ChannelName = "hanime.tv";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

        private readonly HanimeClient _client;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<HanimeChannel> _logger;

        public HanimeChannel(HanimeClient client, IMediaEncoder mediaEncoder, ILogger<HanimeChannel> logger)
        {
            _client = client;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }

        public string Name => ChannelName;

        public string Description => "Videos from hanime.tv.";

        // Changing it discards Jellyfin's cached folder listings
        public string DataVersion => "1";

        public string HomePageUrl => "https://hanime.tv";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.Adult;

        public InternalChannelFeatures GetChannelFeatures() => new()
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Episode],
        };

        /// <inheritdoc />
        public bool IsEnabledFor(string userId) =>
            Guid.TryParse(userId, out var id) && Plugin.Instance?.Configuration.IsAllowed(id) == true;

        /// <inheritdoc />
        public string? GetCacheKey(string? userId)
        {
            // Jellyfin caches folder listings per user for hours: start over when the settings
            // that change them do
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return null;
            }

            var key = string.Join(
                '\n',
                IsEnabledFor(userId ?? string.Empty),
                config.HideCensored,
                config.MaxItemsPerFolder,
                string.Join(',', config.NormalizedHiddenTags().Order(StringComparer.OrdinalIgnoreCase)),
                config.SearchUrl);
            return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        }

        /// <inheritdoc />
        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("The plugin is not loaded");
            // Requests without a user are Jellyfin's own (e.g. refreshing the channels). Others
            // are refused rather than answered with an empty folder: Jellyfin would delete the
            // folder's items for everyone and cache the empty listing
            if (!query.UserId.Equals(Guid.Empty) && !config.IsAllowed(query.UserId))
            {
                throw new UnauthorizedAccessException("The hanime.tv channel is not enabled for this user");
            }

            // Failures are thrown rather than returned as an empty folder, which Jellyfin would
            // cache for hours and use to delete the folder's items
            var catalog = await _client.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            return CatalogBrowser.GetItems(catalog, config, query.FolderId);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !IsEnabledFor(request.UserId))
            {
                return Array.Empty<ChannelItemInfo>();
            }

            try
            {
                var catalog = await _client.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                return CatalogBrowser.GetLatest(catalog, config, 24);
            }
            catch (HanimeException ex)
            {
                _logger.LogWarning("hanime.tv: no latest videos: {Error}", ex.Message);
                return Array.Empty<ChannelItemInfo>();
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var slug = CatalogBrowser.SlugOf(id) ?? throw new ArgumentException("Not a video: " + id, nameof(id));
            // Asked for on playback: hanime.tv's stream URLs expire, so they are not stored
            var streams = await _client.GetStreamsAsync(slug, cancellationToken).ConfigureAwait(false);
            var sources = await Task.WhenAll(streams.Select(s => ToMediaSourceAsync(slug, s, cancellationToken))).ConfigureAwait(false);
            _logger.LogInformation("hanime.tv: {Count} streams for {Slug}: {Streams}", sources.Length, slug, string.Join(", ", streams.Select(s => s.Label)));
            return sources;
        }

        private async Task<MediaSourceInfo> ToMediaSourceAsync(string slug, HanimeStream stream, CancellationToken cancellationToken)
        {
            var source = CreateMediaSource(slug, stream);
            // The streams' codecs and duration: without them Jellyfin can neither remux nor seek
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                var info = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = CreateMediaSource(slug, stream),
                        MediaType = DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    timeout.Token).ConfigureAwait(false);
                ApplyProbe(source, info);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("hanime.tv: could not probe the {Label} stream of {Slug}, assuming H.264 and AAC: {Error}", stream.Label, slug, ex.Message);
            }

            return source;
        }

        internal static MediaSourceInfo CreateMediaSource(string slug, HanimeStream stream)
        {
            var height = stream.Height > 0 ? stream.Height : 720;
            return new MediaSourceInfo
            {
                Id = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(slug + "|" + stream.Height + "|" + stream.Premium))),
                Name = stream.Label,
                Path = stream.Url,
                Protocol = MediaProtocol.Http,
                Container = "hls",
                IsRemote = true,
                Type = MediaSourceType.Default,
                // Browsers cannot play hanime.tv's streams themselves (CORS, Referer): Jellyfin
                // remuxes or transcodes them
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = HanimeClient.UserAgent,
                    ["Referer"] = HanimeClient.Referer,
                },
                Bitrate = EstimatedBitrate(height),
                MediaStreams =
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = "h264",
                        Height = height,
                        Width = (int)Math.Round(height * 16 / 9.0 / 2) * 2,
                        IsDefault = true,
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 1,
                        Codec = "aac",
                        Channels = 2,
                        ChannelLayout = "stereo",
                        IsDefault = true,
                    },
                ],
                DefaultAudioStreamIndex = 1,
            };
        }

        internal static void ApplyProbe(MediaSourceInfo source, MediaSourceInfo probed)
        {
            if (probed.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) == true)
            {
                source.MediaStreams = probed.MediaStreams;
                source.DefaultAudioStreamIndex = probed.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Index;
            }

            if (probed.RunTimeTicks is > 0)
            {
                source.RunTimeTicks = probed.RunTimeTicks;
            }

            if (probed.Bitrate is > 0)
            {
                source.Bitrate = probed.Bitrate;
            }
        }

        /// <summary>
        /// Bits per second of hanime.tv's streams, used until they are probed.
        /// </summary>
        private static int EstimatedBitrate(int height) => height switch
        {
            >= 1080 => 3_000_000,
            >= 720 => 1_500_000,
            >= 480 => 800_000,
            _ => 600_000,
        };

        /// <inheritdoc />
        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.HAnimeTV.thumb.png");
            return Task.FromResult(stream is null
                ? new DynamicImageResponse { HasImage = false }
                : new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Stream = stream });
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary, ImageType.Thumb];

        /// <summary>
        /// Gets the channel's id in Jellyfin, as Jellyfin computes it from the name.
        /// </summary>
        internal static Guid InternalId(MediaBrowser.Controller.Library.ILibraryManager libraryManager, string channelName) =>
            libraryManager.GetNewItemId("Channel " + channelName, typeof(MediaBrowser.Controller.Channels.Channel));
    }
}
