using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Hentai
{
    /// <summary>
    /// Reads a site's pages as a browser does: its headers, cookies, a retry when the site asks
    /// for a pause, and a clear error for bot checks. Shared by the sites read from their pages.
    /// </summary>
    internal sealed class SiteHttp
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Func<string, Exception?, Exception> _error;

        /// <param name="error">Makes the site's exception from a message and its cause.</param>
        public SiteHttp(IHttpClientFactory httpClientFactory, Func<string, Exception?, Exception> error)
        {
            _httpClientFactory = httpClientFactory;
            _error = error;
        }

        /// <summary>
        /// Gets a page's HTML.
        /// </summary>
        /// <param name="cookies">Cookies to send, and to add those the site sets to; null for none.</param>
        /// <param name="notFoundIsEmpty">Whether a missing page is empty rather than an error.</param>
        public async Task<string> GetPageAsync(Uri url, Uri? referer, Dictionary<string, string>? cookies, CancellationToken cancellationToken, bool notFoundIsEmpty = false)
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
                    throw _error(url.Host + " answered with a bot check (Cloudflare) instead of the page; it does not let this server in", null);
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
            AddBrowserHeaders(request, referer);
            request.Headers.TryAddWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority));
            if (ajax)
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            }

            if (cookies is not null)
            {
                AddCookies(request, cookies);
            }

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
            AddBrowserHeaders(request, site);
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

        private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}
