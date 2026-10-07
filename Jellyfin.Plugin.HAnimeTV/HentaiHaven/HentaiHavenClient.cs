using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using MediaBrowser.Common.Net;
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
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        private const int MaxListingPages = 500;
        private const int Parallelism = 4;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SeriesMaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan StreamCacheTime = TimeSpan.FromMinutes(10);
        private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

        private readonly IHttpClientFactory _httpClientFactory;
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
            _httpClientFactory = httpClientFactory;
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
            var html = await GetPageAsync(page, null, cookies, cancellationToken).ConfigureAwait(false);
            var streams = Array.Empty<HentaiHavenStream>() as IReadOnlyList<HentaiHavenStream>;
            var player = HentaiHavenPage.PlayerFrame(html, page);
            var playerHtml = html;
            if (player is not null)
            {
                playerHtml = await GetPageAsync(player, page, cookies, cancellationToken).ConfigureAwait(false);
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
        public async Task<HttpResponseMessage> FetchMediaAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request, SiteUrl);
            request.Headers.TryAddWithoutValidation("Origin", SiteUrl.GetLeftPart(UriPartial.Authority));
            if (!string.IsNullOrEmpty(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            try
            {
                return await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new HentaiHavenException($"Could not reach {url.Host}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets an episode page's address relative to the site, as stream links carry it.
        /// </summary>
        public static string RelativePath(Uri site, string episodeUrl)
        {
            var url = new Uri(episodeUrl);
            if (url.AbsoluteUri.StartsWith(site.AbsoluteUri, StringComparison.Ordinal))
            {
                return url.AbsoluteUri[site.AbsoluteUri.Length..];
            }

            // Elsewhere on the site's host: from its root
            return SameHost(url, site) ? url.PathAndQuery : url.AbsoluteUri;
        }

        /// <summary>
        /// Gets the address of an episode's page; only the site's own pages.
        /// </summary>
        internal Uri EpisodeUrl(string path)
        {
            var site = SiteUrl;
            if (!Uri.TryCreate(site, path, out var url) || !SameHost(url, site)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                throw new HentaiHavenException("Not an episode of " + site.Host + ": " + path);
            }

            return url;
        }

        private static bool SameHost(Uri a, Uri b) =>
            string.Equals(WithoutWww(a.Host), WithoutWww(b.Host), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

        private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

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
                var html = await GetPageAsync(url, site, null, cancellationToken, notFoundIsEmpty: page > 1).ConfigureAwait(false);
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
            var html = await GetPageAsync(url, site, null, cancellationToken).ConfigureAwait(false);
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
                episodes = HentaiHavenPage.Episodes(await PostAsync(new Uri(url, "ajax/chapters/"), url, null, cancellationToken).ConfigureAwait(false), url, now);
                if (episodes.Count == 0 && HentaiHavenPage.ChaptersHolderId(html) is { } id)
                {
                    var form = new Dictionary<string, string> { ["action"] = "manga_get_chapters", ["manga"] = id };
                    episodes = HentaiHavenPage.Episodes(await PostAsync(new Uri(site, "wp-admin/admin-ajax.php"), url, form, cancellationToken).ConfigureAwait(false), url, now);
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
            using var form = new MultipartFormDataContent
            {
                { new StringContent("zarat_get_data_player_ajax"), "action" },
                { new StringContent(keys.En), "a" },
                { new StringContent(keys.Iv), "b" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = form };
            AddBrowserHeaders(request, player);
            request.Headers.TryAddWithoutValidation("Origin", player.GetLeftPart(UriPartial.Authority));
            AddCookies(request, cookies);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HentaiHavenException($"The player's API answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return HentaiHavenPage.ApiSources(body, api);
        }

        private async Task<string> GetPageAsync(Uri url, Uri? referer, Dictionary<string, string>? cookies, CancellationToken cancellationToken, bool notFoundIsEmpty = false)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBrowserHeaders(request, referer);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                if (cookies is not null)
                {
                    AddCookies(request, cookies);
                }

                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (cookies is not null && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var cookie in setCookies)
                    {
                        var pair = cookie.Split(';', 2)[0].Split('=', 2);
                        if (pair.Length == 2 && pair[0].Trim().Length > 0)
                        {
                            cookies[pair[0].Trim()] = pair[1].Trim();
                        }
                    }
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsEmpty)
                {
                    return string.Empty;
                }

                if (HentaiHavenPage.IsChallenge(html))
                {
                    throw new HentaiHavenException(url.Host + " answered with a bot check (Cloudflare) instead of the page; it does not let this server in");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HentaiHavenException($"{url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} for {url.AbsolutePath}");
                }

                return html;
            }
        }

        private async Task<string> PostAsync(Uri url, Uri referer, Dictionary<string, string>? form, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>()) };
            AddBrowserHeaders(request, referer);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HentaiHavenException($"{url.Host} answered {(int)response.StatusCode} for {url.AbsolutePath}");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, timeout.Token).ConfigureAwait(false);
                return response;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HentaiHavenException($"{request.RequestUri?.Host} did not answer within {RequestTimeout.TotalSeconds:0} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new HentaiHavenException($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
            }
        }

        private static void AddBrowserHeaders(HttpRequestMessage request, Uri? referer)
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (referer is not null)
            {
                request.Headers.Referrer = referer;
            }
        }

        private static void AddCookies(HttpRequestMessage request, Dictionary<string, string> cookies)
        {
            if (cookies.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => c.Key + "=" + c.Value)));
            }
        }

        private Catalog? LoadCache()
        {
            if (_cacheFile is null || !File.Exists(_cacheFile))
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(_cacheFile);
                var stored = JsonSerializer.Deserialize<StoredCatalog>(stream, CacheJson);
                if (stored?.Site is null || stored.Series is null)
                {
                    return null;
                }

                // Old enough to be read again, but there if reading fails
                return new Catalog(stored.Site, DateTimeOffset.MinValue, stored.Series, ToVideos(stored.Series, new Uri(stored.Site)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or UriFormatException)
            {
                _logger.LogWarning("Hentai Haven: could not read the saved catalog {File}: {Error}", _cacheFile, ex.Message);
                return null;
            }
        }

        private void SaveCache(Catalog catalog)
        {
            if (_cacheFile is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                var temp = _cacheFile + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, new StoredCatalog { Site = catalog.Site, Series = catalog.Series.ToList() }, CacheJson);
                }

                File.Move(temp, _cacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Hentai Haven: could not save the catalog to {File}: {Error}", _cacheFile, ex.Message);
            }
        }

        private sealed record Catalog(string Site, DateTimeOffset FetchedAt, IReadOnlyList<HentaiHavenSeries> Series, IReadOnlyList<HentaiVideo> Videos);

        private sealed class StoredCatalog
        {
            public string? Site { get; set; }

            public List<HentaiHavenSeries>? Series { get; set; }
        }
    }
}
