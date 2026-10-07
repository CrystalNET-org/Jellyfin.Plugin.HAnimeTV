using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.HentaiHaven
{
    /// <summary>
    /// Hentai Haven could not be read.
    /// </summary>
    public class HentaiHavenException : Exception
    {
        public HentaiHavenException(string message)
            : base(message)
        {
        }

        public HentaiHavenException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Reads the catalog and the streams from Hentai Haven's pages.
    /// </summary>
    /// <remarks>
    /// The catalog is the site's list of all series (its search for nothing, newest first)
    /// and each series' page. Pages of series whose listed episodes did not change are read
    /// once a month only, from a copy kept on disk, so a sync reads the list and what is new.
    /// </remarks>
    public sealed class HentaiHavenClient
    {
        private const int MaxListingPages = 500;
        private const int Parallelism = 4;

        private static readonly TimeSpan SeriesMaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan StreamCacheTime = TimeSpan.FromMinutes(10);

        private readonly SiteHttp _http;
        private readonly Func<HentaiSettings> _configuration;
        private readonly string? _cacheFile;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _catalogLock = new(1, 1);
        private readonly ConcurrentDictionary<string, (IReadOnlyList<HentaiHavenStream> Streams, DateTimeOffset Time)> _streams = new(StringComparer.Ordinal);

        private Catalog? _catalog;

        /// <param name="cacheFile">Where the series' pages are kept between restarts; null for nowhere.</param>
        public HentaiHavenClient(IHttpClientFactory httpClientFactory, Func<HentaiSettings> configuration, string? cacheFile, ILogger logger, TimeProvider? time = null)
        {
            _http = new SiteHttp(httpClientFactory, (message, inner) => inner is null ? new HentaiHavenException(message) : new HentaiHavenException(message, inner));
            _configuration = configuration;
            _cacheFile = cacheFile;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        public DateTimeOffset? CatalogTime => _catalog?.FetchedAt;

        public int? CatalogCount => _catalog?.Videos.Count;

        public string? CatalogError { get; private set; }

        /// <summary>
        /// Gets the site's address, without a trailing slash.
        /// </summary>
        public Uri SiteUrl
        {
            get
            {
                var configured = _configuration().HentaiHavenUrl?.Trim();
                return new Uri((string.IsNullOrEmpty(configured) ? HentaiSettings.DefaultHentaiHavenUrl : configured).TrimEnd('/') + "/");
            }
        }

        /// <summary>
        /// Gets the catalog, read at most once per <see cref="HentaiSettings.CatalogCacheHours"/>;
        /// if reading fails, the last catalog (also one kept on disk) is used.
        /// </summary>
        /// <exception cref="HentaiHavenException">No catalog could be read.</exception>
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
                    var previous = _catalog?.Site == site.AbsoluteUri ? _catalog.Series : Array.Empty<HentaiHavenSeries>();
                    var series = await CrawlAsync(site, previous, int.MaxValue, cancellationToken).ConfigureAwait(false);
                    _catalog = new Catalog(site.AbsoluteUri, _time.GetUtcNow(), series, ToVideos(series, site));
                    CatalogError = null;
                    SaveCache(_catalog);
                    _logger.LogInformation("Hentai Haven: read the catalog, {Series} series, {Episodes} episodes", series.Count, _catalog.Videos.Count);
                    return _catalog.Videos;
                }
                catch (Exception ex) when (ex is HentaiHavenException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    CatalogError = ex.Message;
                    if (_catalog is { } stale && stale.Site == site.AbsoluteUri)
                    {
                        _logger.LogWarning("Hentai Haven: could not read the catalog, using the one from {Time}: {Error}", stale.FetchedAt, ex.Message);
                        return stale.Videos;
                    }

                    throw ex as HentaiHavenException ?? new HentaiHavenException("Could not read the catalog: " + ex.Message, ex);
                }
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        /// <summary>
        /// Reads the first page of the list of series and the newest series' page, bypassing
        /// the cache; for testing the settings.
        /// </summary>
        public async Task<IReadOnlyList<HentaiVideo>> SampleAsync(CancellationToken cancellationToken)
        {
            var site = SiteUrl;
            var series = await CrawlAsync(site, Array.Empty<HentaiHavenSeries>(), 1, cancellationToken).ConfigureAwait(false);
            return ToVideos(series.Take(1).ToList(), site);
        }

        /// <summary>
        /// Gets an episode's streams, best first.
        /// </summary>
        /// <param name="path">The episode page's path, relative to the site.</param>
        /// <exception cref="HentaiHavenException">The episode has no stream.</exception>
        public async Task<IReadOnlyList<HentaiHavenStream>> GetStreamsAsync(string path, CancellationToken cancellationToken)
        {
            var page = EpisodeUrl(path);
            if (_streams.TryGetValue(page.AbsoluteUri, out var cached) && _time.GetUtcNow() - cached.Time < StreamCacheTime)
            {
                return cached.Streams;
            }

            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            var html = await _http.GetPageAsync(page, null, cookies, cancellationToken).ConfigureAwait(false);
            var streams = Array.Empty<HentaiHavenStream>() as IReadOnlyList<HentaiHavenStream>;
            var player = HentaiHavenPage.PlayerFrame(html, page);
            var playerHtml = html;
            if (player is not null)
            {
                playerHtml = await _http.GetPageAsync(player, page, cookies, cancellationToken).ConfigureAwait(false);
            }

            if (HentaiHavenPage.PlayerKeys(playerHtml) is { } keys)
            {
                var api = player is not null && player.AbsolutePath.Contains("/player-logic/", StringComparison.Ordinal)
                    ? new Uri(player, "api.php")
                    : new Uri(SiteUrl, "wp-content/plugins/player-logic/api.php");
                streams = await GetApiStreamsAsync(api, player ?? page, keys, cookies, cancellationToken).ConfigureAwait(false);
            }

            if (streams.Count == 0)
            {
                streams = HentaiHavenPage.DirectStreams(playerHtml, player ?? page);
            }

            if (streams.Count == 0)
            {
                throw new HentaiHavenException(player is null
                    ? "The episode's page has no player; is " + SiteUrl.Host + " a Hentai Haven site?"
                    : "The player of the episode returned no stream");
            }

            foreach (var old in _streams.Where(e => _time.GetUtcNow() - e.Value.Time >= StreamCacheTime).ToList())
            {
                _streams.TryRemove(old);
            }

            _streams[page.AbsoluteUri] = (streams, _time.GetUtcNow());
            return streams;
        }

        /// <summary>
        /// Fetches a stream's playlist, segment or file with the headers of the site's player.
        /// The caller disposes the response.
        /// </summary>
        public Task<HttpResponseMessage> FetchMediaAsync(Uri url, string? range, CancellationToken cancellationToken) =>
            _http.FetchMediaAsync(url, SiteUrl, range, cancellationToken);

        /// <summary>
        /// Gets an episode page's address relative to the site, as stream links carry it.
        /// </summary>
        public static string RelativePath(Uri site, string episodeUrl) => SiteHttp.RelativePath(site, episodeUrl);

        /// <summary>
        /// Gets the address of an episode's page; only the site's own pages.
        /// </summary>
        internal Uri EpisodeUrl(string path)
        {
            var site = SiteUrl;
            if (!Uri.TryCreate(site, path, out var url) || !SiteHttp.SameHost(url, site)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                throw new HentaiHavenException("Not an episode of " + site.Host + ": " + path);
            }

            return url;
        }

        internal static IReadOnlyList<HentaiVideo> ToVideos(IReadOnlyList<HentaiHavenSeries> catalog, Uri site)
        {
            var videos = new List<HentaiVideo>();
            foreach (var series in catalog)
            {
                foreach (var episode in series.Episodes)
                {
                    var released = episode.ReleasedAt ?? (series.Year is { } year ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);
                    videos.Add(new HentaiVideo
                    {
                        Source = HentaiSource.HentaiHaven,
                        Id = RelativePath(site, episode.Url),
                        Name = series.Title + " " + episode.Number.ToString(CultureInfo.InvariantCulture),
                        SeriesName = series.Title,
                        EpisodeNumber = episode.Number,
                        PageUrl = episode.Url,
                        Description = series.Description,
                        Brand = series.Studio,
                        Tags = series.Genres,
                        PosterUrl = series.PosterUrl ?? episode.ThumbnailUrl,
                        ThumbnailUrl = episode.ThumbnailUrl ?? series.PosterUrl,
                        IsCensored = series.Genres.Contains("censored", StringComparer.OrdinalIgnoreCase),
                        CreatedAt = episode.ReleasedAt,
                        ReleasedAt = released,
                    });
                }
            }

            return videos;
        }

        private async Task<IReadOnlyList<HentaiHavenSeries>> CrawlAsync(Uri site, IReadOnlyList<HentaiHavenSeries> previous, int maxPages, CancellationToken cancellationToken)
        {
            // The list of all series: the site's search for nothing, most recently updated first
            var listings = new List<HentaiHavenListing>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= Math.Min(maxPages, MaxListingPages); page++)
            {
                var url = new Uri(site, (page == 1 ? string.Empty : "page/" + page.ToString(CultureInfo.InvariantCulture) + "/") + "?s=&post_type=wp-manga&m_orderby=latest");
                var html = await _http.GetPageAsync(url, site, null, cancellationToken, notFoundIsEmpty: page > 1).ConfigureAwait(false);
                var found = HentaiHavenPage.Listing(html, url).Where(l => seen.Add(l.Url)).ToList();
                if (found.Count == 0)
                {
                    if (page == 1)
                    {
                        throw new HentaiHavenException($"Found no series on {url}; is {site.Host} a Hentai Haven site?");
                    }

                    break;
                }

                listings.AddRange(found);
            }

            var known = previous.GroupBy(s => s.Url, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            var result = new HentaiHavenSeries[listings.Count];
            var read = 0;
            await Parallel.ForEachAsync(
                listings.Select((listing, index) => (listing, index)),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    var (listing, index) = item;
                    if (known.TryGetValue(listing.Url, out var cached)
                        && now - cached.FetchedAt < SeriesMaxAge
                        && listing.EpisodeUrls.All(e => cached.Episodes.Any(c => c.Url == e)))
                    {
                        result[index] = cached;
                        return;
                    }

                    result[index] = await GetSeriesAsync(new Uri(listing.Url), site, token).ConfigureAwait(false);
                    Interlocked.Increment(ref read);
                }).ConfigureAwait(false);

            _logger.LogInformation("Hentai Haven: {Series} series listed, {Read} series pages read", listings.Count, read);
            return result.Where(s => s.Episodes.Count > 0).ToList();
        }

        private async Task<HentaiHavenSeries> GetSeriesAsync(Uri url, Uri site, CancellationToken cancellationToken)
        {
            var html = await _http.GetPageAsync(url, site, null, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var series = HentaiHavenPage.Series(html, url, now);
            if (series.Episodes.Count > 0)
            {
                return series;
            }

            // Themes that load the episodes after the page
            var episodes = Array.Empty<HentaiHavenEpisode>() as IReadOnlyList<HentaiHavenEpisode>;
            try
            {
                episodes = HentaiHavenPage.Episodes(await _http.PostAsync(new Uri(url, "ajax/chapters/"), url, new FormUrlEncodedContent([]), null, cancellationToken).ConfigureAwait(false), url, now);
                if (episodes.Count == 0 && HentaiHavenPage.ChaptersHolderId(html) is { } id)
                {
                    var form = new Dictionary<string, string> { ["action"] = "manga_get_chapters", ["manga"] = id };
                    episodes = HentaiHavenPage.Episodes(await _http.PostAsync(new Uri(site, "wp-admin/admin-ajax.php"), url, new FormUrlEncodedContent(form), null, cancellationToken).ConfigureAwait(false), url, now);
                }
            }
            catch (HentaiHavenException ex)
            {
                _logger.LogDebug("Hentai Haven: no episodes for {Url}: {Error}", url, ex.Message);
            }

            return new HentaiHavenSeries
            {
                Url = series.Url,
                Title = series.Title,
                Description = series.Description,
                PosterUrl = series.PosterUrl,
                Genres = series.Genres,
                Studio = series.Studio,
                Year = series.Year,
                Episodes = episodes,
                FetchedAt = series.FetchedAt,
            };
        }

        private async Task<IReadOnlyList<HentaiHavenStream>> GetApiStreamsAsync(Uri api, Uri player, (string En, string Iv) keys, Dictionary<string, string> cookies, CancellationToken cancellationToken)
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent("zarat_get_data_player_ajax"), "action" },
                { new StringContent(keys.En), "a" },
                { new StringContent(keys.Iv), "b" },
            };
            var body = await _http.PostAsync(api, player, form, cookies, cancellationToken, ajax: false).ConfigureAwait(false);
            return HentaiHavenPage.ApiSources(body, api);
        }

        private Catalog? LoadCache()
        {
            var stored = SiteHttp.LoadJson<StoredCatalog>(_cacheFile, _logger, "Hentai Haven");
            if (stored?.Site is null || stored.Series is null || !Uri.TryCreate(stored.Site, UriKind.Absolute, out var site))
            {
                return null;
            }

            // Old enough to be read again, but there if reading fails
            return new Catalog(stored.Site, DateTimeOffset.MinValue, stored.Series, ToVideos(stored.Series, site));
        }

        private void SaveCache(Catalog catalog) =>
            SiteHttp.SaveJson(_cacheFile, new StoredCatalog { Site = catalog.Site, Series = catalog.Series.ToList() }, _logger, "Hentai Haven");

        private sealed record Catalog(string Site, DateTimeOffset FetchedAt, IReadOnlyList<HentaiHavenSeries> Series, IReadOnlyList<HentaiVideo> Videos);

        private sealed class StoredCatalog
        {
            public string? Site { get; set; }

            public List<HentaiHavenSeries>? Series { get; set; }
        }
    }
}
