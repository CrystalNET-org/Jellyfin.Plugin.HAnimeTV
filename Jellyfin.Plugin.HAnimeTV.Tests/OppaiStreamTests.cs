using System.Net;
using System.Text;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.Library;
using Jellyfin.Plugin.HAnimeTV.OppaiStream;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class OppaiStreamTests : IDisposable
    {
        private const string Site = "https://oppai.test/";

        private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        private readonly HentaiSettings _config = new() { OppaiStreamUrl = "https://oppai.test" };
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), "oppai-test-" + Guid.NewGuid().ToString("N") + ".json");
        private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

        public void Dispose() => File.Delete(_cacheFile);

        // The site's search (actions/search.php): one card per episode, newest first, the title
        // split into the series and the episode's number
        internal static string Search(params (string Name, int Episode)[] episodes) =>
            "<div style=\"position:absolute;opacity:0;\" id=\"amount-full\" amo=\"1883\"></div>"
            + string.Concat(episodes.Select(e =>
                $"""
                <div class="in-grid episode-shown" id="1-{e.Episode}" idgt="1" folder="{e.Name}" ep="{e.Episode}" tags="4k,Censored" name="{e.Name}" desc="About it">
                    <div class="in-main-gr" just-check="1"><a href="{Site}watch?e={e.Name} {e.Episode}&amp;f=x{e.Episode}">
                        <div class="cover-img">
                            <img class="aspect" src="{Site}assets/aspect.png">
                            <img class="cover-img-in" src="{Site}thumbs/{e.Name.Replace(' ', '-')}-{e.Episode}.jpg" id="cover-1" alt="thumbnail">
                            <div class="tags-video"><h6 class="fh-tag white">4k</h6></div>
                        </div>
                        <div class="wrap-ep-info">
                            <object><h6 class="gray extra-line">By <a href="{Site}search?studio=Nur" class="gray">Nur</a></h6></object><h5 class="white bold title-ep"><font class="title inline">{WebUtility.HtmlEncode(e.Name)}</font> <font class="ep inline">{e.Episode}</font></h5>
                        </div>
                    </a></div>
                </div>
                """));

        internal static string EpisodePage(string name, int episode, bool hls = false, bool subtitles = true) =>
            $$"""
            <html><body>
            <div class="episode-info">
              <h1>{{name}} Ep {{episode}}</h1>
              <h6>Studio: <a class="red" href="/search?studio=Pink">Pink Pineapple</a></h6>
            </div>
            <video id="episode" poster="{{Site}}posters/{{name.Replace(' ', '-')}}.jpg" controls>
              {{(subtitles ? $"<track kind=\"captions\" src=\"{Site}subs/{name.Replace(' ', '-')}-{episode}.vtt\" srclang=\"en\" label=\"English\">" : string.Empty)}}
            </video>
            <div class="description"><p>About {{name}} &amp; more. Watch {{name}} Episode {{episode}} on Oppai Stream</p></div>
            <div class="tags"><a href="/t/a">Big Boobs</a><a href="/t/b">Uncensored</a></div>
            <script>
              var availableres = {"720":"https:\/\/cdn.oppai.test\/{{episode}}\/720.{{(hls ? "m3u8" : "mp4")}}","4k":"https:\/\/cdn.oppai.test\/{{episode}}\/2160.{{(hls ? "m3u8" : "mp4")}}","1080":"https:\/\/cdn.oppai.test\/{{episode}}\/1080.{{(hls ? "m3u8" : "mp4")}}"};
            </script>
            </body></html>
            """;

        private OppaiStreamClient Create(string? cacheFile = null)
        {
            var handler = new Handler(this);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            return new OppaiStreamClient(factory.Object, () => _config, cacheFile, NullLogger.Instance, new FixedTime());
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
                Site + "actions/search.php?text=&order=uploaded&page=1&limit=36&genres=&blacklist=&studio=&ibt=0&swa=0" => Search(("Ane no Show", 2), ("Other Show", 1), ("Ane no Show", 1)),
                Site + "watch?e=Ane%20no%20Show%202&f=x2" => EpisodePage("Ane no Show", 2),
                Site + "watch?e=Ane%20no%20Show%201&f=x1" => EpisodePage("Ane no Show", 1, subtitles: false),
                Site + "watch?e=Other%20Show%201&f=x1" => EpisodePage("Other Show", 1, hls: true),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>Not found</html>") }
                : Ok(body);
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

        [Fact]
        public void Listing_ReadsTheCards()
        {
            var listing = OppaiStreamPage.Listing(Search(("A & B", 3), ("Other", 1)), new Uri(Site + "actions/search.php"));

            Assert.Equal(2, listing.Count);
            Assert.Equal(Site + "watch?e=A%20%26%20B%203&f=x3", listing[0].Url);
            Assert.Equal("A & B 3", listing[0].Title);
            Assert.Equal(Site + "thumbs/A-&-B-3.jpg", listing[0].ThumbnailUrl);
        }

        [Fact]
        public void Listing_ReadsTheSitesSingleQuotes()
        {
            // As the site writes its pages; a browser saves them with double quotes
            var html = Search(("A & B", 3), ("Other", 1)).Replace('"', '\'');

            var listing = OppaiStreamPage.Listing(html, new Uri(Site + "actions/search.php"));

            Assert.Equal(new[] { "A & B 3", "Other 1" }, listing.Select(l => l.Title));
            Assert.Equal(Site + "watch?e=A%20%26%20B%203&f=x3", listing[0].Url);
            Assert.Equal(Site + "thumbs/A-&-B-3.jpg", listing[0].ThumbnailUrl);
        }

        [Fact]
        public void Listing_TakesTheTitleFromTheCardWithoutHeading()
        {
            var html = "<div class=episodes><div class='in-grid episode-shown' name='Kokuhaku' ep=4><a href='/watch?e=Kokuhaku-4&for=search'><img class='cover-img-in' src='/t.png'></a></div></div>";

            var listing = Assert.Single(OppaiStreamPage.Listing(html, new Uri(Site + "actions/search.php")));

            Assert.Equal("Kokuhaku 4", listing.Title);
            Assert.Equal(Site + "watch?e=Kokuhaku-4&for=search", listing.Url);
        }

        [Fact]
        public void Listing_FallsBackToTheEpisodesLinks()
        {
            var html = "<ul><li><a href='https://oppai.test/watch?e=Kokuhaku-4&amp;for=search'><b>?</b></a></li><li><a href='/watch?e=Pure-X-Holic-2&for=search'>x</a></li><li><a href='/search?studio=Nur'>Nur</a></li></ul>";

            var listing = OppaiStreamPage.Listing(html, new Uri(Site + "actions/search.php"));

            Assert.Equal(new[] { "Kokuhaku 4", "Pure X Holic 2" }, listing.Select(l => l.Title));
            Assert.Equal(Site + "watch?e=Kokuhaku-4&for=search", listing[0].Url);
        }

        [Fact]
        public void Episode_ReadsTheSitesSingleQuotes()
        {
            var page = OppaiStreamPage.Episode(EpisodePage("Ane no Show", 2).Replace("class=\"", "class='").Replace("\" href", "' href").Replace("\">", "'>"), new Uri(Site + "watch?e=x"));

            Assert.Equal("Ane no Show Ep 2", page.Title);
            Assert.Equal("Pink Pineapple", page.Studio);
            Assert.Equal("About Ane no Show & more.", page.Description);
            Assert.Equal(new[] { "Big Boobs", "Uncensored" }, page.Genres);
        }

        [Fact]
        public void Episode_ReadsTheMetadataStreamsAndSubtitles()
        {
            var page = OppaiStreamPage.Episode(EpisodePage("Ane no Show", 2), new Uri(Site + "watch?e=x"));

            Assert.Equal("Ane no Show Ep 2", page.Title);
            Assert.Equal("About Ane no Show & more.", page.Description);
            Assert.Equal(new[] { "Big Boobs", "Uncensored" }, page.Genres);
            Assert.Equal("Pink Pineapple", page.Studio);
            Assert.Equal(Site + "posters/Ane-no-Show.jpg", page.PosterUrl);
            Assert.Equal(new[] { "2160p", "1080p", "720p" }, page.Streams.Select(s => s.Label));
            Assert.Equal("https://cdn.oppai.test/2/2160.mp4", page.Streams[0].Url);
            Assert.False(page.Streams[0].IsHls);
            var subtitle = Assert.Single(page.Subtitles);
            Assert.Equal(("en", "English", Site + "subs/Ane-no-Show-2.vtt", ".vtt"), (subtitle.Language, subtitle.Label, subtitle.Url, subtitle.Extension));
        }

        [Fact]
        public void Streams_AreReadEvenWithoutJson()
        {
            var streams = OppaiStreamPage.Streams("<script>var availableres = {'1080':'https://cdn.test/1080.mp4','720':'https://cdn.test/720.mp4',};</script>", new Uri(Site));

            Assert.Equal(new[] { "https://cdn.test/1080.mp4", "https://cdn.test/720.mp4" }, streams.Select(s => s.Url));
        }

        [Theory]
        [InlineData("Ane no Show Ep 2", "Ane no Show", 2)]
        [InlineData("Ane no Show 12", "Ane no Show", 12)]
        [InlineData("Ane no Show Episode 3", "Ane no Show", 3)]
        [InlineData("Solo", "Solo", null)]
        public void SeriesAndNumber_SplitTheTitle(string title, string series, int? number) =>
            Assert.Equal((series, number), OppaiStreamPage.SeriesAndNumber(title));

        [Theory]
        [InlineData(null, "English", "en")]
        [InlineData("es", "Whatever", "es")]
        [InlineData(null, "Español", "es")]
        [InlineData(null, "English (Signs)", "en")]
        [InlineData(null, "Subs", "und")]
        public void LanguageCode_ComesFromSrclangOrLabel(string? srclang, string label, string expected) =>
            Assert.Equal(expected, OppaiStreamPage.LanguageCode(srclang, label));

        [Fact]
        public async Task GetCatalog_ReadsEveryEpisode()
        {
            var catalog = await Create().GetCatalogAsync(CancellationToken.None);

            Assert.Equal(new[] { "Ane no Show 2", "Other Show 1", "Ane no Show 1" }, catalog.Select(v => v.Name));
            var newest = catalog[0];
            Assert.Equal(HentaiSource.OppaiStream, newest.Source);
            Assert.Equal("watch?e=Ane%20no%20Show%202&f=x2", newest.Id);
            Assert.Equal(("Ane no Show", 2), newest.SeriesInfo());
            Assert.Equal("Pink Pineapple", newest.Brand);
            Assert.True(newest.StreamIsFile);
            Assert.False(catalog[1].StreamIsFile);
            Assert.Single(newest.Subtitles);
            Assert.Empty(catalog[2].Subtitles);
            Assert.Equal(Site + "thumbs/Ane-no-Show-2.jpg", newest.ThumbnailUrl);
            Assert.Equal(Site + "posters/Ane-no-Show.jpg", newest.PosterUrl);
            // The list's order: newest first
            Assert.True(catalog[0].CreatedAt > catalog[2].CreatedAt);
            // A short page is the last
            Assert.DoesNotContain(_requests, r => r.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetCatalog_ReadsOnlyNewEpisodesAgain()
        {
            _config.CatalogCacheHours = 1;
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _requests.Clear();
            _override = r => r.RequestUri!.AbsoluteUri.Contains("search.php", StringComparison.Ordinal)
                ? Ok(Search(("Ane no Show", 3), ("Ane no Show", 2), ("Other Show", 1), ("Ane no Show", 1)))
                : r.RequestUri.AbsoluteUri == Site + "watch?e=Ane%20no%20Show%203&f=x3" ? Ok(EpisodePage("Ane no Show", 3)) : null;

            var catalog = await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Equal(4, catalog.Count);
            Assert.Equal(new[] { "/actions/search.php", "/watch" }, _requests.Select(r => r.RequestUri!.AbsolutePath));
        }

        [Fact]
        public async Task GetCatalog_UsesTheSavedCatalogWhenTheSiteFails()
        {
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _override = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("down") };

            var client = Create(_cacheFile);
            var catalog = await client.GetCatalogAsync(CancellationToken.None);

            Assert.Equal(3, catalog.Count);
            Assert.Contains("502", client.CatalogError);
        }

        [Fact]
        public async Task GetCatalog_ExplainsAnEmptySearch()
        {
            _override = _ => Ok("<html>nothing</html>");

            var error = await Assert.ThrowsAsync<OppaiStreamException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("Found no episodes", error.Message);
            Assert.Contains("It answered 20 characters", error.Message);
        }

        [Fact]
        public async Task GetCatalog_AsksForTheSearchAsTheSitesSearchPage()
        {
            await Create().GetCatalogAsync(CancellationToken.None);

            var search = _requests.First(r => r.RequestUri!.AbsolutePath == "/actions/search.php");
            Assert.Equal("XMLHttpRequest", search.Headers.GetValues("X-Requested-With").Single());
            Assert.Equal(Site + "search.php?a=recent", search.Headers.Referrer?.AbsoluteUri);
            Assert.Contains("&ibt=0&", search.RequestUri!.Query, StringComparison.Ordinal);
        }

        [Fact]
        public void Streams_PreferMp4ToWebm()
        {
            // As the site has them: 4K only as WebM
            var streams = OppaiStreamPage.Streams("""<script>var availableres = {"720":"https:\/\/cdn.test\/720\/E04.mp4","1080":"https:\/\/cdn.test\/1080\/E04.mp4","4k":"https:\/\/cdn.test\/4k\/E04.webm"};</script>""", new Uri(Site));

            Assert.Equal(new[] { "1080p", "720p", "2160p" }, streams.Select(s => s.Label));
        }

        [Fact]
        public async Task GetMedia_ReadsTheStreamsBestFirst()
        {
            var media = await Create().GetMediaAsync("watch?e=Ane%20no%20Show%202&f=x2", CancellationToken.None);

            Assert.Equal("https://cdn.oppai.test/2/2160.mp4", media.Streams[0].Url);
            Assert.Single(media.Subtitles);
            Assert.Equal(Site, _requests[0].Headers.Referrer?.AbsoluteUri);
        }

        [Fact]
        public async Task GetMedia_OnlyReadsTheSitesPages()
        {
            await Assert.ThrowsAsync<OppaiStreamException>(() => Create().GetMediaAsync("https://elsewhere.test/watch?e=a", CancellationToken.None));
            await Assert.ThrowsAsync<OppaiStreamException>(() => Create().GetMediaAsync("watch?e=missing", CancellationToken.None));
        }

        [Fact]
        public async Task FetchMedia_SendsTheSitesHeadersAndRange()
        {
            _override = _ => Ok("video");

            using var response = await Create().FetchMediaAsync(new Uri("https://cdn.oppai.test/2/1080.mp4"), "bytes=5-", CancellationToken.None);

            var request = Assert.Single(_requests);
            Assert.Equal(Site, request.Headers.Referrer?.AbsoluteUri);
            Assert.Equal("https://oppai.test", request.Headers.GetValues("Origin").Single());
            Assert.Equal("bytes=5-", request.Headers.GetValues("Range").Single());
        }

        [Fact]
        public void Library_WritesSubtitlesNextToTheEpisode()
        {
            var video = new HentaiVideo
            {
                Source = HentaiSource.OppaiStream,
                Id = "watch?e=Show%201&f=1",
                Name = "Show 1",
                SeriesName = "Show",
                EpisodeNumber = 1,
                StreamIsFile = true,
                Subtitles = [new("en", "English", "https://s/1.vtt"), new("en", "English (Signs)", "https://s/2.srt"), new("und", "Other", "https://s/3")],
            };

            var files = LibraryLayout.Build([video], new HentaiSettings(), v => "https://jf/HanimeTV/OppaiStream/x/" + (v.StreamIsFile ? "video.mp4" : "index.m3u8"))
                .ToDictionary(f => f.RelativePath.Replace('\\', '/'));

            Assert.Equal("https://jf/HanimeTV/OppaiStream/x/video.mp4\n", files["Show/Season 01/Show S01E01.strm"].Content);
            Assert.Equal("https://s/1.vtt", files["Show/Season 01/Show S01E01.en.vtt"].DownloadUrl);
            Assert.Equal("https://s/2.srt", files["Show/Season 01/Show S01E01.en.2.srt"].DownloadUrl);
            Assert.Equal("https://s/3", files["Show/Season 01/Show S01E01.und.vtt"].DownloadUrl);
        }

        [Fact]
        public void Merge_PrefersHanimeThenOppaiStreamThenHentaiHaven()
        {
            HentaiVideo Video(HentaiSource source, string series, int episode) => new()
            {
                Source = source,
                Id = series + episode,
                Name = series + " " + episode,
                SeriesName = series,
                EpisodeNumber = episode,
            };

            var merged = HentaiCatalog.Merge(
            [
                [Video(HentaiSource.Hanime, "Show", 1)],
                [Video(HentaiSource.OppaiStream, "Show", 1), Video(HentaiSource.OppaiStream, "Show", 2)],
                [Video(HentaiSource.HentaiHaven, "Show", 2), Video(HentaiSource.HentaiHaven, "Show", 3)],
            ]);

            Assert.Equal(new[] { "hanime:Show1", "oppaistream:Show2", "hentaihaven:Show3" }, merged.Select(v => v.Key));
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly OppaiStreamTests _test;

            public Handler(OppaiStreamTests test) => _test = test;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_test._requests)
                {
                    _test._requests.Add(request);
                }

                return Task.FromResult(_test.Respond(request));
            }
        }

        private sealed class FixedTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
