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
    /// The catalog is the home page's list of every episode, newest first (<c>?page=N</c>), and
    /// each episode's page for its details. Episode pages are kept on disk and read once
    /// (again after 30 days), so a sync reads the list and what is new. The videos are on a
    /// separate host (nhplayer), whose player page carries each video's address.
    /// </remarks>
    public sealed class HentaiHavenClient
    {
        private const int MaxListingPages = 500;
        private const int Parallelism = 4;

        private static readonly TimeSpan EpisodeMaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan StreamCacheTime = TimeSpan.FromMinutes(10);

        private readonly SiteHttp _http;
        private readonly Func<HentaiSettings> _configuration;
        private readonly string? _cacheFile;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _catalogLock = new(1, 1);
        private readonly ConcurrentDictionary<string, (IReadOnlyList<HentaiHavenStream> Streams, DateTimeOffset Time)> _streams = new(StringComparer.Ordinal);

        /// <summary>
        /// The pages the video hosts expect as Referer, by host, learned from the players.
        /// </summary>
        private readonly ConcurrentDictionary<string, Uri> _referers = new(StringComparer.OrdinalIgnoreCase);

        private Catalog? _catalog;

        /// <param name="cacheFile">Where the episodes' pages are kept between restarts; null for nowhere.</param>
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
        /// Gets the site's address, with a trailing slash.
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
                    var previous = _catalog?.Site == site.AbsoluteUri ? _catalog.Episodes : Array.Empty<HentaiHavenEpisode>();
                    var episodes = await CrawlAsync(site, previous, cancellationToken).ConfigureAwait(false);
                    _catalog = new Catalog(site.AbsoluteUri, _time.GetUtcNow(), episodes, ToVideos(episodes, site));
                    CatalogError = null;
                    SiteHttp.SaveJson(_cacheFile, new StoredCatalog { Site = _catalog.Site, Episodes = episodes.ToList() }, _logger, "Hentai Haven");
                    _logger.LogInformation("Hentai Haven: read the catalog, {Episodes} episodes", episodes.Count);
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
        /// Reads the newest episodes (the first page of the list, the newest's page),
        /// bypassing the cache; for testing the settings.
        /// </summary>
        public async Task<(int Listed, HentaiVideo? Newest)> SampleAsync(CancellationToken cancellationToken)
        {
            var site = SiteUrl;
            var (listing, _) = await ListPageAsync(site, 1, cancellationToken).ConfigureAwait(false);
            if (listing.Count == 0)
            {
                var html = await _http.GetPageAsync(site, null, null, cancellationToken).ConfigureAwait(false);
                throw new HentaiHavenException($"Found no episodes on {site}; is {site.Host} Hentai Haven? It answered {HentaiHavenPage.Describe(html, site)}");
            }

            var episode = await GetEpisodeAsync(listing[0], site, cancellationToken).ConfigureAwait(false);
            return (listing.Count, ToVideos([episode], site)[0]);
        }

        /// <summary>
        /// Gets an episode's streams, best first.
        /// </summary>
        /// <param name="path">The episode page's address, relative to the site.</param>
        /// <exception cref="HentaiHavenException">The episode has no stream.</exception>
        public async Task<IReadOnlyList<HentaiHavenStream>> GetStreamsAsync(string path, CancellationToken cancellationToken)
        {
            var page = EpisodeUrl(path);
            if (_streams.TryGetValue(page.AbsoluteUri, out var cached) && _time.GetUtcNow() - cached.Time < StreamCacheTime)
            {
                return cached.Streams;
            }

            var html = await _http.GetPageAsync(page, SiteUrl, null, cancellationToken).ConfigureAwait(false);
            var player = HentaiHavenPage.Episode(html, page).Player
                ?? throw new HentaiHavenException("The episode's page has no player; " + SiteUrl.Host + " answered " + HentaiHavenPage.Describe(html, page));

            var streams = await GetPlayerStreamsAsync(player, page, cancellationToken).ConfigureAwait(false);
            if (streams.Count == 0)
            {
                throw new HentaiHavenException($"The player at {player.Host} has no video for the episode");
            }

            foreach (var stream in streams)
            {
                if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var url) && stream.Referer is not null)
                {
                    _referers[url.Host] = new Uri(stream.Referer);
                }
            }

            foreach (var old in _streams.Where(e => _time.GetUtcNow() - e.Value.Time >= StreamCacheTime).ToList())
            {
                _streams.TryRemove(old);
            }

            _streams[page.AbsoluteUri] = (streams, _time.GetUtcNow());
            return streams;
        }

        /// <summary>
        /// Fetches a stream's file or playlist with the Referer its host expects: the player's
        /// page for the video hosts, else the site. The caller disposes the response.
        /// </summary>
        public Task<HttpResponseMessage> FetchMediaAsync(Uri url, string? range, CancellationToken cancellationToken) =>
            _http.FetchMediaAsync(url, _referers.TryGetValue(url.Host, out var referer) ? referer : SiteUrl, range, cancellationToken);

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

        internal static IReadOnlyList<HentaiVideo> ToVideos(IReadOnlyList<HentaiHavenEpisode> episodes, Uri site) =>
            episodes.Select(e => new HentaiVideo
            {
                Source = HentaiSource.HentaiHaven,
                Id = RelativePath(site, e.Url),
                Name = e.SeriesName + " " + e.Number.ToString(CultureInfo.InvariantCulture),
                SeriesName = e.SeriesName,
                EpisodeNumber = e.Number,
                PageUrl = e.Url,
                Description = e.Description,
                Brand = e.Studio,
                Tags = e.Genres,
                PosterUrl = e.PosterUrl ?? e.ThumbnailUrl,
                ThumbnailUrl = e.ThumbnailUrl ?? e.PosterUrl,
                Views = e.Views,
                Likes = e.Likes,
                Dislikes = e.Dislikes,
                IsCensored = e.Genres.Contains("censored", StringComparer.OrdinalIgnoreCase),
                CreatedAt = e.UploadedAt ?? e.ReleasedAt,
                ReleasedAt = e.ReleasedAt ?? e.UploadedAt,
                // nhplayer serves MP4 files; the stream link handles a playlist as well
                StreamIsFile = true,
            }).ToList();

        /// <summary>
        /// Reads the player's page: its servers' players (whose page may name the video's
        /// streams), else the video's address the server links carry, else addresses on the page.
        /// </summary>
        private async Task<IReadOnlyList<HentaiHavenStream>> GetPlayerStreamsAsync(Uri player, Uri episode, CancellationToken cancellationToken)
        {
            var html = await _http.GetPageAsync(player, episode, null, cancellationToken).ConfigureAwait(false);
            foreach (var (server, video) in HentaiHavenPage.PlayerServers(html, player))
            {
                try
                {
                    var serverHtml = await _http.GetPageAsync(server, player, null, cancellationToken).ConfigureAwait(false);
                    if (HentaiHavenPage.DirectStreams(serverHtml, server) is { Count: > 0 } direct)
                    {
                        return direct;
                    }
                }
                catch (HentaiHavenException ex)
                {
                    _logger.LogDebug("Hentai Haven: the player {Player} failed: {Error}", server, ex.Message);
                }

                if (video is not null)
                {
                    return [new HentaiHavenStream(video, "MP4", 0, player.AbsoluteUri)];
                }
            }

            return HentaiHavenPage.DirectStreams(html, player);
        }

        private async Task<IReadOnlyList<HentaiHavenEpisode>> CrawlAsync(Uri site, IReadOnlyList<HentaiHavenEpisode> previous, CancellationToken cancellationToken)
        {
            // Every episode, newest first: the first page says how many pages there are
            var (first, lastPage) = await ListPageAsync(site, 1, cancellationToken).ConfigureAwait(false);
            if (first.Count == 0)
            {
                var html = await _http.GetPageAsync(site, null, null, cancellationToken).ConfigureAwait(false);
                throw new HentaiHavenException($"Found no episodes on {site}; is {site.Host} Hentai Haven? It answered {HentaiHavenPage.Describe(html, site)}");
            }

            var pages = new IReadOnlyList<HentaiHavenListing>[Math.Min(lastPage, MaxListingPages)];
            pages[0] = first;
            await Parallel.ForEachAsync(
                Enumerable.Range(2, Math.Max(0, pages.Length - 1)),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (page, token) => pages[page - 1] = (await ListPageAsync(site, page, token).ConfigureAwait(false)).Listing).ConfigureAwait(false);

            var listings = pages.SelectMany(p => p).DistinctBy(l => l.Url).ToList();
            var known = previous.GroupBy(e => e.Url, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            var result = new HentaiHavenEpisode?[listings.Count];
            var read = 0;
            var failed = 0;
            await Parallel.ForEachAsync(
                listings.Select((listing, index) => (listing, index)),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    var (listing, index) = item;
                    known.TryGetValue(listing.Url, out var cached);
                    if (cached is not null && now - cached.FetchedAt < EpisodeMaxAge)
                    {
                        result[index] = cached;
                        return;
                    }

                    try
                    {
                        result[index] = await GetEpisodeAsync(listing, site, token).ConfigureAwait(false);
                        Interlocked.Increment(ref read);
                    }
                    catch (HentaiHavenException ex)
                    {
                        // One missing page does not fail the catalog: the episode keeps its last
                        // read, or waits for the next sync
                        _logger.LogDebug("Hentai Haven: could not read {Url}: {Error}", listing.Url, ex.Message);
                        result[index] = cached;
                        Interlocked.Increment(ref failed);
                    }
                }).ConfigureAwait(false);

            _logger.LogInformation("Hentai Haven: {Episodes} episodes listed, {Read} episode pages read, {Failed} failed", listings.Count, read, failed);
            if (failed > 0 && result.All(e => e is null))
            {
                throw new HentaiHavenException($"Could not read any of the {failed} episode pages; see the log");
            }

            return result.OfType<HentaiHavenEpisode>().ToList();
        }

        private async Task<(IReadOnlyList<HentaiHavenListing> Listing, int LastPage)> ListPageAsync(Uri site, int page, CancellationToken cancellationToken)
        {
            var url = page == 1 ? site : new Uri(site, "?page=" + page.ToString(CultureInfo.InvariantCulture));
            var html = await _http.GetPageAsync(url, site, null, cancellationToken, notFoundIsEmpty: page > 1).ConfigureAwait(false);
            return (HentaiHavenPage.Listing(html, url), HentaiHavenPage.LastPage(html));
        }

        private async Task<HentaiHavenEpisode> GetEpisodeAsync(HentaiHavenListing listing, Uri site, CancellationToken cancellationToken)
        {
            var url = new Uri(listing.Url);
            var html = await _http.GetPageAsync(url, site, null, cancellationToken).ConfigureAwait(false);
            var content = HentaiHavenPage.Episode(html, url);
            var title = content.Title ?? listing.Title;
            var (series, number) = HentaiHavenPage.SeriesAndNumber(title);
            return new HentaiHavenEpisode
            {
                Url = listing.Url,
                Title = title,
                SeriesName = content.SeriesName ?? series,
                Number = number ?? listing.Number ?? HentaiHavenPage.NumberInUrl(listing.Url) ?? 1,
                Description = content.Description,
                Genres = content.Genres,
                Studio = content.Studio,
                PosterUrl = content.PosterUrl ?? listing.ThumbnailUrl,
                ThumbnailUrl = content.ThumbnailUrl,
                ReleasedAt = content.ReleasedAt,
                UploadedAt = content.UploadedAt,
                Views = content.Views,
                Likes = content.Likes,
                Dislikes = content.Dislikes,
                FetchedAt = _time.GetUtcNow(),
            };
        }

        private Catalog? LoadCache()
        {
            var stored = SiteHttp.LoadJson<StoredCatalog>(_cacheFile, _logger, "Hentai Haven");
            if (stored?.Site is null || stored.Episodes is null || !Uri.TryCreate(stored.Site, UriKind.Absolute, out var site))
            {
                return null;
            }

            // Old enough to be read again, but there if reading fails
            return new Catalog(stored.Site, DateTimeOffset.MinValue, stored.Episodes, ToVideos(stored.Episodes, site));
        }

        private sealed record Catalog(string Site, DateTimeOffset FetchedAt, IReadOnlyList<HentaiHavenEpisode> Episodes, IReadOnlyList<HentaiVideo> Videos);

        private sealed class StoredCatalog
        {
            public string? Site { get; set; }

            public List<HentaiHavenEpisode>? Episodes { get; set; }
        }
    }
}
