using System.Net;
using System.Text;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HentaiHavenTests : IDisposable
    {
        private const string Site = "https://haven.test/";

        private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        private readonly HentaiSettings _config = new() { HentaiHavenUrl = "https://haven.test" };
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly Dictionary<string, string> _bodies = new();
        private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), "hh-test-" + Guid.NewGuid().ToString("N") + ".json");
        private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

        public void Dispose() => File.Delete(_cacheFile);

        // Madara's search results, as Hentai Haven lists its series
        internal static string Listing(params (string Slug, string Title, int[] Episodes)[] series) =>
            "<html><body><div class=\"c-tabs-item\">"
            + string.Concat(series.Select(s =>
                $"""
                <div class="row c-tabs-item__content">
                  <div class="col-4"><div class="tab-thumb c-image-hover"><a href="{Site}watch/{s.Slug}/" title="{s.Title}"><img data-src="{Site}covers/{s.Slug}.jpg" src="data:image/gif;base64,AA"></a></div></div>
                  <div class="col-8"><div class="tab-summary">
                    <div class="post-title"><h3 class="h4"><a href="{Site}watch/{s.Slug}/">{s.Title}</a></h3></div>
                    <div class="post-content"><div class="post-content_item mg_genres"><div class="summary-heading"><h5>Genres</h5></div><div class="summary-content"><a href="{Site}genre/x/">X</a></div></div></div>
                  </div>
                  <div class="tab-meta"><div class="meta-item latest-chap"><span class="font-meta chapter">{string.Concat(s.Episodes.Select(e => $"<a href=\"{Site}watch/{s.Slug}/episode-{e}/\">Episode {e}</a>"))}</span></div></div></div>
                </div>
                """))
            + "</div></body></html>";

        internal static string SeriesPage(string slug, string title, params int[] episodes) =>
            $"""
            <html><head>
              <meta property="og:title" content="{title} - Hentai Haven">
              <meta property="og:image" content="{Site}og/{slug}.jpg">
            </head><body class="postid-42">
            <div class="post-title"><h1><span class="manga-title-badges hot">HOT</span> {title} </h1></div>
            <div class="summary_image"><a href="{Site}watch/{slug}/"><img class="img-responsive" data-src="{Site}covers/{slug}-poster.jpg" src="data:image/gif;base64,AA" alt="{title}"></a></div>
            <div class="post-content">
              <div class="post-content_item"><div class="summary-heading"><h5>Release</h5></div><div class="summary-content">2021</div></div>
              <div class="post-content_item"><div class="summary-heading"><h5>Studio</h5></div><div class="summary-content"><div class="author-content"><a href="{Site}studio/pink/" rel="tag">Pink Pineapple</a></div></div></div>
              <div class="post-content_item"><div class="summary-heading"><h5>Genre(s)</h5></div><div class="summary-content"><div class="genres-content"><a href="{Site}genre/a/">Big Boobs</a>, <a href="{Site}genre/b/">Vanilla</a></div></div></div>
            </div>
            <div class="description-summary"><div class="summary__content show-more"><p>Synopsis: First &amp; foremost.</p><p>Second paragraph.</p></div></div>
            <div class="page-content-listing single-page"><div class="listing-chapters_wrap"><ul class="main version-chap">
            {string.Concat(episodes.Reverse().Select(e => $"""
              <li class="wp-manga-chapter has-thumb"><a href="{Site}watch/{slug}/episode-{e}/"><img data-src="{Site}thumbs/{slug}-{e}.jpg"> Episode {e} </a><span class="chapter-release-date"><i>April {e}, 2021</i></span></li>
            """))}
            </ul></div></div>
            </body></html>
            """;

        internal static string EpisodePage(string slug, int episode) =>
            $"""<html><body><div class="player_logic_item"><iframe src="{Site}wp-content/plugins/player-logic/player.php?data={slug}-{episode}" allowfullscreen></iframe></div></body></html>""";

        internal const string PlayerPage = """
            <html><body><script type="text/javascript">
            var en = 'ENCRYPTED+/=';
            var iv = 'IV==';
            </script></body></html>
            """;

        internal static string ApiAnswer(string slug, int episode) =>
            $$$"""{"status":true,"data":{"sources":[{"src":"https://cdn.test/{{{slug}}}/{{{episode}}}/480/index.m3u8","type":"application/x-mpegURL","label":"480p"},{"src":"https://cdn.test/{{{slug}}}/{{{episode}}}/1080/index.m3u8","type":"application/x-mpegURL","label":"1080p"}]}}""";

        private HentaiHavenClient Create(string? cacheFile = null)
        {
            var handler = new Handler(this);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            return new HentaiHavenClient(factory.Object, () => _config, cacheFile, NullLogger.Instance, new FixedTime());
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (_override(request) is { } overridden)
            {
                return overridden;
            }

            var url = request.RequestUri!.AbsoluteUri;
            string? body = url switch
            {
                Site + "?s=&post_type=wp-manga&m_orderby=latest" => Listing(("first-show", "First Show", [2, 1]), ("second-show", "Second Show", [1])),
                Site + "watch/first-show/" => SeriesPage("first-show", "First Show", 1, 2),
                Site + "watch/second-show/" => SeriesPage("second-show", "Second Show", 1),
                Site + "watch/first-show/episode-2/" => EpisodePage("first-show", 2),
                Site + "wp-content/plugins/player-logic/player.php?data=first-show-2" => PlayerPage,
                Site + "wp-content/plugins/player-logic/api.php" => ApiAnswer("first-show", 2),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>Not found</html>") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        }

        [Fact]
        public void Listing_ReadsTheSeriesAndTheirListedEpisodes()
        {
            var listing = HentaiHavenPage.Listing(Listing(("a", "A &amp; B", [3, 2]), ("b", "Other", [1])), new Uri(Site));

            Assert.Equal(2, listing.Count);
            Assert.Equal(Site + "watch/a/", listing[0].Url);
            Assert.Equal("A & B", listing[0].Title);
            Assert.Equal(new[] { Site + "watch/a/episode-3/", Site + "watch/a/episode-2/" }, listing[0].EpisodeUrls);
            Assert.Equal(new[] { Site + "watch/b/episode-1/" }, listing[1].EpisodeUrls);
        }

        [Fact]
        public void Series_ReadsTheMetadataAndTheEpisodesOldestFirst()
        {
            var series = HentaiHavenPage.Series(SeriesPage("show", "The Show", 1, 2, 3), new Uri(Site + "watch/show/"), Now);

            Assert.Equal("The Show", series.Title);
            Assert.Equal("First & foremost.\n\nSecond paragraph.", series.Description);
            Assert.Equal(Site + "covers/show-poster.jpg", series.PosterUrl);
            Assert.Equal(new[] { "Big Boobs", "Vanilla" }, series.Genres);
            Assert.Equal("Pink Pineapple", series.Studio);
            Assert.Equal(2021, series.Year);
            Assert.Equal(new[] { 1, 2, 3 }, series.Episodes.Select(e => e.Number));
            Assert.Equal(Site + "watch/show/episode-1/", series.Episodes[0].Url);
            Assert.Equal(new DateTime(2021, 4, 2, 0, 0, 0, DateTimeKind.Utc), series.Episodes[1].ReleasedAt);
            Assert.Equal(Site + "thumbs/show-3.jpg", series.Episodes[2].ThumbnailUrl);
        }

        [Fact]
        public void Series_FallsBackToTheMetaTags()
        {
            const string Html = """
                <html><head><meta property="og:title" content="Bare Show &#8211; Hentai Haven"><meta property="og:image" content="https://haven.test/og.jpg">
                <meta name="description" content="About it."></head><body>
                <a href="https://haven.test/watch/bare/episode-2/">Ep 2</a><a href="https://haven.test/watch/bare/episode-1/">Ep 1</a>
                <a href="https://haven.test/watch/bare/#comments">Comments</a></body></html>
                """;

            var series = HentaiHavenPage.Series(Html, new Uri(Site + "watch/bare/"), Now);

            Assert.Equal("Bare Show", series.Title);
            Assert.Equal("About it.", series.Description);
            Assert.Equal("https://haven.test/og.jpg", series.PosterUrl);
            Assert.Equal(new[] { 1, 2 }, series.Episodes.Select(e => e.Number));
        }

        [Fact]
        public void Player_KeysAndSourcesAreRead()
        {
            var frame = HentaiHavenPage.PlayerFrame(EpisodePage("a", 1), new Uri(Site + "watch/a/episode-1/"));
            var sources = HentaiHavenPage.ApiSources(ApiAnswer("a", 1), new Uri(Site));

            Assert.Equal(Site + "wp-content/plugins/player-logic/player.php?data=a-1", frame?.AbsoluteUri);
            Assert.Equal(("ENCRYPTED+/=", "IV=="), HentaiHavenPage.PlayerKeys(PlayerPage));
            Assert.Equal(new[] { "1080p", "480p" }, sources.Select(s => s.Label));
            Assert.True(sources[0].IsHls);
            Assert.Empty(HentaiHavenPage.ApiSources("""{"status":false,"data":"error"}""", new Uri(Site)));
            Assert.Empty(HentaiHavenPage.ApiSources("<html>", new Uri(Site)));
        }

        [Fact]
        public void DirectStreams_FindsAddressesInScripts()
        {
            const string Html = """<script>player.setup({file:"https:\/\/cdn.test\/v\/720p.mp4"}); var hls = 'https://cdn.test/v/master.m3u8?t=1';</script>""";

            var streams = HentaiHavenPage.DirectStreams(Html, new Uri(Site));

            Assert.Equal(new[] { "https://cdn.test/v/master.m3u8?t=1", "https://cdn.test/v/720p.mp4" }, streams.Select(s => s.Url));
        }

        [Theory]
        [InlineData("April 3, 2021", "2021-04-03T00:00:00")]
        [InlineData("2021-04-03", "2021-04-03T00:00:00")]
        [InlineData("3 days ago", "2026-10-04T12:00:00")]
        [InlineData("1 hour ago", "2026-10-07T11:00:00")]
        public void ParseDate_ReadsDatesAndAges(string text, string expected) =>
            Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), HentaiHavenPage.ParseDate(text, Now));

        [Theory]
        [InlineData("Episode 3", "https://x/watch/a/episode-3/", 3)]
        [InlineData("Ep. 12", "https://x/watch/a/12/", 12)]
        [InlineData("OVA", "https://x/watch/a/episode-4/", 4)]
        [InlineData("Part 2", "https://x/watch/a/part/", 2)]
        [InlineData("Special", "https://x/watch/a/special/", null)]
        public void EpisodeNumber_ComesFromTheNameOrAddress(string text, string url, int? expected) =>
            Assert.Equal(expected, HentaiHavenPage.EpisodeNumber(text, url));

        [Fact]
        public void Describe_TellsWhatThePageIs()
        {
            const string Html = "<html><head><title>Other Site &amp; Co</title><meta name=\"generator\" content=\"Next.js\"></head>"
                + "<body><a href=\"/hentai/some-show\">A</a><a href=\"https://haven.test/hentai/some-show/1\">B</a><a href=\"https://elsewhere.test/x\">C</a></body></html>";

            Assert.Equal(
                Html.Length + " characters, titled \"Other Site & Co\", made with Next.js, with links such as /hentai/some-show /hentai/some-show/1",
                HentaiHavenPage.Describe(Html, new Uri(Site)));
        }

        [Fact]
        public void IsChallenge_RecognizesCloudflare()
        {
            Assert.True(HentaiHavenPage.IsChallenge("<html><head><title>Just a moment...</title>"));
            Assert.False(HentaiHavenPage.IsChallenge(SeriesPage("a", "A", 1)));
        }

        [Fact]
        public async Task GetCatalog_ReadsTheListAndEverySeries()
        {
            var catalog = await Create().GetCatalogAsync(CancellationToken.None);

            Assert.Equal(new[] { "First Show 1", "First Show 2", "Second Show 1" }, catalog.Select(v => v.Name));
            var first = catalog[0];
            Assert.Equal(HentaiSource.HentaiHaven, first.Source);
            Assert.Equal("watch/first-show/episode-1/", first.Id);
            Assert.Equal(("First Show", 1), first.SeriesInfo());
            Assert.Equal("Pink Pineapple", first.Brand);
            Assert.Equal(Site + "covers/first-show-poster.jpg", first.PosterUrl);
            Assert.Equal(Site + "thumbs/first-show-1.jpg", first.ThumbnailUrl);
            Assert.Equal(Site + "watch/first-show/episode-1/", first.PageUrl);
            // Page 2 answered 404: the end of the list
            Assert.Contains(_requests, r => r.RequestUri!.AbsoluteUri == Site + "page/2/?s=&post_type=wp-manga&m_orderby=latest");
        }

        [Fact]
        public async Task GetCatalog_ReadsOnlyChangedSeriesAgain()
        {
            _config.CatalogCacheHours = 1;
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _requests.Clear();

            // A new client: the series come from the file
            _override = r => r.RequestUri!.AbsoluteUri == Site + "?s=&post_type=wp-manga&m_orderby=latest"
                ? Ok(Listing(("first-show", "First Show", [3, 2]), ("second-show", "Second Show", [1])))
                : r.RequestUri.AbsoluteUri == Site + "watch/first-show/" ? Ok(SeriesPage("first-show", "First Show", 1, 2, 3)) : null;
            var catalog = await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Equal(4, catalog.Count);
            Assert.Contains(_requests, r => r.RequestUri!.AbsolutePath == "/watch/first-show/");
            Assert.DoesNotContain(_requests, r => r.RequestUri!.AbsolutePath == "/watch/second-show/");
        }

        [Fact]
        public async Task GetCatalog_UsesTheSavedCatalogWhenTheSiteFails()
        {
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _override = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") };

            var client = Create(_cacheFile);
            var catalog = await client.GetCatalogAsync(CancellationToken.None);

            Assert.Equal(3, catalog.Count);
            Assert.Contains("503", client.CatalogError);
        }

        [Fact]
        public async Task GetCatalog_ReportsBotChecks()
        {
            _override = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title><script src=\"/cdn-cgi/challenge-platform/x\"></script>") };

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("bot check", error.Message);
        }

        [Fact]
        public async Task GetStreams_AsksThePlayersApi()
        {
            var streams = await Create().GetStreamsAsync("watch/first-show/episode-2/", CancellationToken.None);

            Assert.Equal("https://cdn.test/first-show/2/1080/index.m3u8", streams[0].Url);
            var api = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
            Assert.Equal(Site + "wp-content/plugins/player-logic/api.php", api.RequestUri!.AbsoluteUri);
            var form = _bodies[api.RequestUri.AbsoluteUri];
            Assert.Contains("zarat_get_data_player_ajax", form);
            Assert.Contains("ENCRYPTED+/=", form);
            Assert.Contains("IV==", form);
            Assert.Equal(Site + "wp-content/plugins/player-logic/player.php?data=first-show-2", api.Headers.Referrer?.AbsoluteUri);
        }

        [Theory]
        [InlineData("https://haven.test/", "https://haven.test/watch/a/episode-1/", "watch/a/episode-1/")]
        [InlineData("https://haven.test/sub/", "https://haven.test/sub/watch/a/episode-1/", "watch/a/episode-1/")]
        [InlineData("https://haven.test/sub/", "https://www.haven.test/watch/a/?p=1", "/watch/a/?p=1")]
        [InlineData("https://haven.test/", "https://cdn.test/watch/a/", "https://cdn.test/watch/a/")]
        public void RelativePath_ResolvesBackToTheEpisode(string site, string episode, string expected)
        {
            var path = HentaiHavenClient.RelativePath(new Uri(site), episode);

            Assert.Equal(expected, path);
            Assert.Equal(new Uri(new Uri(site), path).PathAndQuery, new Uri(episode).PathAndQuery);
        }

        [Fact]
        public async Task GetStreams_OnlyReadsTheSitesPages()
        {
            await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetStreamsAsync("https://elsewhere.test/watch/a/", CancellationToken.None));
            await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetStreamsAsync("//elsewhere.test/watch/a/", CancellationToken.None));
        }

        [Fact]
        public async Task FetchMedia_SendsTheSitesHeadersAndRange()
        {
            _override = _ => Ok("segment");

            using var response = await Create().FetchMediaAsync(new Uri("https://cdn.test/a.ts"), "bytes=0-99", CancellationToken.None);

            var request = Assert.Single(_requests);
            Assert.Equal(Site, request.Headers.Referrer?.AbsoluteUri);
            Assert.Equal("https://haven.test", request.Headers.GetValues("Origin").Single());
            Assert.Equal("bytes=0-99", request.Headers.GetValues("Range").Single());
        }

        [Fact]
        public void Merge_PrefersTheFirstSourceForEpisodesBothHave()
        {
            var hanime = new[] { Hanime("show-1", "Ane no Show 1"), Hanime("show-2", "Ane no Show 2") };
            var haven = new[] { Haven("Ane no Show!", 2), Haven("Ane no Show!", 3), Haven("Only Haven", 1) };

            var merged = HentaiCatalog.Merge([hanime, haven]);

            Assert.Equal(new[] { "hanime:show-1", "hanime:show-2", "hentaihaven:Ane no Show!/3", "hentaihaven:Only Haven/1" }, merged.Select(v => v.Key));
        }

        [Fact]
        public void Merge_KeepsEpisodesOfOneSourceThatShareANumber()
        {
            var haven = new[] { Haven("Show", 1), Haven("Show", 1, "watch/show/episode-1-preview/") };

            Assert.Equal(2, HentaiCatalog.Merge([haven]).Count);
        }

        private static HentaiVideo Hanime(string slug, string name) => new() { Id = slug, Name = name };

        private static HentaiVideo Haven(string series, int episode, string? id = null) => new()
        {
            Source = HentaiSource.HentaiHaven,
            Id = id ?? series + "/" + episode,
            Name = series + " " + episode,
            SeriesName = series,
            EpisodeNumber = episode,
        };

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

        private sealed class Handler : HttpMessageHandler
        {
            private readonly HentaiHavenTests _test;

            public Handler(HentaiHavenTests test) => _test = test;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_test._requests)
                {
                    _test._requests.Add(request);
                }

                if (request.Content is not null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    lock (_test._requests)
                    {
                        _test._bodies[request.RequestUri!.AbsoluteUri] = body;
                    }
                }

                return _test.Respond(request);
            }
        }

        private sealed class FixedTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
