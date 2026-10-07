using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HanimeClientTests
    {
        private readonly PluginConfiguration _config = new();
        private readonly FakeHandler _handler = new();
        private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

        private HanimeClient Create()
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler, disposeHandler: false));
            return new HanimeClient(factory.Object, () => _config, NullLogger.Instance, _time);
        }

        private static string Catalog(params string[] slugs) =>
            new JsonArray(slugs.Select(s => (JsonNode)new JsonObject { ["slug"] = s, ["name"] = s }).ToArray()).ToJsonString();

        [Fact]
        public void ParseCatalog_ReadsArraysAndHits()
        {
            using var array = JsonDocument.Parse("""[{ "slug": "a", "name": "A" }, { "slug": "a", "name": "Duplicate" }, { "name": "no slug" }]""");
            using var hits = JsonDocument.Parse(JsonSerializer.Serialize(new { page = 0, hits = """[{ "slug": "b", "name": "B" }]""" }));

            Assert.Equal(new[] { "A" }, HanimeClient.ParseCatalog(array.RootElement).Select(v => v.Name));
            Assert.Equal(new[] { "b" }, HanimeClient.ParseCatalog(hits.RootElement).Select(v => v.Slug));
        }

        [Fact]
        public void ParseStreams_ResolvesAndSortsGuestStreams()
        {
            var payload = JsonNode.Parse("""
                { "sources": [
                    { "kind": "normal", "src": "/hls/v-360.m3u8", "label": "360p" },
                    { "kind": "normal", "url": "https://cdn.example/v-720.m3u8", "height": 720 },
                    { "kind": "premium", "src": "/hls/v-1080.m3u8", "height": "1080" },
                    { "kind": "normal", "src": "" },
                    { "kind": "normal", "src": "javascript:alert(1)" }
                ] }
                """)!;

            var guest = HanimeClient.ParseStreams(payload, "https://hanime.tv", includePremium: false);
            var member = HanimeClient.ParseStreams(payload, "https://hanime.tv/", includePremium: true);

            Assert.Equal(new[] { "https://cdn.example/v-720.m3u8", "https://hanime.tv/hls/v-360.m3u8" }, guest.Select(s => s.Url));
            Assert.Equal(new[] { 720, 360 }, guest.Select(s => s.Height));
            Assert.Equal(new[] { 1080, 720, 360 }, member.Select(s => s.Height));
            Assert.True(member[0].Premium);
        }

        [Fact]
        public async Task GetStreams_SendsASignedHandshake()
        {
            HttpRequestMessage? sent = null;
            string? body = null;
            _handler.Respond = async request =>
            {
                sent = request;
                body = await request.Content!.ReadAsStringAsync();
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.Add("X-Token", HanimeCrypto.Seal(JsonNode.Parse("""{ "sources": [{ "kind": "normal", "src": "/hls/x.m3u8", "label": "720p" }] }""")!));
                return response;
            };

            var streams = await Create().GetStreamsAsync("some-video-1", CancellationToken.None);

            Assert.Equal("https://hanime.tv/hls/x.m3u8", Assert.Single(streams).Url);
            Assert.Equal(HttpMethod.Post, sent!.Method);
            Assert.Equal(PluginConfiguration.DefaultHandshakeUrl, sent.RequestUri!.ToString());
            var time = _time.GetUtcNow().ToUnixTimeSeconds();
            Assert.Equal(HanimeCrypto.WebSignature(time), sent.Headers.GetValues("X-Signature").Single());
            Assert.Equal("web2", sent.Headers.GetValues("X-Signature-Version").Single());
            Assert.Equal(time.ToString(System.Globalization.CultureInfo.InvariantCulture), sent.Headers.GetValues("X-Time").Single());
            Assert.Equal("https://hanime.tv/", sent.Headers.Referrer!.ToString());
            Assert.False(sent.Headers.Contains("X-Session-Token"));
            var token = HanimeCrypto.Open(JsonNode.Parse(body!)!["token"]!.GetValue<string>());
            Assert.Equal("htv_player_handshake", token["directive"]!.GetValue<string>());
            Assert.Equal("some-video-1", token["slug"]!.GetValue<string>());
            Assert.Equal(time, token["timestamp_unix"]!.GetValue<long>());
        }

        [Fact]
        public async Task GetStreams_ReportsRefusals()
        {
            _handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("blocked") });

            var error = await Assert.ThrowsAsync<HanimeException>(() => Create().GetStreamsAsync("v", CancellationToken.None));

            Assert.Contains("403", error.Message);
            Assert.Contains("relay", error.Message);
        }

        [Fact]
        public async Task GetStreams_LogsInWithAnAccount()
        {
            _config.Email = "user@example.com";
            _config.Password = "secret";
            var requests = new List<(HttpRequestMessage Request, string Body)>();
            _handler.Respond = async request =>
            {
                requests.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync()));
                if (request.RequestUri!.ToString() == PluginConfiguration.DefaultLoginUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{ "session_token": "tok", "session_token_expire_time_unix": 1893456000, "user": { "is_able_to_access_premium": true } }"""),
                    };
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.Add("X-Token", HanimeCrypto.Seal(JsonNode.Parse("""{ "sources": [{ "kind": "premium", "src": "/hls/x-1080.m3u8", "height": 1080 }, { "kind": "normal", "src": "/hls/x.m3u8", "height": 720 }] }""")!));
                return response;
            };
            var client = Create();

            var first = await client.GetStreamsAsync("v", CancellationToken.None);
            await client.GetStreamsAsync("v", CancellationToken.None);

            Assert.Equal(new[] { 1080, 720 }, first.Select(s => s.Height));
            // One login for both
            Assert.Equal(3, requests.Count);
            Assert.Equal("app2", requests[0].Request.Headers.GetValues("X-Signature-Version").Single());
            Assert.Equal("user@example.com", JsonNode.Parse(requests[0].Body)!["email"]!.GetValue<string>());
            Assert.Equal("tok", requests[1].Request.Headers.GetValues("X-Session-Token").Single());
            Assert.Equal("Logged in, premium", client.AccountStatus);
        }

        [Fact]
        public async Task GetStreams_PlaysAsGuestWhenTheLoginFails()
        {
            _config.Email = "user@example.com";
            _config.Password = "wrong";
            var logins = 0;
            _handler.Respond = request =>
            {
                if (request.RequestUri!.ToString() == PluginConfiguration.DefaultLoginUrl)
                {
                    logins++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{ "errors": ["bad"] }""") });
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.Add("X-Token", HanimeCrypto.Seal(JsonNode.Parse("""{ "sources": [{ "kind": "normal", "src": "/hls/x.m3u8", "height": 720 }] }""")!));
                return Task.FromResult(response);
            };
            var client = Create();

            await client.GetStreamsAsync("v", CancellationToken.None);
            await client.GetStreamsAsync("v", CancellationToken.None);

            // Not retried right away, so as not to lock the account
            Assert.Equal(1, logins);
            Assert.StartsWith("Login failed", client.AccountStatus);
        }

        [Fact]
        public async Task GetCatalog_IsCachedAndKeptWhenDownloadsFail()
        {
            var downloads = 0;
            var fail = false;
            _handler.Respond = request =>
            {
                downloads++;
                Assert.Equal("app2", request.Headers.GetValues("X-Signature-Version").Single());
                return Task.FromResult(fail
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("down") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog("a", "b")) });
            };
            var client = Create();

            Assert.Equal(2, (await client.GetCatalogAsync(CancellationToken.None)).Count);
            Assert.Equal(2, (await client.GetCatalogAsync(CancellationToken.None)).Count);
            Assert.Equal(1, downloads);

            _time.Advance(TimeSpan.FromHours(7));
            fail = true;
            Assert.Equal(2, (await client.GetCatalogAsync(CancellationToken.None)).Count);
            Assert.Equal(2, downloads);
            Assert.Contains("502", client.CatalogError);

            _time.Advance(TimeSpan.FromHours(7));
            fail = false;
            _handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") });
            Assert.Equal(2, (await client.GetCatalogAsync(CancellationToken.None)).Count);
        }

        [Fact]
        public async Task GetCatalog_ThrowsWithoutAnyCatalog()
        {
            _handler.Respond = _ => throw new HttpRequestException("no route");

            var error = await Assert.ThrowsAsync<HanimeException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("no route", error.Message);
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);
        }

        private sealed class FakeTime : TimeProvider
        {
            private DateTimeOffset _now;

            public FakeTime(DateTimeOffset now) => _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
