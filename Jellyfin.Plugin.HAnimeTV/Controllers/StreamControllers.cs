using System.Text;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Controllers
{
    /// <summary>
    /// A source's stream links: what the library's .strm files and the channels' media sources
    /// point at. Players fetch them without a Jellyfin login (Jellyfin's ffmpeg has none), so
    /// they carry the plugin's stream token instead. Everything is fetched through the plugin,
    /// with the headers the source's player sends.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    public abstract class StreamProxyController : ControllerBase
    {
        private const string PlaylistType = "application/vnd.apple.mpegurl";

        private readonly IStreamResolver _resolver;
        private readonly ILogger _logger;

        protected StreamProxyController(IStreamResolver resolver, ILogger logger)
        {
            _resolver = resolver;
            _logger = logger;
        }

        /// <summary>
        /// Gets the video's best stream as an HLS playlist served through the plugin; a video
        /// file redirects to <c>video.mp4</c>.
        /// </summary>
        [HttpGet("index.m3u8")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status302Found)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public Task<ActionResult> GetPlaylist([FromRoute] string id, [FromQuery] string? token, CancellationToken cancellationToken) =>
            GetStreamAsync(id, token, playlist: true, cancellationToken);

        /// <summary>
        /// Gets the video's best stream as a file, with ranges for seeking.
        /// </summary>
        [HttpGet("video.mp4")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public Task<ActionResult> GetVideo([FromRoute] string id, [FromQuery] string? token, CancellationToken cancellationToken) =>
            GetStreamAsync(id, token, playlist: false, cancellationToken);

        /// <summary>
        /// Gets a playlist, segment, key or file of the stream; only URLs the plugin signed.
        /// </summary>
        [HttpGet("proxy/{token}/{signature}/{url}/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult> GetProxied([FromRoute] string token, [FromRoute] string signature, [FromRoute] string url, CancellationToken cancellationToken)
        {
            if (!Authorized(token, out var secret) || HlsProxy.Resolve(url, signature, secret) is not { } upstream)
            {
                return Forbid403();
            }

            return await ProxyAsync(upstream, secret, HlsProxy.ProxyDepth, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ActionResult> GetStreamAsync(string id, string? token, bool playlist, CancellationToken cancellationToken)
        {
            if (!Authorized(token, out var secret))
            {
                return Forbid403();
            }

            Uri upstream;
            try
            {
                upstream = await _resolver.ResolveAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (StreamUnavailableException ex)
            {
                _logger.LogWarning("{Source}: no stream for {Id}: {Error}", _resolver.Name, id, ex.Message);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }

            // A file asked for as a playlist: players follow the redirect and read the file
            var redirect = playlist ? "video.mp4?token=" + Uri.EscapeDataString(secret) : null;
            return await ProxyAsync(upstream, secret, 0, redirect, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ActionResult> ProxyAsync(Uri upstream, string secret, int depth, string? fileRedirect, CancellationToken cancellationToken)
        {
            string? range = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;
            if (fileRedirect is not null && !HlsProxy.IsPlaylist(null, upstream) && LooksLikeFile(upstream))
            {
                return Redirect(fileRedirect);
            }

            HttpResponseMessage response;
            try
            {
                response = await _resolver.FetchAsync(upstream, range, cancellationToken).ConfigureAwait(false);
            }
            catch (StreamUnavailableException ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Source}: {Host} answered {Status} for a stream", _resolver.Name, upstream.Host, (int)response.StatusCode);
                var status = (int)response.StatusCode;
                response.Dispose();
                return StatusCode(status);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (HlsProxy.IsPlaylist(contentType, upstream))
            {
                using (response)
                {
                    var playlist = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var rewritten = HlsProxy.Rewrite(playlist, upstream, uri => HlsProxy.Link(uri, secret, depth));
                    Response.Headers.CacheControl = "no-store";
                    return File(Encoding.UTF8.GetBytes(rewritten), PlaylistType);
                }
            }

            if (fileRedirect is not null)
            {
                response.Dispose();
                return Redirect(fileRedirect);
            }

            // Media: segments, keys and files, passed through with their ranges
            using (response)
            {
                Response.StatusCode = (int)response.StatusCode;
                Response.ContentType = contentType ?? "application/octet-stream";
                if (response.Content.Headers.ContentLength is { } length)
                {
                    Response.ContentLength = length;
                }

                if (response.Content.Headers.ContentRange is { } contentRange)
                {
                    Response.Headers.ContentRange = contentRange.ToString();
                }

                if (response.Headers.AcceptRanges.Count > 0)
                {
                    Response.Headers.AcceptRanges = string.Join(", ", response.Headers.AcceptRanges);
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    try
                    {
                        await stream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                        // The player stopped reading, e.g. to seek
                    }
                }

                return new EmptyResult();
            }
        }

        private static bool LooksLikeFile(Uri url) =>
            url.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || url.AbsolutePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);

        private static bool Authorized(string? token, out string secret)
        {
            secret = Plugin.Instance?.Configuration.StreamToken ?? string.Empty;
            return HlsProxy.IsValidToken(token, secret);
        }

        private ObjectResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden, "Invalid or missing stream token");
    }

    /// <summary>
    /// hanime.tv's streams, by slug.
    /// </summary>
    [Route("HanimeTV/Stream/{id}")]
    public class HanimeStreamController : StreamProxyController
    {
        public HanimeStreamController(HanimeStreamResolver resolver, ILogger<HanimeStreamController> logger)
            : base(resolver, logger)
        {
        }
    }

    /// <summary>
    /// Hentai Haven's streams, by the encoded path of the episode's page.
    /// </summary>
    [Route("HanimeTV/HentaiHaven/{id}")]
    public class HentaiHavenStreamController : StreamProxyController
    {
        public HentaiHavenStreamController(HentaiHavenStreamResolver resolver, ILogger<HentaiHavenStreamController> logger)
            : base(resolver, logger)
        {
        }
    }

    /// <summary>
    /// oppai.stream's streams, by the encoded address of the episode's page.
    /// </summary>
    [Route("HanimeTV/OppaiStream/{id}")]
    public class OppaiStreamStreamController : StreamProxyController
    {
        public OppaiStreamStreamController(OppaiStreamResolver resolver, ILogger<OppaiStreamStreamController> logger)
            : base(resolver, logger)
        {
        }
    }

    /// <summary>
    /// Pornhub's streams, by viewkey.
    /// </summary>
    [Route("HanimeTV/Pornhub/{id}")]
    public class PornhubStreamController : StreamProxyController
    {
        public PornhubStreamController(PornhubStreamResolver resolver, ILogger<PornhubStreamController> logger)
            : base(resolver, logger)
        {
        }
    }
}
