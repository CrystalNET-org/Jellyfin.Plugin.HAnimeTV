using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Hanime
{
    /// <summary>
    /// Reads the catalog and the streams from hanime.tv.
    /// </summary>
    public sealed partial class HanimeClient
    {
        /// <summary>
        /// Sent with every request, and by ffmpeg when it reads the streams.
        /// </summary>
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        public const string Referer = "https://hanime.tv/";

        private const string Origin = "https://hanime.tv";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Func<PluginConfiguration> _configuration;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _catalogLock = new(1, 1);
        private readonly SemaphoreSlim _sessionLock = new(1, 1);

        private Catalog? _catalog;
        private Session? _session;

        public HanimeClient(IHttpClientFactory httpClientFactory, Func<PluginConfiguration> configuration, ILogger logger, TimeProvider? time = null)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        /// <summary>
        /// Gets when the catalog was last downloaded, or null.
        /// </summary>
        public DateTimeOffset? CatalogTime => _catalog?.FetchedAt;

        /// <summary>
        /// Gets the number of videos in the catalog, or null before it was downloaded.
        /// </summary>
        public int? CatalogCount => _catalog?.Videos.Count;

        /// <summary>
        /// Gets why the last download of the catalog failed, or null if it succeeded.
        /// </summary>
        public string? CatalogError { get; private set; }

        /// <summary>
        /// Gets the account's state: null without an account, else whether the login worked
        /// and whether the account has premium.
        /// </summary>
        public string? AccountStatus { get; private set; }

        /// <summary>
        /// Gets the catalog. It is downloaded at most once per <see cref="PluginConfiguration.CatalogCacheHours"/>;
        /// if a download fails, the last catalog is used.
        /// </summary>
        /// <exception cref="HanimeException">No catalog could be downloaded.</exception>
        public async Task<IReadOnlyList<HanimeVideo>> GetCatalogAsync(CancellationToken cancellationToken)
        {
            var config = _configuration();
            var maxAge = TimeSpan.FromHours(Math.Max(1, config.CatalogCacheHours));
            if (_catalog is { } cached && cached.Source == config.SearchUrl && _time.GetUtcNow() - cached.FetchedAt < maxAge)
            {
                return cached.Videos;
            }

            // One download at a time: the channel's folders are often opened together
            await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_catalog is { } current && current.Source == config.SearchUrl && _time.GetUtcNow() - current.FetchedAt < maxAge)
                {
                    return current.Videos;
                }

                try
                {
                    var videos = await DownloadCatalogAsync(config.SearchUrl, cancellationToken).ConfigureAwait(false);
                    _catalog = new Catalog(videos, config.SearchUrl, _time.GetUtcNow());
                    CatalogError = null;
                    _logger.LogInformation("hanime.tv: downloaded the catalog, {Count} videos", videos.Count);
                    return videos;
                }
                catch (Exception ex) when (ex is HanimeException or HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    CatalogError = ex.Message;
                    if (_catalog is { } stale && stale.Source == config.SearchUrl)
                    {
                        _logger.LogWarning("hanime.tv: could not download the catalog, using the one from {Time}: {Error}", stale.FetchedAt, ex.Message);
                        return stale.Videos;
                    }

                    throw ex as HanimeException ?? new HanimeException("Could not download the catalog: " + ex.Message, ex);
                }
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        /// <summary>
        /// Downloads the catalog, bypassing the cache.
        /// </summary>
        internal async Task<IReadOnlyList<HanimeVideo>> DownloadCatalogAsync(string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAppHeaders(request, sessionToken: string.Empty);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "catalog", cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return ParseCatalog(document.RootElement);
            }
        }

        /// <summary>
        /// Reads the catalog: an array of videos, or an object whose "data" (current responses,
        /// next to "ads") or "hits" (older ones) is that array or a string containing it.
        /// </summary>
        internal static IReadOnlyList<HanimeVideo> ParseCatalog(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in (string[])["data", "hits"])
                {
                    if (root.TryGetProperty(name, out var list) && list.ValueKind is JsonValueKind.Array or JsonValueKind.String)
                    {
                        if (list.ValueKind == JsonValueKind.String)
                        {
                            using var inner = JsonDocument.Parse(list.GetString()!);
                            return ParseCatalog(inner.RootElement);
                        }

                        root = list;
                        break;
                    }
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                // Names the fields, so that a changed format can be told apart from an error page
                var fields = root.ValueKind == JsonValueKind.Object
                    ? "an object with " + string.Join(", ", root.EnumerateObject().Select(p => p.Name).Take(10))
                    : root.ValueKind.ToString().ToLowerInvariant();
                throw new HanimeException("The catalog has an unknown format: " + fields);
            }

            var videos = new List<HanimeVideo>();
            var slugs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateArray())
            {
                if (HanimeVideo.FromJson(item) is { } video && slugs.Add(video.Slug))
                {
                    videos.Add(video);
                }
            }

            return videos;
        }

        /// <summary>
        /// Gets the video's streams, best first.
        /// </summary>
        /// <exception cref="HanimeException">hanime.tv returned no streams.</exception>
        public async Task<IReadOnlyList<HanimeStream>> GetStreamsAsync(string slug, CancellationToken cancellationToken)
        {
            var config = _configuration();
            var sessionToken = await GetSessionTokenAsync(config, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var token = HanimeCrypto.Seal(new JsonObject
            {
                ["timestamp_unix"] = now,
                ["directive"] = "htv_player_handshake",
                ["slug"] = slug,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, config.HandshakeUrl)
            {
                Content = new StringContent(new JsonObject { ["token"] = token }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            AddBrowserHeaders(request);
            request.Headers.TryAddWithoutValidation("X-Signature-Version", "web2");
            request.Headers.TryAddWithoutValidation("X-Signature", HanimeCrypto.WebSignature(now));
            request.Headers.TryAddWithoutValidation("X-Time", now.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Csrf-Token", "null");
            if (!string.IsNullOrEmpty(sessionToken))
            {
                request.Headers.TryAddWithoutValidation("X-Session-Token", sessionToken);
            }

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "streams of " + slug, cancellationToken).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("X-Token", out var values) || values.FirstOrDefault() is not { Length: > 0 } xToken)
            {
                throw new HanimeException($"hanime.tv returned no streams for {slug} (no X-Token)");
            }

            JsonNode payload;
            try
            {
                payload = HanimeCrypto.Open(xToken);
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or JsonException or InvalidOperationException)
            {
                throw new HanimeException($"Could not read the streams of {slug}: {ex.Message}", ex);
            }

            var streams = ParseStreams(payload, config.StreamHost, includePremium: !string.IsNullOrEmpty(sessionToken));
            if (streams.Count == 0)
            {
                throw new HanimeException($"hanime.tv returned no playable streams for {slug}");
            }

            return streams;
        }

        /// <summary>
        /// Reads the handshake's sources: { "sources": [{ "kind", "src" or "url", "label" or "height" }] }.
        /// </summary>
        internal static IReadOnlyList<HanimeStream> ParseStreams(JsonNode payload, string streamHost, bool includePremium)
        {
            if (payload["sources"] is not JsonArray sources)
            {
                return Array.Empty<HanimeStream>();
            }

            var streams = new List<HanimeStream>();
            foreach (var source in sources.OfType<JsonObject>())
            {
                var path = Text(source["src"]) ?? Text(source["url"]);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var kind = Text(source["kind"]);
                var premium = kind is not null && !string.Equals(kind, "normal", StringComparison.OrdinalIgnoreCase);
                if (premium && !includePremium)
                {
                    continue;
                }

                if (!Uri.TryCreate(new Uri(streamHost.TrimEnd('/') + "/"), path, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                {
                    continue;
                }

                var height = Digits().Match(Text(source["height"]) ?? Text(source["label"]) ?? string.Empty) is { Success: true } match
                    && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
                streams.Add(new HanimeStream(url.AbsoluteUri, height, premium));
            }

            return streams
                .DistinctBy(s => s.Url)
                .OrderByDescending(s => s.Height)
                .ToList();
        }

        /// <summary>
        /// Gets the session token of the configured account, logging in if needed; empty
        /// without an account or if the login fails (the streams then are those of a guest).
        /// </summary>
        private async Task<string> GetSessionTokenAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.Email) || string.IsNullOrEmpty(config.Password))
            {
                AccountStatus = null;
                return string.Empty;
            }

            var credentials = config.Email.Trim() + "\n" + config.Password + "\n" + config.LoginUrl;
            if (_session is { } session && session.Credentials == credentials && session.ExpiresAt > _time.GetUtcNow().AddMinutes(5))
            {
                return session.Token;
            }

            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_session is { } current && current.Credentials == credentials && current.ExpiresAt > _time.GetUtcNow().AddMinutes(5))
                {
                    return current.Token;
                }

                // A failed login is not retried for a while, so as not to lock the account
                if (_session is { Token.Length: 0 } failed && failed.Credentials == credentials && failed.ExpiresAt > _time.GetUtcNow())
                {
                    return string.Empty;
                }

                try
                {
                    _session = await LoginAsync(config, credentials, cancellationToken).ConfigureAwait(false);
                    AccountStatus = _session.Premium ? "Logged in, premium" : "Logged in, no premium (up to 720p)";
                    _logger.LogInformation("hanime.tv: logged in as {Email} ({Status})", config.Email.Trim(), AccountStatus);
                }
                catch (Exception ex) when (ex is HanimeException or HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    _session = new Session(string.Empty, credentials, _time.GetUtcNow().AddMinutes(15), false);
                    AccountStatus = "Login failed: " + ex.Message;
                    _logger.LogWarning("hanime.tv: login failed, playing as a guest: {Error}", ex.Message);
                }

                return _session.Token;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task<Session> LoginAsync(PluginConfiguration config, string credentials, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.LoginUrl)
            {
                Content = new StringContent(
                    new JsonObject { ["email"] = config.Email.Trim(), ["password"] = config.Password }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            };
            AddAppHeaders(request, sessionToken: string.Empty);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "login", cancellationToken).ConfigureAwait(false);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var token = Text(body?["session_token"]);
            if (string.IsNullOrEmpty(token))
            {
                throw new HanimeException("The login returned no session");
            }

            var expires = long.TryParse(Text(body?["session_token_expire_time_unix"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : _time.GetUtcNow().AddHours(12);
            var premium = body?["user"]?["is_able_to_access_premium"] is JsonValue value && value.TryGetValue<bool>(out var isPremium) && isPremium;
            return new Session(token, credentials, expires, premium);
        }

        /// <summary>
        /// Forgets the session, e.g. after the account settings changed.
        /// </summary>
        public void ResetSession()
        {
            _session = null;
            AccountStatus = null;
        }

        private static void AddBrowserHeaders(HttpRequestMessage request)
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.Referrer = new Uri(Referer);
        }

        private void AddAppHeaders(HttpRequestMessage request, string sessionToken)
        {
            AddBrowserHeaders(request);
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation("X-Claim", now.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Signature-Version", "app2");
            request.Headers.TryAddWithoutValidation("X-Signature", HanimeCrypto.AppSignature(now));
            request.Headers.TryAddWithoutValidation("X-Session-Token", sessionToken);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            try
            {
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HanimeException($"{request.RequestUri?.Host} did not answer within {RequestTimeout.TotalSeconds:0} seconds", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new HanimeException($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 200)
            {
                body = body[..200];
            }

            var hint = response.StatusCode == HttpStatusCode.Forbidden
                ? " (hanime.tv blocks many datacenter and VPN addresses; a relay can be set under Advanced)"
                : string.Empty;
            throw new HanimeException($"hanime.tv refused the {what}: HTTP {(int)response.StatusCode}{hint} {body}".TrimEnd());
        }

        private static string? Text(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<double>(out var number) => ((long)number).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        private sealed record Catalog(IReadOnlyList<HanimeVideo> Videos, string Source, DateTimeOffset FetchedAt);

        private sealed record Session(string Token, string Credentials, DateTimeOffset ExpiresAt, bool Premium);
    }
}
