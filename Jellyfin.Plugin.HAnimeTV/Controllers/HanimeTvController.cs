using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.Library;
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
        private readonly LibrarySync _sync;
        private readonly LibraryAccessSync _access;
        private readonly LibraryLocator _locator;
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;

        public HanimeTvController(
            HanimeClient client,
            LibrarySync sync,
            LibraryAccessSync access,
            LibraryLocator locator,
            IUserManager userManager,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            _client = client;
            _sync = sync;
            _access = access;
            _locator = locator;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Gets the library's state and which users can access it.
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

            var libraryId = _locator.Find();
            return new
            {
                LibraryFolder = _locator.FolderPath,
                LibraryId = libraryId,
                StreamBaseUrl = _sync.StreamBaseUrl(config),
                // Jellyfin's own guess, e.g. a pod IP in Kubernetes, which ffmpeg workers elsewhere cannot reach
                StreamBaseUrlDetected = string.IsNullOrWhiteSpace(config.StreamBaseUrl),
                config.EnforceAccess,
                _sync.LastReport,
                _client.CatalogTime,
                _client.CatalogCount,
                _client.CatalogError,
                _client.AccountStatus,
                LastAccessSync = _access.LastSync,
                LastAccessChanged = _access.LastChanged,
                Users = _userManager.GetUsers()
                    .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(u => new
                    {
                        u.Id,
                        Name = u.Username,
                        IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                        Selected = config.IsAllowed(u.Id),
                        // What Jellyfin's policy grants, which decides what the user sees
                        PolicyGrantsAccess = libraryId is { } id && LibraryAccessSync.PolicyGrantsAccess(u, id),
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

            var series = LibraryLayout.Series(LibraryLayout.Visible(catalog, settings));
            try
            {
                var streams = await client.GetStreamsAsync(newest.Slug, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Ok = true,
                    CatalogCount = catalog.Count,
                    SeriesCount = series.Count,
                    EpisodeCount = series.Sum(s => s.Episodes.Count),
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
        /// Syncs the library now.
        /// </summary>
        [HttpPost("Sync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<SyncReport>> Sync(CancellationToken cancellationToken) =>
            await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Brings the users' library access in line with the selection now.
        /// </summary>
        [HttpPost("SyncAccess")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> SyncAccess(CancellationToken cancellationToken)
        {
            var changed = await _access.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return new { Changed = changed };
        }
    }
}
