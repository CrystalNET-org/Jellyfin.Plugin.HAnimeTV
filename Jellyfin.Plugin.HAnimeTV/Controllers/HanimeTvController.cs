using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Controllers
{
    /// <summary>
    /// Status and actions for the plugin's configuration page.
    /// </summary>
    [ApiController]
    [Route("HanimeTV")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class HanimeTvController : ControllerBase
    {
        private readonly HanimeClient _client;
        private readonly ChannelAccessSync _sync;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;

        public HanimeTvController(
            HanimeClient client,
            ChannelAccessSync sync,
            IUserManager userManager,
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            _client = client;
            _sync = sync;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Gets the channel's state and which users can access it.
        /// </summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var channelId = _sync.ChannelId;
            return new
            {
                ChannelId = channelId,
                config.EnforceAccess,
                _client.CatalogTime,
                _client.CatalogCount,
                _client.CatalogError,
                _client.AccountStatus,
                _sync.LastSync,
                _sync.LastChanged,
                Users = _userManager.GetUsers()
                    .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(u => new
                    {
                        u.Id,
                        Name = u.Username,
                        IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                        Selected = config.IsAllowed(u.Id),
                        // What Jellyfin's policy grants, which decides access by id and playback
                        PolicyGrantsAccess = PolicyGrantsAccess(u, channelId),
                    }),
            };
        }

        /// <summary>
        /// Tests the given settings: downloads the catalog and the streams of its newest video.
        /// </summary>
        [HttpPost("Test")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> Test([FromBody] PluginConfiguration settings, CancellationToken cancellationToken)
        {
            var client = new HanimeClient(_httpClientFactory, () => settings, _loggerFactory.CreateLogger<HanimeClient>());
            IReadOnlyList<HanimeVideo> catalog;
            try
            {
                catalog = await client.DownloadCatalogAsync(settings.SearchUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HanimeException ex)
            {
                return new { Ok = false, CatalogError = ex.Message };
            }

            var newest = catalog.OrderByDescending(v => v.CreatedAt ?? DateTime.MinValue).FirstOrDefault();
            if (newest is null)
            {
                return new { Ok = false, CatalogCount = 0, CatalogError = "The catalog is empty" };
            }

            try
            {
                var streams = await client.GetStreamsAsync(newest.Slug, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Ok = true,
                    CatalogCount = catalog.Count,
                    TestedVideo = newest.Name,
                    Streams = streams.Select(s => s.Label),
                    client.AccountStatus,
                };
            }
            catch (HanimeException ex)
            {
                return new { Ok = false, CatalogCount = catalog.Count, TestedVideo = newest.Name, StreamError = ex.Message, client.AccountStatus };
            }
        }

        /// <summary>
        /// Brings the users' channel access in line with the selection now.
        /// </summary>
        [HttpPost("SyncAccess")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> SyncAccess(CancellationToken cancellationToken)
        {
            var changed = await _sync.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return new { Changed = changed };
        }

        private bool PolicyGrantsAccess(User user, Guid channelId)
        {
            // Jellyfin's own check, once the channel exists
            if (_libraryManager.GetItemById(channelId) is MediaBrowser.Controller.Channels.Channel channel)
            {
                return channel.IsVisible(user);
            }

            var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
            return blocked.Length != 0
                ? !blocked.Contains(channelId)
                : user.HasPermission(PermissionKind.EnableAllChannels) || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Contains(channelId);
        }
    }
}
