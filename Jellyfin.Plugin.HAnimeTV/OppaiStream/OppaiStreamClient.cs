using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.OppaiStream
{
    /// <summary>
    /// oppai.stream could not be read.
    /// </summary>
    public class OppaiStreamException : Exception
    {
        public OppaiStreamException(string message)
            : base(message)
        {
        }

        public OppaiStreamException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// An episode's streams, best first, and subtitles.
    /// </summary>
    public sealed record OppaiStreamMedia(IReadOnlyList<OppaiStreamStream> Streams, IReadOnlyList<HentaiSubtitle> Subtitles);

    /// <summary>
    /// Reads the catalog and the streams from oppai.stream's pages.
    /// </summary>
    /// <remarks>
    /// The catalog is the site's search, most recently uploaded first, which lists every
    /// episode, and each episode's page for its metadata and subtitles. Episode pages are kept
    /// on disk and read once (again after 30 days), so a sync reads the list and what is new.
    /// </remarks>
    public sealed class OppaiStreamClient
    {
        internal const int PageSize = 36;

        private const int MaxListingPages = 300;
        private const int Parallelism = 4;

        private static readonly TimeSpan EpisodeMaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan StreamCacheTime = TimeSpan.FromMinutes(10);

        private readonly SiteHttp _http;
        private readonly Func<HentaiSettings> _configuration;
        private readonly string? _cacheFile;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _catalogLock = new(1, 1);
        private readonly ConcurrentDictionary<string, (OppaiStreamMedia Media, DateTimeOffset Time)> _media = new(StringComparer.Ordinal);

        private Catalog? _catalog;

        /// <param name="cacheFile">Where the episodes' pages are kept between restarts; null for nowhere.</param>
        public OppaiStreamClient(IHttpClientFactory httpClientFactory, Func<HentaiSettings> configuration, string? cacheFile, ILogger logger, TimeProvider? time = null)
        {
            _http = new SiteHttp(httpClientFactory, (message, inner) => inner is null ? new OppaiStreamException(message) : new OppaiStreamException(message, inner));
            _configuration = configuration;
            _cacheFile = cacheFile;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        public DateTimeOffset? CatalogTime => _catalog?.FetchedAt;

        public int? CatalogCount => _catalog?.Videos.Count;

        public string? CatalogError { get; private set; }

        /// <summary>
        /// Gets the site's address, with a trailing slash.
        /// </summary>
        public Uri SiteUrl
        {
            get
            {
                var configured = _configuration().OppaiStreamUrl?.Trim();
                return new Uri((string.IsNullOrEmpty(configured) ? HentaiSettings.DefaultOppaiStreamUrl : configured).TrimEnd('/') + "/");
            }
        }

        /// <summary>
        /// Gets the catalog, read at most once per <see cref="HentaiSettings.CatalogCacheHours"/>;
        /// if reading fails, the last catalog (also one kept on disk) is used.
        /// </summary>
        /// <exception cref="OppaiStreamException">No catalog could be read.</exception>
        public async Task<IReadOnlyList<HentaiVideo>> GetCatalogAsync(CancellationToken cancellationToken)
        {
            var site = SiteUrl;
            var maxAge = TimeSpan.FromHours(Math.Max(1, _configuration().CatalogCacheHours));
            if (_catalog is { } cached && cached.Site == site.AbsoluteUri && _time.GetUtcNow() - cached.FetchedAt < maxAge)
            {
                return cached.Videos;
            }

            await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _catalog ??= LoadCache();
                if (_catalog is { } current && current.Site == site.AbsoluteUri && _time.GetUtcNow() - current.FetchedAt < maxAge)
                {
                    return current.Videos;
                }

                try
                {
                    var previous = _catalog?.Site == site.AbsoluteUri ? _catalog.Episodes : Array.Empty<OppaiStreamEpisode>();
                    var episodes = await CrawlAsync(site, previous, MaxListingPages, cancellationToken).ConfigureAwait(false);
                    _catalog = new Catalog(site.AbsoluteUri, _time.GetUtcNow(), episodes, ToVideos(episodes, site));
                    CatalogError = null;
                    SiteHttp.SaveJson(_cacheFile, new StoredCatalog { Site = _catalog.Site, Episodes = episodes.ToList() }, _logger, "oppai.stream");
                    _logger.LogInformation("oppai.stream: read the catalog, {Episodes} episodes", episodes.Count);
                    return _catalog.Videos;
                }
                catch (Exception ex) when (ex is OppaiStreamException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    CatalogError = ex.Message;
                    if (_catalog is { } stale && stale.Site == site.AbsoluteUri)
                    {
                        _logger.LogWarning("oppai.stream: could not read the catalog, using the one from {Time}: {Error}", stale.FetchedAt, ex.Message);
                        return stale.Videos;
                    }

                    throw ex as OppaiStreamException ?? new OppaiStreamException("Could not read the catalog: " + ex.Message, ex);
                }
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        /// <summary>
        /// Reads the newest episodes (the first page of the list, the newest's page),
        /// bypassing the cache; for testing the settings.
        /// </summary>
        public async Task<(int Listed, HentaiVideo? Newest)> SampleAsync(CancellationToken cancellationToken)
        {
            var site = SiteUrl;
            var listing = await ListPageAsync(site, 1, cancellationToken).ConfigureAwait(false);
            if (listing.Count == 0)
            {
                return (0, null);
            }

            var episode = await GetEpisodeAsync(listing[0], site, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return (listing.Count, ToVideos([episode], site)[0]);
        }

        /// <summary>
        /// Gets an episode's streams, best first, and subtitles.
        /// </summary>
        /// <param name="path">The episode page's address, relative to the site.</param>
        /// <exception cref="OppaiStreamException">The episode has no stream.</exception>
        public async Task<OppaiStreamMedia> GetMediaAsync(string path, CancellationToken cancellationToken)
        {
            var page = EpisodeUrl(path);
            if (_media.TryGetValue(page.AbsoluteUri, out var cached) && _time.GetUtcNow() - cached.Time < StreamCacheTime)
            {
                return cached.Media;
            }

            var html = await _http.GetPageAsync(page, SiteUrl, null, cancellationToken).ConfigureAwait(false);
            var content = OppaiStreamPage.Episode(html, page);
            if (content.Streams.Count == 0)
            {
                throw new OppaiStreamException("The episode's page has no streams (no \"availableres\"); is " + SiteUrl.Host + " oppai.stream?");
            }

            var media = new OppaiStreamMedia(content.Streams, content.Subtitles);
            foreach (var old in _media.Where(e => _time.GetUtcNow() - e.Value.Time >= StreamCacheTime).ToList())
            {
                _media.TryRemove(old);
            }

            _media[page.AbsoluteUri] = (media, _time.GetUtcNow());
            return media;
        }

        /// <summary>
        /// Fetches a stream's file or playlist, or a subtitle, with the site's headers. The
        /// caller disposes the response.
        /// </summary>
        public Task<HttpResponseMessage> FetchMediaAsync(Uri url, string? range, CancellationToken cancellationToken) =>
            _http.FetchMediaAsync(url, SiteUrl, range, cancellationToken);

        /// <summary>
        /// Gets the address of an episode's page; only the site's own pages.
        /// </summary>
        internal Uri EpisodeUrl(string path)
        {
            var site = SiteUrl;
            if (!Uri.TryCreate(site, path, out var url) || !SiteHttp.SameHost(url, site)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                throw new OppaiStreamException("Not an episode of " + site.Host + ": " + path);
            }

            return url;
        }

        internal static IReadOnlyList<HentaiVideo> ToVideos(IReadOnlyList<OppaiStreamEpisode> episodes, Uri site) =>
            episodes.Select(e => new HentaiVideo
            {
                Source = HentaiSource.OppaiStream,
                Id = SiteHttp.RelativePath(site, e.Url),
                Name = e.SeriesName + " " + e.Number.ToString(CultureInfo.InvariantCulture),
                SeriesName = e.SeriesName,
                EpisodeNumber = e.Number,
                PageUrl = e.Url,
                Description = e.Description,
                Brand = e.Studio,
                Tags = e.Genres,
                PosterUrl = e.PosterUrl ?? e.ThumbnailUrl,
                ThumbnailUrl = e.ThumbnailUrl ?? e.PosterUrl,
                IsCensored = e.Genres.Contains("censored", StringComparer.OrdinalIgnoreCase),
                CreatedAt = e.FirstSeen.UtcDateTime,
                StreamIsFile = e.StreamIsFile,
                Subtitles = e.Subtitles,
            }).ToList();

        private async Task<IReadOnlyList<OppaiStreamEpisode>> CrawlAsync(Uri site, IReadOnlyList<OppaiStreamEpisode> previous, int maxPages, CancellationToken cancellationToken)
        {
            // Every episode, most recently uploaded first
            var listings = new List<OppaiStreamListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= maxPages; page++)
            {
                var found = await ListPageAsync(site, page, cancellationToken).ConfigureAwait(false);
                var added = found.Where(l => seen.Add(l.Url)).ToList();
                listings.AddRange(added);
                if (page == 1 && found.Count == 0)
                {
                    throw new OppaiStreamException($"Found no episodes in {site.Host}'s search; is it oppai.stream?");
                }

                // A short page is the last; one that only repeats earlier ones too
                if (found.Count < PageSize || added.Count == 0)
                {
                    break;
                }
            }

            var known = previous.GroupBy(e => e.Url, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            var result = new OppaiStreamEpisode?[listings.Count];
            var read = 0;
            await Parallel.ForEachAsync(
                listings.Select((listing, index) => (listing, index)),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    var (listing, index) = item;
                    if (known.TryGetValue(listing.Url, out var cached) && now - cached.FetchedAt < EpisodeMaxAge)
                    {
                        result[index] = cached;
                        return;
                    }

                    // The site shows no upload dates: new episodes count as uploaded now, in the
                    // list's order
                    var firstSeen = cached?.FirstSeen ?? now.AddSeconds(-index);
                    try
                    {
                        result[index] = await GetEpisodeAsync(listing, site, firstSeen, token).ConfigureAwait(false);
                        Interlocked.Increment(ref read);
                    }
                    catch (OppaiStreamException ex) when (cached is not null)
                    {
                        _logger.LogDebug("oppai.stream: keeping the last read of {Url}: {Error}", listing.Url, ex.Message);
                        result[index] = cached;
                    }
                }).ConfigureAwait(false);

            _logger.LogInformation("oppai.stream: {Episodes} episodes listed, {Read} episode pages read", listings.Count, read);
            return result.OfType<OppaiStreamEpisode>().ToList();
        }

        private async Task<IReadOnlyList<OppaiStreamListing>> ListPageAsync(Uri site, int page, CancellationToken cancellationToken)
        {
            var url = new Uri(site, "actions/search.php?text=&order=uploaded&page=" + page.ToString(CultureInfo.InvariantCulture) + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture));
            var html = await _http.GetPageAsync(url, site, null, cancellationToken, notFoundIsEmpty: page > 1).ConfigureAwait(false);
            return OppaiStreamPage.Listing(html, url);
        }

        private async Task<OppaiStreamEpisode> GetEpisodeAsync(OppaiStreamListing listing, Uri site, DateTimeOffset firstSeen, CancellationToken cancellationToken)
        {
            var url = new Uri(listing.Url);
            var html = await _http.GetPageAsync(url, site, null, cancellationToken).ConfigureAwait(false);
            var content = OppaiStreamPage.Episode(html, url);

            // "Title Ep 3" on the page, "Title 3" in the list
            var (series, number) = OppaiStreamPage.SeriesAndNumber(content.Title ?? listing.Title);
            if (number is null)
            {
                (series, number) = OppaiStreamPage.SeriesAndNumber(listing.Title);
            }

            return new OppaiStreamEpisode
            {
                Url = listing.Url,
                Title = content.Title ?? listing.Title,
                SeriesName = series,
                Number = number ?? 1,
                Description = content.Description,
                Genres = content.Genres,
                Studio = content.Studio,
                PosterUrl = content.PosterUrl,
                ThumbnailUrl = listing.ThumbnailUrl ?? content.PosterUrl,
                StreamIsFile = content.Streams.Count > 0 && !content.Streams[0].IsHls,
                Subtitles = content.Subtitles,
                FirstSeen = firstSeen,
                FetchedAt = _time.GetUtcNow(),
            };
        }

        private Catalog? LoadCache()
        {
            var stored = SiteHttp.LoadJson<StoredCatalog>(_cacheFile, _logger, "oppai.stream");
            if (stored?.Site is null || stored.Episodes is null || !Uri.TryCreate(stored.Site, UriKind.Absolute, out var site))
            {
                return null;
            }

            // Old enough to be read again, but there if reading fails
            return new Catalog(stored.Site, DateTimeOffset.MinValue, stored.Episodes, ToVideos(stored.Episodes, site));
        }

        private sealed record Catalog(string Site, DateTimeOffset FetchedAt, IReadOnlyList<OppaiStreamEpisode> Episodes, IReadOnlyList<HentaiVideo> Videos);

        private sealed class StoredCatalog
        {
            public string? Site { get; set; }

            public List<OppaiStreamEpisode>? Episodes { get; set; }
        }
    }
}
