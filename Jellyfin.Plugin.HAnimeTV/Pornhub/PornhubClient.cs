using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Pornhub
{
    /// <summary>
    /// Pornhub could not be reached or returned something unusable.
    /// </summary>
    public class PornhubException : Exception
    {
        public PornhubException(string message)
            : base(message)
        {
        }

        public PornhubException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// What a search asks for.
    /// </summary>
    /// <param name="Ordering">featured, newest, mostviewed or rating; none for relevance.</param>
    /// <param name="Period">weekly, monthly or alltime; with mostviewed and rating.</param>
    /// <param name="Search">Search terms.</param>
    /// <param name="Category">A category's name.</param>
    public sealed record PornhubQuery(string? Ordering, string? Period = null, string? Search = null, string? Category = null);

    /// <summary>
    /// Reads the catalog from Pornhub's public webmasters API and the streams from the video
    /// pages, the way yt-dlp does.
    /// </summary>
    public sealed class PornhubClient
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        /// <summary>
        /// The site's age check, as a visitor who confirmed it has them.
        /// </summary>
        private const string AgeCookies = "age_verified=1; accessAgeDisclaimerPH=1; accessAgeDisclaimerUK=1; accessPH=1; platform=pc";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ListCacheTime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan StreamCacheTime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CategoryCacheTime = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Func<PornhubSettings> _configuration;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<string, (IReadOnlyList<PornhubVideo> Videos, DateTimeOffset Time)> _lists = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (IReadOnlyList<PornhubStream> Streams, DateTimeOffset Time)> _streams = new(StringComparer.Ordinal);
        private (IReadOnlyList<string> Names, string Source, DateTimeOffset Time)? _categories;

        public PornhubClient(IHttpClientFactory httpClientFactory, Func<PornhubSettings> configuration, ILogger logger, TimeProvider? time = null)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// Gets up to <paramref name="count"/> videos, page by page.
        /// </summary>
        public async Task<IReadOnlyList<PornhubVideo>> SearchAsync(PornhubQuery query, int count, CancellationToken cancellationToken)
        {
            var config = _configuration();
            var key = string.Join('\n', config.ApiUrl, query.Ordering, query.Period, query.Search, query.Category, count);
            if (_lists.TryGetValue(key, out var cached) && _time.GetUtcNow() - cached.Time < ListCacheTime)
            {
                return cached.Videos;
            }

            var videos = new List<PornhubVideo>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            // Pages hold around 20 to 30 videos; a few pages fill a folder
            for (var page = 1; videos.Count < count && page <= 10; page++)
            {
                var url = config.ApiUrl.TrimEnd('/') + "/search?" + Query(
                    ("search", query.Search ?? string.Empty),
                    ("category", query.Category),
                    ("ordering", query.Ordering),
                    ("period", query.Period),
                    ("page", page.ToString(CultureInfo.InvariantCulture)),
                    ("thumbsize", "large_hd"));
                using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                var pageVideos = ParseVideos(document);
                var before = videos.Count;
                videos.AddRange(pageVideos.Where(v => ids.Add(v.Id)));
                // Past the last page, or a page repeating the previous one
                if (videos.Count == before)
                {
                    break;
                }
            }

            var result = videos.Take(count).ToList();
            _lists[key] = (result, _time.GetUtcNow());
            foreach (var old in _lists.Where(e => _time.GetUtcNow() - e.Value.Time >= ListCacheTime).ToList())
            {
                _lists.TryRemove(old);
            }

            return result;
        }

        /// <summary>
        /// Gets the categories' names.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken)
        {
            var config = _configuration();
            if (_categories is { } cached && cached.Source == config.ApiUrl && _time.GetUtcNow() - cached.Time < CategoryCacheTime)
            {
                return cached.Names;
            }

            using var document = await GetJsonAsync(config.ApiUrl.TrimEnd('/') + "/categories", cancellationToken).ConfigureAwait(false);
            var names = document.RootElement.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray()
                    .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("category", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => WebUtility.HtmlDecode(n!).Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : throw new PornhubException("Pornhub returned no categories");
            _categories = (names, config.ApiUrl, _time.GetUtcNow());
            return names;
        }

        /// <summary>
        /// Gets the video's streams, HLS first, then best first.
        /// </summary>
        /// <exception cref="PornhubException">The page has no playable stream.</exception>
        public async Task<IReadOnlyList<PornhubStream>> GetStreamsAsync(string viewkey, CancellationToken cancellationToken)
        {
            if (_streams.TryGetValue(viewkey, out var cached) && _time.GetUtcNow() - cached.Time < StreamCacheTime)
            {
                return cached.Streams;
            }

            var config = _configuration();
            var pageUrl = config.SiteUrl.TrimEnd('/') + "/view_video.php?viewkey=" + Uri.EscapeDataString(viewkey);
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            AddSiteHeaders(request, config);
            request.Headers.TryAddWithoutValidation("Cookie", AgeCookies);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "video page of " + viewkey, cancellationToken).ConfigureAwait(false);
            // A deleted video redirects elsewhere
            if (response.RequestMessage?.RequestUri is { } final && !final.Query.Contains("viewkey=" + viewkey, StringComparison.OrdinalIgnoreCase))
            {
                throw new PornhubException($"Pornhub redirected {viewkey} elsewhere: the video may be deleted or need a login");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (PornhubPage.Unavailable(html) is { } reason)
            {
                throw new PornhubException(reason);
            }

            var streams = new List<PornhubStream>();
            foreach (var stream in PornhubPage.Streams(html))
            {
                if (!stream.IsMediaList)
                {
                    streams.Add(stream);
                    continue;
                }

                // get_media: a list of MP4 files
                using var listRequest = new HttpRequestMessage(HttpMethod.Get, stream.Url);
                AddSiteHeaders(listRequest, config);
                listRequest.Headers.TryAddWithoutValidation("Cookie", AgeCookies);
                try
                {
                    using var listResponse = await SendAsync(listRequest, cancellationToken).ConfigureAwait(false);
                    if (listResponse.IsSuccessStatusCode)
                    {
                        streams.AddRange(PornhubPage.MediaList(await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));
                    }
                }
                catch (PornhubException ex)
                {
                    _logger.LogDebug("Pornhub: could not read the MP4 list of {Viewkey}: {Error}", viewkey, ex.Message);
                }
            }

            var ordered = streams
                .DistinctBy(s => s.Url)
                .OrderByDescending(s => s.IsHls)
                .ThenByDescending(s => s.Height)
                .ToList();
            if (ordered.Count == 0)
            {
                // A challenge page instead of the video, or a changed page
                throw new PornhubException(html.Contains("RNKEY", StringComparison.Ordinal) || html.Contains("onload=\"go()\"", StringComparison.Ordinal)
                    ? "Pornhub answered with a bot check instead of the video page"
                    : $"Found no streams on the page of {viewkey}");
            }

            _streams[viewkey] = (ordered, _time.GetUtcNow());
            foreach (var old in _streams.Where(e => _time.GetUtcNow() - e.Value.Time >= StreamCacheTime).ToList())
            {
                _streams.TryRemove(old);
            }

            return ordered;
        }

        /// <summary>
        /// Fetches a stream's playlist, segment or file with the headers of Pornhub's player
        /// (without Origin and Referer, its CDN answers 412). The caller disposes the response.
        /// </summary>
        public async Task<HttpResponseMessage> FetchMediaAsync(Uri url, string? range, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddSiteHeaders(request, _configuration());
            if (!string.IsNullOrEmpty(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            try
            {
                // No overall timeout: a file may take its time; the player cancels
                return await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new PornhubException($"Could not reach {url.Host}: {ex.Message}", ex);
            }
        }

        internal static IReadOnlyList<PornhubVideo> ParseVideos(JsonDocument document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
            {
                return videos.EnumerateArray().Select(PornhubVideo.FromJson).OfType<PornhubVideo>().ToList();
            }

            // { "code": "2001", "message": "No Videos found!" }: past the last page, or nothing found
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out _))
            {
                return Array.Empty<PornhubVideo>();
            }

            throw new PornhubException("Pornhub's API answered in an unknown format");
        }

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "API request", cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new PornhubException("Pornhub's API did not answer with JSON: " + ex.Message, ex);
                }
            }
        }

        private static void AddSiteHeaders(HttpRequestMessage request, PornhubSettings config)
        {
            var site = config.SiteUrl.TrimEnd('/');
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", site);
            request.Headers.Referrer = new Uri(site + "/");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PornhubException($"{request.RequestUri?.Host} did not answer within {RequestTimeout.TotalSeconds:0} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new PornhubException($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => " (Pornhub refuses the server: its address, e.g. a datacenter or a blocked country, or that it is no browser)",
                (HttpStatusCode)451 => " (Pornhub is not available in the server's country or region)",
                _ => string.Empty,
            };
            throw new PornhubException($"Pornhub refused the {what}: HTTP {(int)response.StatusCode}{hint} {(body.Length > 200 ? body[..200] : body)}".TrimEnd());
        }

        private static string Query(params (string Name, string? Value)[] parameters) =>
            string.Join('&', parameters.Where(p => p.Value is not null).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!)));
    }
}
