using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Pornhub;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class PornhubTests
    {
        private const string VideoJson = """
            {
              "duration": "12:34", "views": 1234, "video_id": "ph5f0b", "rating": "87.5", "ratings": 10,
              "title": "A &amp; B", "publish_date": "2024-03-02 10:20:30",
              "default_thumb": "https://ci.test/thumb.jpg",
              "tags": [{ "tag_name": "tag one" }, { "tag_name": "Tag One" }],
              "pornstars": [{ "pornstar_name": "Someone" }],
              "categories": [{ "category": "Amateur" }, { "category": "HD" }]
            }
            """;

        private readonly PornhubSettings _config = new() { ApiUrl = "https://api.test/webmasters", SiteUrl = "https://site.test" };
        private readonly List<HttpRequestMessage> _requests = new();
        private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        private PornhubClient Create()
        {
            var handler = new Handler(r =>
            {
                _requests.Add(r);
                return _respond(r);
            });
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            return new PornhubClient(factory.Object, () => _config, NullLogger.Instance);
        }

        private static HttpResponseMessage Ok(string body, string type = "application/json") =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

        [Fact]
        public void Video_IsReadFromTheApi()
        {
            using var document = JsonDocument.Parse(VideoJson);

            var video = PornhubVideo.FromJson(document.RootElement)!;

            Assert.Equal("ph5f0b", video.Id);
            Assert.Equal("A & B", video.Title);
            Assert.Equal(TimeSpan.FromSeconds(754), video.Duration);
            Assert.Equal(87.5, video.Rating);
            Assert.Equal(new DateTime(2024, 3, 2, 10, 20, 30, DateTimeKind.Utc), video.PublishedAt);
            Assert.Equal(new[] { "tag one" }, video.Tags);
            Assert.Equal(new[] { "Amateur", "HD" }, video.Categories);
            Assert.Equal(new[] { "Someone" }, video.Pornstars);
        }

        [Theory]
        [InlineData("1:02:03", 3723)]
        [InlineData("0:45", 45)]
        [InlineData("n/a", null)]
        public void Duration_IsRead(string text, int? seconds) =>
            Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, PornhubVideo.ParseDuration(text));

        [Fact]
        public void Streams_ComeFromTheFlashvars()
        {
            const string Html = """
                <script>var flashvars_123 = {"mediaDefinitions":[
                  {"videoUrl":"https://cv.test/hls/720P_4000K_1.mp4/master.m3u8","quality":"720","format":"hls"},
                  {"videoUrl":"https://cv.test/hls/1080P_8000K_1.mp4/master.m3u8","quality":["1080","720"],"format":"hls"},
                  {"videoUrl":"https://site.test/video/get_media?s=x","quality":[],"format":"mp4"}]};</script>
                """;

            var streams = PornhubPage.Streams(Html);

            Assert.Equal(new[] { 720, 720, 0 }, streams.Select(s => s.Height));
            Assert.True(streams[0].IsHls);
            Assert.True(streams[2].IsMediaList);
        }

        [Fact]
        public void Streams_AreAssembledFromScriptVariables()
        {
            const string Html = """<script>var media_x = "https://cv.test/"; var media_0 = media_x /* "https://decoy" + */ + "videos/480P_2000K_1.mp4?t=1";</script>""";

            Assert.Contains(("media_0", "https://cv.test/videos/480P_2000K_1.mp4?t=1"), PornhubPage.ScriptVariables(Html));
            var stream = Assert.Single(PornhubPage.Streams(Html), s => s.Url.EndsWith("t=1", StringComparison.Ordinal));
            Assert.Equal(480, stream.Height);
        }

        [Fact]
        public void Unavailable_ExplainsWhy()
        {
            Assert.Contains("country", PornhubPage.Unavailable("<div class=\"geoBlocked\">x</div>"));
            Assert.Equal("Pornhub says: This video has been removed", PornhubPage.Unavailable("<div class=\"removed\"> <span>This video</span> has been removed </div>"));
            Assert.Null(PornhubPage.Unavailable("<html>fine</html>"));
        }

        [Fact]
        public void Folders_MapToSearches()
        {
            Assert.Equal(new PornhubQuery("mostviewed", "weekly"), PornhubChannelSource.Query(PornhubChannelSource.MostViewedWeek));
            Assert.Equal(new PornhubQuery("mostviewed", "monthly", Category: "Amateur"), PornhubChannelSource.Query("category:Amateur"));
            Assert.Equal(new PornhubQuery(null, Search: "some one"), PornhubChannelSource.Query("search:some one"));
            Assert.Null(PornhubChannelSource.Query(PornhubChannelSource.Categories));

            Assert.DoesNotContain(PornhubChannelSource.Root(_config), f => f.Id == PornhubChannelSource.Searches);
            _config.Searches = ["some one"];
            Assert.Contains(PornhubChannelSource.Root(_config), f => f.Id == PornhubChannelSource.Searches);
        }

        [Fact]
        public void VideoItems_LeaveOutHiddenCategories()
        {
            using var document = JsonDocument.Parse(VideoJson);
            var video = PornhubVideo.FromJson(document.RootElement)!;
            _config.HiddenCategories = ["hd"];

            Assert.Empty(PornhubChannelSource.VideoItems("newest", [video], _config));
            _config.HiddenCategories = [];
            var item = Assert.Single(PornhubChannelSource.VideoItems("newest", [video], _config));
            Assert.Equal("newest|ph5f0b", item.Id);
            Assert.Equal("ph5f0b", PornhubChannelSource.ViewkeyOf(item.Id));
            Assert.Equal(8.8f, item.CommunityRating);
            Assert.Equal("With Someone.", item.Overview);
        }

        [Fact]
        public async Task Search_ReadsPagesUntilTheEnd()
        {
            _respond = r => r.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal)
                ? Ok("{\"videos\":[" + VideoJson + "]}")
                : Ok("""{"code":"2001","message":"No Videos found!"}""");

            var videos = await Create().SearchAsync(new PornhubQuery("newest"), 50, CancellationToken.None);

            Assert.Single(videos);
            Assert.Equal(2, _requests.Count);
            Assert.StartsWith("https://api.test/webmasters/search?search=&ordering=newest&page=1", _requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Streams_AreReadWithTheAgeCookiesAndHlsFirst()
        {
            _respond = r => r.RequestUri!.AbsolutePath == "/view_video.php"
                ? Ok("""<script>var flashvars_1 = {"mediaDefinitions":[{"videoUrl":"https://site.test/video/get_media?s=1","format":"mp4"},{"videoUrl":"https://cv.test/a/master.m3u8","quality":"480"}]};</script>""", "text/html")
                : Ok("""[{"videoUrl":"https://cv.test/a/1080.mp4","quality":"1080"},{"videoUrl":"https://cv.test/a/720.mp4","quality":"720"}]""");

            var streams = await Create().GetStreamsAsync("ph1", CancellationToken.None);

            Assert.Equal(new[] { "https://cv.test/a/master.m3u8", "https://cv.test/a/1080.mp4", "https://cv.test/a/720.mp4" }, streams.Select(s => s.Url));
            Assert.Contains("age_verified=1", _requests[0].Headers.GetValues("Cookie").Single(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Refusals_AreExplained()
        {
            _respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };

            var error = await Assert.ThrowsAsync<PornhubException>(() => Create().GetStreamsAsync("ph1", CancellationToken.None));

            Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
            Assert.Contains("refuses the server", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Media_IsFetchedWithOriginRefererAndRange()
        {
            _respond = _ => Ok("data", "video/mp4");

            using var response = await Create().FetchMediaAsync(new Uri("https://cv.test/a/720.mp4"), "bytes=10-", CancellationToken.None);

            var request = Assert.Single(_requests);
            Assert.Equal("https://site.test", request.Headers.GetValues("Origin").Single());
            Assert.Equal("https://site.test/", request.Headers.Referrer?.AbsoluteUri);
            Assert.Equal("bytes=10-", request.Headers.GetValues("Range").Single());
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = _respond(request);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }
    }
}
