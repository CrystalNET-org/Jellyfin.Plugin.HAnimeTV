using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Hentai
{
    /// <summary>
    /// The cause of a site's error when the site answered with a bot check.
    /// </summary>
    public sealed class SiteChallengeException : Exception
    {
        public SiteChallengeException(string host)
            : base(host + " answered with a bot check")
        {
        }
    }

    /// <summary>
    /// Reads a site's pages as a browser does: its headers, cookies, a retry when the site asks
    /// for a pause, at most one request per interval, and Cloudflare's bot checks passed with
    /// FlareSolverr if it is set up (else a clear error). Shared by the sites read from their
    /// pages.
    /// </summary>
    internal sealed class SiteHttp
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        // FlareSolverr opens the page in a browser and waits for the check to pass
        private static readonly TimeSpan SolverTimeout = TimeSpan.FromSeconds(60);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Func<string, Exception?, Exception> _error;
        private readonly Func<string?> _flareSolverrUrl;
        private readonly TimeSpan _requestInterval;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly SemaphoreSlim _paceLock = new(1, 1);
        private readonly SemaphoreSlim _solverLock = new(1, 1);

        /// <summary>
        /// The cookies and browser with which FlareSolverr passed a site's check, by host: they
        /// let later requests in without it while they are valid.
        /// </summary>
        private readonly ConcurrentDictionary<string, Clearance> _clearances = new(StringComparer.OrdinalIgnoreCase);

        private TimeSpan _nextRequest;

        /// <param name="error">Makes the site's exception from a message and its cause.</param>
        /// <param name="flareSolverrUrl">FlareSolverr's address, if one is set up.</param>
        /// <param name="requestInterval">The least time between two pages' requests.</param>
        public SiteHttp(IHttpClientFactory httpClientFactory, Func<string, Exception?, Exception> error, Func<string?>? flareSolverrUrl = null, TimeSpan requestInterval = default)
        {
            _httpClientFactory = httpClientFactory;
            _error = error;
            _flareSolverrUrl = flareSolverrUrl ?? (() => null);
            _requestInterval = requestInterval;
        }

        /// <summary>
        /// Gets a page's HTML.
        /// </summary>
        /// <param name="cookies">Cookies to send, and to add those the site sets to; null for none.</param>
        /// <param name="notFoundIsEmpty">Whether a missing page is empty rather than an error.</param>
        /// <param name="ajax">Whether to ask as the site's own scripts do (jQuery's <c>$.ajax</c>).</param>
        public async Task<string> GetPageAsync(Uri url, Uri? referer, Dictionary<string, string>? cookies, CancellationToken cancellationToken, bool notFoundIsEmpty = false, bool ajax = false)
        {
            var retriedWithClearance = false;
            for (var attempt = 1; ; attempt++)
            {
                await PaceAsync(cancellationToken).ConfigureAwait(false);
                _clearances.TryGetValue(url.Host, out var clearance);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddBrowserHeaders(request, referer, clearance?.UserAgent);
                if (ajax)
                {
                    request.Headers.TryAddWithoutValidation("Accept", "*/*");
                    request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                }

                AddCookies(request, cookies, clearance?.Cookies);

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

                if (IsChallenge(response, html))
                {
                    if (FlareSolverrUri() is not { } solver)
                    {
                        throw _error(url.Host + " answered with a bot check (Cloudflare) instead of the page; it does not let this server in. A FlareSolverr address in the settings gets past it", new SiteChallengeException(url.Host));
                    }

                    await _solverLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // FlareSolverr passed the check while this request was under way: its cookies may do
                        if (!retriedWithClearance && _clearances.TryGetValue(url.Host, out var current) && !ReferenceEquals(current, clearance))
                        {
                            retriedWithClearance = true;
                            continue;
                        }

                        var (status, page) = await SolveAsync(solver, url, cancellationToken).ConfigureAwait(false);
                        if (status == (int)HttpStatusCode.NotFound && notFoundIsEmpty)
                        {
                            return string.Empty;
                        }

                        if (HentaiHavenPage.IsChallenge(page))
                        {
                            throw _error(url.Host + " answered FlareSolverr with a bot check (Cloudflare) too", new SiteChallengeException(url.Host));
                        }

                        if (status is < 200 or >= 300)
                        {
                            throw _error($"{url.Host} answered {status} for {url.AbsolutePath} (through FlareSolverr)", null);
                        }

                        return page;
                    }
                    finally
                    {
                        _solverLock.Release();
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw _error($"{url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} for {url.AbsolutePath}", null);
                }

                return html;
            }
        }

        /// <summary>
        /// Posts a form as the site's scripts do and gets the answer.
        /// </summary>
        public async Task<string> PostAsync(Uri url, Uri referer, HttpContent content, Dictionary<string, string>? cookies, CancellationToken cancellationToken, bool ajax = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            AddBrowserHeaders(request, referer, null);
            request.Headers.TryAddWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority));
            if (ajax)
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            }

            AddCookies(request, cookies, null);

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw _error($"{url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} for {url.AbsolutePath}", null);
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches a stream's playlist, segment or file, or a subtitle, with the site's
        /// <c>Referer</c> and <c>Origin</c>. The caller disposes the response.
        /// </summary>
        public async Task<HttpResponseMessage> FetchMediaAsync(Uri url, Uri site, string? range, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _clearances.TryGetValue(url.Host, out var clearance);
            AddBrowserHeaders(request, site, clearance?.UserAgent);
            AddCookies(request, null, clearance?.Cookies);
            request.Headers.TryAddWithoutValidation("Origin", site.GetLeftPart(UriPartial.Authority));
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
                throw _error($"Could not reach {url.Host}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Whether two addresses are on the same site; "www." does not count.
        /// </summary>
        public static bool SameHost(Uri a, Uri b) =>
            string.Equals(WithoutWww(a.Host), WithoutWww(b.Host), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

        /// <summary>
        /// Gets a page's address relative to the site, as stream links carry it.
        /// </summary>
        public static string RelativePath(Uri site, string pageUrl)
        {
            var url = new Uri(pageUrl);
            if (url.AbsoluteUri.StartsWith(site.AbsoluteUri, StringComparison.Ordinal))
            {
                return url.AbsoluteUri[site.AbsoluteUri.Length..];
            }

            // Elsewhere on the site's host: from its root
            return SameHost(url, site) ? url.PathAndQuery : url.AbsoluteUri;
        }

        /// <summary>
        /// Reads a JSON file the plugin wrote; null if it is missing or unreadable.
        /// </summary>
        public static T? LoadJson<T>(string? file, ILogger logger, string site)
            where T : class
        {
            if (file is null || !File.Exists(file))
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(file);
                return JsonSerializer.Deserialize<T>(stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                logger.LogWarning("{Site}: could not read the saved catalog {File}: {Error}", site, file, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Writes a JSON file, never leaving half of one.
        /// </summary>
        public static void SaveJson<T>(string? file, T value, ILogger logger, string site)
        {
            if (file is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, value);
                }

                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{Site}: could not save the catalog to {File}: {Error}", site, file, ex.Message);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw _error($"{request.RequestUri?.Host} did not answer within {RequestTimeout.TotalSeconds:0} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                throw _error($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Whether an answer is Cloudflare's bot check rather than the page.
        /// </summary>
        internal static bool IsChallenge(HttpResponseMessage response, string html) =>
            (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge", StringComparer.OrdinalIgnoreCase))
            || (!response.IsSuccessStatusCode && HentaiHavenPage.IsChallenge(html));

        private Uri? FlareSolverrUri()
        {
            var configured = _flareSolverrUrl()?.Trim();
            return string.IsNullOrEmpty(configured) || !Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var url) ? null : url;
        }

        /// <summary>
        /// Has FlareSolverr open a page and pass the site's check, and keeps its cookies for
        /// the requests that follow.
        /// </summary>
        private async Task<(int Status, string Html)> SolveAsync(Uri solver, Uri url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(solver, "v1"))
            {
                Content = JsonContent.Create(new { cmd = "request.get", url = url.AbsoluteUri, maxTimeout = (int)SolverTimeout.TotalMilliseconds }),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SolverTimeout + RequestTimeout);
            string body;
            try
            {
                using var response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, timeout.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw _error($"FlareSolverr did not get {url.AbsoluteUri} within {(SolverTimeout + RequestTimeout).TotalSeconds:0} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                throw _error($"Could not reach FlareSolverr at {solver}: {ex.Message}", ex);
            }

            JsonObject? answer;
            try
            {
                answer = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                answer = null;
            }

            if (answer?["solution"] is not JsonObject solution || !string.Equals((string?)answer["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                var message = answer?["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : body.Length > 200 ? body[..200] : body;
                throw _error($"FlareSolverr could not get {url.AbsoluteUri}: {message}", null);
            }

            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cookie in (solution["cookies"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (cookie["name"] is JsonValue name && name.TryGetValue<string>(out var n) && cookie["value"] is JsonValue value && value.TryGetValue<string>(out var v))
                {
                    cookies[n] = v;
                }
            }

            var userAgent = solution["userAgent"] is JsonValue ua && ua.TryGetValue<string>(out var agent) && agent.Length > 0 ? agent : UserAgent;
            _clearances[url.Host] = new Clearance(cookies, userAgent);
            var status = solution["status"] is JsonValue s && s.TryGetValue<int>(out var code) ? code : 200;
            var html = solution["response"] is JsonValue r && r.TryGetValue<string>(out var page) ? page : string.Empty;
            return (status, html);
        }

        /// <summary>
        /// Waits until the interval since the last page's request has passed.
        /// </summary>
        private async Task PaceAsync(CancellationToken cancellationToken)
        {
            if (_requestInterval <= TimeSpan.Zero)
            {
                return;
            }

            await _paceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _nextRequest - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                _nextRequest = _clock.Elapsed + _requestInterval;
            }
            finally
            {
                _paceLock.Release();
            }
        }

        private static void AddBrowserHeaders(HttpRequestMessage request, Uri? referer, string? userAgent)
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (referer is not null)
            {
                request.Headers.Referrer = referer;
            }
        }

        private static void AddCookies(HttpRequestMessage request, Dictionary<string, string>? cookies, IReadOnlyDictionary<string, string>? clearance)
        {
            var all = (clearance ?? new Dictionary<string, string>()).Concat(cookies ?? new Dictionary<string, string>())
                .GroupBy(c => c.Key, StringComparer.Ordinal).Select(g => g.Last()).ToList();
            if (all.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", all.Select(c => c.Key + "=" + c.Value)));
            }
        }

        private sealed record Clearance(IReadOnlyDictionary<string, string> Cookies, string UserAgent);

        private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}
