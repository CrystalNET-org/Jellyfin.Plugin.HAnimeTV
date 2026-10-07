using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using Jellyfin.Plugin.HAnimeTV.OppaiStream;
using Jellyfin.Plugin.HAnimeTV.Pornhub;

namespace Jellyfin.Plugin.HAnimeTV.Streaming
{
    /// <summary>
    /// A video has no stream, or its source could not be reached.
    /// </summary>
    public class StreamUnavailableException : Exception
    {
        public StreamUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Finds a source's streams and fetches them with the headers its player sends.
    /// </summary>
    public interface IStreamResolver
    {
        /// <summary>
        /// Gets the source's name, for logs.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the address of the video's best stream: an HLS playlist or a video file.
        /// </summary>
        /// <param name="id">The video's id in the stream link.</param>
        /// <exception cref="StreamUnavailableException">The video has no stream.</exception>
        Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken);

        /// <summary>
        /// Fetches a playlist, segment, key or file; the caller disposes the response.
        /// </summary>
        /// <exception cref="StreamUnavailableException">The address could not be reached.</exception>
        Task<HttpResponseMessage> FetchAsync(Uri url, string? range, CancellationToken cancellationToken);
    }

    public sealed class HanimeStreamResolver : IStreamResolver
    {
        private readonly HanimeClient _client;

        public HanimeStreamResolver(HanimeClient client) => _client = client;

        public string Name => "hanime.tv";

        public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var streams = await _client.GetCachedStreamsAsync(id, cancellationToken).ConfigureAwait(false);
                return new Uri(streams[0].Url);
            }
            catch (HanimeException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }

        public async Task<HttpResponseMessage> FetchAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.FetchMediaAsync(url, range, cancellationToken).ConfigureAwait(false);
            }
            catch (HanimeException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }
    }

    public sealed class PornhubStreamResolver : IStreamResolver
    {
        private readonly PornhubClient _client;

        public PornhubStreamResolver(PornhubClient client) => _client = client;

        public string Name => "Pornhub";

        public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var streams = await _client.GetStreamsAsync(id, cancellationToken).ConfigureAwait(false);
                return new Uri(streams[0].Url);
            }
            catch (PornhubException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }

        public async Task<HttpResponseMessage> FetchAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.FetchMediaAsync(url, range, cancellationToken).ConfigureAwait(false);
            }
            catch (PornhubException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }
    }

    public sealed class HentaiHavenStreamResolver : IStreamResolver
    {
        private readonly HentaiHavenClient _client;

        public HentaiHavenStreamResolver(HentaiHavenClient client) => _client = client;

        public string Name => "Hentai Haven";

        /// <param name="id">The episode page's path, encoded with <see cref="StreamLinks.EncodeId"/>.</param>
        public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var path = StreamLinks.DecodeId(id) ?? throw new HentaiHavenException("Not an episode: " + id);
                var streams = await _client.GetStreamsAsync(path, cancellationToken).ConfigureAwait(false);
                return new Uri(streams[0].Url);
            }
            catch (HentaiHavenException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }

        public async Task<HttpResponseMessage> FetchAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.FetchMediaAsync(url, range, cancellationToken).ConfigureAwait(false);
            }
            catch (HentaiHavenException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }
    }

    public sealed class OppaiStreamResolver : IStreamResolver
    {
        private readonly OppaiStreamClient _client;

        public OppaiStreamResolver(OppaiStreamClient client) => _client = client;

        public string Name => "oppai.stream";

        /// <param name="id">The episode page's address, encoded with <see cref="StreamLinks.EncodeId"/>.</param>
        public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var path = StreamLinks.DecodeId(id) ?? throw new OppaiStreamException("Not an episode: " + id);
                var media = await _client.GetMediaAsync(path, cancellationToken).ConfigureAwait(false);
                return new Uri(media.Streams[0].Url);
            }
            catch (OppaiStreamException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }

        public async Task<HttpResponseMessage> FetchAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.FetchMediaAsync(url, range, cancellationToken).ConfigureAwait(false);
            }
            catch (OppaiStreamException ex)
            {
                throw new StreamUnavailableException(ex.Message, ex);
            }
        }
    }
}
