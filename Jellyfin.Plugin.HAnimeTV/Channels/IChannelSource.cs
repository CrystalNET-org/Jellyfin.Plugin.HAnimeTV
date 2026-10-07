using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.HAnimeTV.Channels
{
    /// <summary>
    /// A provider that can be shown as a channel (see <see cref="ProviderChannel"/>).
    /// </summary>
    public interface IChannelSource
    {
        /// <summary>
        /// Gets the channel's name. Jellyfin derives the channel's id from it, so it never changes.
        /// </summary>
        string ChannelName { get; }

        string Description { get; }

        string HomePageUrl { get; }

        /// <summary>
        /// Gets the channel's image, a PNG file in Images.
        /// </summary>
        string ImageName { get; }

        /// <summary>
        /// Gets the provider's settings: its mode and users.
        /// </summary>
        ProviderSettings Settings(PluginConfiguration config);

        /// <summary>
        /// Gets a folder's items; the root's for a null or empty id.
        /// </summary>
        /// <exception cref="Exception">The provider could not be reached; Jellyfin then shows
        /// nothing, without caching it or deleting the folder's items.</exception>
        Task<ChannelItemResult> GetItemsAsync(string? folderId, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the newest videos, for Jellyfin's "Latest" row.
        /// </summary>
        Task<IEnumerable<ChannelItemInfo>> GetLatestAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Gets a video's playable sources, asked for on playback.
        /// </summary>
        Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(string id, CancellationToken cancellationToken);

        /// <summary>
        /// Gets what Jellyfin's cached folder listings depend on, besides the user.
        /// </summary>
        string CacheKey(PluginConfiguration config);
    }
}
