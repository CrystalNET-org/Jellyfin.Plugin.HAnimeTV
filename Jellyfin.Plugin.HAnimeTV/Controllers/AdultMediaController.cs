using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using Jellyfin.Plugin.HAnimeTV.Library;
using Jellyfin.Plugin.HAnimeTV.OppaiStream;
using Jellyfin.Plugin.HAnimeTV.Pornhub;
using Jellyfin.Plugin.HAnimeTV.Streaming;
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
    public class AdultMediaController : ControllerBase
    {
        private readonly HanimeClient _hanime;
        private readonly HentaiCatalog _catalog;
        private readonly LibrarySync _sync;
        private readonly AccessSync _access;
        private readonly LibraryLocator _locator;
        private readonly StreamLinks _links;
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;

        public AdultMediaController(
            HanimeClient hanime,
            HentaiCatalog catalog,
            LibrarySync sync,
            AccessSync access,
            LibraryLocator locator,
            StreamLinks links,
            IUserManager userManager,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            _hanime = hanime;
            _catalog = catalog;
            _sync = sync;
            _access = access;
            _locator = locator;
            _links = links;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Gets the providers' state and which users can see their channels and library.
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

            var surfaces = _access.Surfaces();
            return new
            {
                StreamBaseUrl = _links.BaseUrl(config),
                // Jellyfin's own guess, e.g. a pod IP in Kubernetes, which ffmpeg workers elsewhere cannot reach
                StreamBaseUrlDetected = StreamLinks.IsGuessed(config),
                config.EnforceAccess,
                Hentai = new
                {
                    config.Hentai.Mode,
                    LibraryFolder = _locator.FolderPath,
                    LibraryId = _locator.Find(),
                    _sync.LastReport,
                    Sources = _catalog.Status(config.Hentai),
                    _hanime.AccountStatus,
                },
                Pornhub = new { config.Pornhub.Mode },
                LastAccessSync = _access.LastSync,
                LastAccessChanged = _access.LastChanged,
                Surfaces = surfaces.Select(s => new { s.Name, s.IsChannel, s.Id }),
                Users = _userManager.GetUsers()
                    .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(u => new
                    {
                        u.Id,
                        Name = u.Username,
                        IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                        // What Jellyfin's policy grants, which decides what the user sees
                        Access = surfaces.Select(s => new
                        {
                            s.Name,
                            s.IsChannel,
                            Selected = s.Grants(config, u.Id),
                            Granted = AccessSync.PolicyGrants(u, s),
                        }),
                    }),
            };
        }

        /// <summary>
        /// Tests a provider with the given settings: reads its catalog and the streams of its
        /// newest video.
        /// </summary>
        /// <param name="provider">"hentai" or "pornhub".</param>
        [HttpPost("Test")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> Test([FromQuery] string? provider, [FromBody] PluginConfiguration settings, CancellationToken cancellationToken)
        {
            if (string.Equals(provider, "pornhub", StringComparison.OrdinalIgnoreCase))
            {
                return await TestPornhubAsync(settings.Pornhub ?? new PornhubSettings(), cancellationToken).ConfigureAwait(false);
            }

            var hentai = settings.Hentai ?? new HentaiSettings();
            var results = new List<object>();
            if (hentai.HanimeEnabled)
            {
                results.Add(await TestHanimeAsync(hentai, cancellationToken).ConfigureAwait(false));
            }

            if (hentai.OppaiStreamEnabled)
            {
                results.Add(await TestOppaiStreamAsync(hentai, cancellationToken).ConfigureAwait(false));
            }

            if (hentai.HentaiHavenEnabled)
            {
                results.Add(await TestHentaiHavenAsync(hentai, cancellationToken).ConfigureAwait(false));
            }

            return new { Sources = results };
        }

        /// <summary>
        /// Syncs the hentai library now.
        /// </summary>
        [HttpPost("Sync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<SyncReport>> Sync(CancellationToken cancellationToken) =>
            await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Brings the users' channel and library access in line with the selections now.
        /// </summary>
        [HttpPost("SyncAccess")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> SyncAccess(CancellationToken cancellationToken)
        {
            var changed = await _access.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return new { Changed = changed };
        }

        private async Task<object> TestHanimeAsync(HentaiSettings settings, CancellationToken cancellationToken)
        {
            const string Name = "hanime.tv";
            var client = new HanimeClient(_httpClientFactory, () => settings, _loggerFactory.CreateLogger<HanimeClient>());
            IReadOnlyList<HentaiVideo> catalog;
            try
            {
                catalog = await client.DownloadCatalogAsync(settings.SearchUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HanimeException ex)
            {
                return new { Name, Ok = false, CatalogError = ex.Message };
            }

            var newest = catalog.OrderByDescending(v => v.CreatedAt ?? DateTime.MinValue).FirstOrDefault();
            if (newest is null)
            {
                return new { Name, Ok = false, VideoCount = 0, CatalogError = "The catalog is empty" };
            }

            var series = LibraryLayout.Series(LibraryLayout.Visible(catalog, settings));
            try
            {
                var streams = await client.GetStreamsAsync(newest.Id, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Name,
                    Ok = true,
                    VideoCount = catalog.Count,
                    SeriesCount = series.Count,
                    TestedVideo = newest.Name,
                    Streams = streams.Select(s => s.Label),
                    client.AccountStatus,
                };
            }
            catch (HanimeException ex)
            {
                return new { Name, Ok = false, VideoCount = catalog.Count, TestedVideo = newest.Name, StreamError = ex.Message, client.AccountStatus };
            }
        }

        private async Task<object> TestOppaiStreamAsync(HentaiSettings settings, CancellationToken cancellationToken)
        {
            const string Name = "oppai.stream";
            var client = new OppaiStreamClient(_httpClientFactory, () => settings, null, _loggerFactory.CreateLogger<OppaiStreamClient>());
            HentaiVideo? newest;
            int listed;
            try
            {
                // The first page of the list and the newest episode: the whole list takes minutes
                (listed, newest) = await client.SampleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OppaiStreamException ex)
            {
                return new { Name, Ok = false, CatalogError = ex.Message };
            }

            if (newest is null)
            {
                return new { Name, Ok = false, VideoCount = 0, CatalogError = "The search lists no episodes" };
            }

            try
            {
                var media = await client.GetMediaAsync(newest.Id, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Name,
                    Ok = true,
                    VideoCount = listed,
                    TestedVideo = newest.Name,
                    Streams = media.Streams.Select(s => (s.IsHls ? "HLS " : "MP4 ") + s.Label)
                        .Concat(media.Subtitles.Select(s => "subtitles: " + s.Label)),
                };
            }
            catch (OppaiStreamException ex)
            {
                return new { Name, Ok = false, VideoCount = listed, TestedVideo = newest.Name, StreamError = ex.Message };
            }
        }

        private async Task<object> TestHentaiHavenAsync(HentaiSettings settings, CancellationToken cancellationToken)
        {
            const string Name = "Hentai Haven";
            var client = new HentaiHavenClient(_httpClientFactory, () => settings, null, _loggerFactory.CreateLogger<HentaiHavenClient>());
            HentaiVideo? newest;
            int listed;
            try
            {
                // The first page of the list and the newest episode: the whole list takes minutes
                (listed, newest) = await client.SampleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HentaiHavenException ex)
            {
                return new { Name, Ok = false, CatalogError = ex.Message };
            }

            if (newest is null)
            {
                return new { Name, Ok = false, VideoCount = 0, CatalogError = "The list shows no episodes" };
            }

            try
            {
                var streams = await client.GetStreamsAsync(newest.Id, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Name,
                    Ok = true,
                    VideoCount = listed,
                    TestedVideo = newest.Name,
                    Streams = streams.Select(s => (s.IsHls ? "HLS " : "MP4 ") + new Uri(s.Url).Host),
                };
            }
            catch (HentaiHavenException ex)
            {
                return new { Name, Ok = false, VideoCount = listed, TestedVideo = newest.Name, StreamError = ex.Message };
            }
        }

        private async Task<object> TestPornhubAsync(PornhubSettings settings, CancellationToken cancellationToken)
        {
            const string Name = "Pornhub";
            var client = new PornhubClient(_httpClientFactory, () => settings, _loggerFactory.CreateLogger<PornhubClient>());
            IReadOnlyList<PornhubVideo> videos;
            try
            {
                videos = await client.SearchAsync(new PornhubQuery("newest"), 10, cancellationToken).ConfigureAwait(false);
            }
            catch (PornhubException ex)
            {
                return new { Sources = new[] { new { Name, Ok = false, CatalogError = ex.Message } } };
            }

            if (videos.Count == 0)
            {
                return new { Sources = new[] { new { Name, Ok = false, CatalogError = "The API returned no videos" } } };
            }

            var video = videos[0];
            try
            {
                var streams = await client.GetStreamsAsync(video.Id, cancellationToken).ConfigureAwait(false);
                return new
                {
                    Sources = new object[]
                    {
                        new
                        {
                            Name,
                            Ok = true,
                            VideoCount = videos.Count,
                            TestedVideo = video.Title,
                            Streams = streams.Select(s => (s.IsHls ? "HLS " : "MP4 ") + (s.Height > 0 ? s.Height + "p" : string.Empty)),
                        },
                    },
                };
            }
            catch (PornhubException ex)
            {
                return new { Sources = new object[] { new { Name, Ok = false, VideoCount = videos.Count, TestedVideo = video.Title, StreamError = ex.Message } } };
            }
        }
    }
}
