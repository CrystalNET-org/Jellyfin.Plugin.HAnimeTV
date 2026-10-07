using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// A provider as a channel. Only the users selected for the provider see it, and only while
    /// the provider is in channel mode.
    /// </summary>
    public class ProviderChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IHasCacheKey
    {
        private readonly ILogger _logger;

        public ProviderChannel(IChannelSource source, ILogger logger)
        {
            Source = source;
            _logger = logger;
        }

        public IChannelSource Source { get; }

        public string Name => Source.ChannelName;

        public string Description => Source.Description;

        // Changing it discards Jellyfin's cached folder listings
        public string DataVersion => "2";

        public string HomePageUrl => Source.HomePageUrl;

        public ChannelParentalRating ParentalRating => ChannelParentalRating.Adult;

        public InternalChannelFeatures GetChannelFeatures() => new()
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Episode, ChannelMediaContentType.Clip],
        };

        /// <inheritdoc />
        public bool IsEnabledFor(string userId) =>
            Guid.TryParse(userId, out var id) && Plugin.Instance?.Configuration is { } config && Source.Settings(config).Grants(id, ProviderMode.Channel);

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

            var key = IsEnabledFor(userId ?? string.Empty) + "\n" + Source.CacheKey(config);
            return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        }

        /// <inheritdoc />
        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("The plugin is not loaded");
            // Requests without a user are Jellyfin's own (e.g. refreshing the channels). Others
            // are refused rather than answered with an empty folder: Jellyfin would delete the
            // folder's items for everyone and cache the empty listing
            if (!query.UserId.Equals(Guid.Empty) && !Source.Settings(config).Grants(query.UserId, ProviderMode.Channel))
            {
                throw new UnauthorizedAccessException($"The {Name} channel is not enabled for this user");
            }

            return await Source.GetItemsAsync(query.FolderId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
        {
            if (!IsEnabledFor(request.UserId))
            {
                return Array.Empty<ChannelItemInfo>();
            }

            try
            {
                return await Source.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("{Channel}: no latest videos: {Error}", Name, ex.Message);
                return Array.Empty<ChannelItemInfo>();
            }
        }

        /// <inheritdoc />
        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken) =>
            Source.GetMediaSourcesAsync(id, cancellationToken);

        /// <inheritdoc />
        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.HAnimeTV.Images." + Source.ImageName);
            return Task.FromResult(stream is null
                ? new DynamicImageResponse { HasImage = false }
                : new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Stream = stream });
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary, ImageType.Thumb];
    }
}
