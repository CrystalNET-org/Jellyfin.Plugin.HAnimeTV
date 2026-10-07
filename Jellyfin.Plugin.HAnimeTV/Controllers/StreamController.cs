using System.Text;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Controllers
{
    /// <summary>
    /// The stream links of the library's .strm files. Players fetch them without a Jellyfin
    /// login (Jellyfin's ffmpeg has none), so they carry the plugin's stream token instead.
    /// </summary>
    [ApiController]
    [Route("HanimeTV/Stream/{slug}")]
    [AllowAnonymous]
    public class StreamController : ControllerBase
    {
        private const string PlaylistType = "application/vnd.apple.mpegurl";

        private readonly HanimeClient _client;
        private readonly ILogger<StreamController> _logger;

        public StreamController(HanimeClient client, ILogger<StreamController> logger)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Gets the video's best stream as an HLS playlist served through the plugin.
        /// </summary>
        [HttpGet("index.m3u8")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult> GetPlaylist([FromRoute] string slug, [FromQuery] string? token, CancellationToken cancellationToken)
        {
            if (!Authorized(token, out var secret))
            {
                return Forbid403();
            }

            Uri upstream;
            try
            {
                var streams = await _client.GetCachedStreamsAsync(slug, cancellationToken).ConfigureAwait(false);
                upstream = new Uri(streams[0].Url);
            }
            catch (HanimeException ex)
            {
                _logger.LogWarning("hanime.tv: no stream for {Slug}: {Error}", slug, ex.Message);
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }

            return await ProxyAsync(upstream, secret, 0, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a playlist, segment or key of the stream; only URLs the plugin signed.
        /// </summary>
        [HttpGet("proxy/{token}/{signature}/{url}/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult> GetProxied([FromRoute] string slug, [FromRoute] string token, [FromRoute] string signature, [FromRoute] string url, CancellationToken cancellationToken)
        {
            if (!Authorized(token, out var secret) || HlsProxy.Resolve(url, signature, secret) is not { } upstream)
            {
                return Forbid403();
            }

            return await ProxyAsync(upstream, secret, HlsProxy.ProxyDepth, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ActionResult> ProxyAsync(Uri upstream, string secret, int depth, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await _client.FetchMediaAsync(upstream, cancellationToken).ConfigureAwait(false);
            }
            catch (HanimeException ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("hanime.tv: {Host} answered {Status} for a stream", upstream.Host, (int)response.StatusCode);
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

            // Media: segments and keys, passed through whole (HLS segments are small)
            Response.RegisterForDispose(response);
            if (response.Content.Headers.ContentLength is { } length)
            {
                Response.ContentLength = length;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new FileStreamResult(stream, contentType ?? "application/octet-stream");
        }

        private static bool Authorized(string? token, out string secret)
        {
            secret = Plugin.Instance?.Configuration.StreamToken ?? string.Empty;
            return HlsProxy.IsValidToken(token, secret);
        }

        private ObjectResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden, "Invalid or missing stream token");
    }
}
