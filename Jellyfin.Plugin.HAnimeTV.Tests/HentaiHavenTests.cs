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
        private const string Player = "https://player.test/";

        private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        private readonly HentaiSettings _config = new() { HentaiHavenUrl = "https://haven.test" };
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), "hh-test-" + Guid.NewGuid().ToString("N") + ".json");
        private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

        public void Dispose() => File.Delete(_cacheFile);

        // The home page's list, newest first, as the site renders it
        internal static string Home(int lastPage, params (string Slug, string Title, int Episode)[] episodes) =>
            "<html><head><title>Hentai Haven | Hentai Series &amp; Episodes</title></head><body><div class=\"sub_overview\">"
            + string.Concat(episodes.Select(e =>
                $"""<a class="a_item" href="/watch/{e.Slug}-episode-{e.Episode}/"><div class="v_item"><div class="video_cover"><img alt="{e.Title} Episode {e.Episode}" loading="lazy" width="268" height="394" decoding="async" data-nimg="1" class="lazy" style="color:transparent" src="/uploads/img_{e.Slug}-{e.Episode}.jpg"/><div class="card_badges"><span class="card_badge">EP {e.Episode}</span><span class="card_badge card_badge_new">New</span></div><button type="button" class="card_save" aria-pressed="false"><i class="fa fa-bookmark" aria-hidden="true"></i></button></div><div class="video_title">{e.Title} Episode {e.Episode}</div></div></a>"""))
            + "</div><ul class=\"pagination\"><li class=\"page-item disabled\"><a class=\"page-link\" href=\"#\" tabindex=\"-1\">Previous</a></li><li class=\"page-item active\"><a class=\"page-link\" href=\"/\">1</a></li>"
            + string.Concat(Enumerable.Range(2, Math.Max(0, lastPage - 1)).Select(p => $"<li class=\"page-item\"><a class=\"page-link\" href=\"/?page={p}\">{p}</a></li>"))
            + "</ul></body></html>";

        internal static string EpisodePage(string slug, string series, int episode, string player) =>
            $"""
            <html><head><meta property="og:image" content="{Site}uploads/posters/{slug}-episode-{episode}-feature.jpg"/></head><body>
            <div class="video"><div class="left"><div class="watch-ad-top mobile"><div style="display:inline-block;max-width:100%"><iframe style="background-color: white;" width="300" height="250" scrolling="no" frameborder="0" name="spot_id_10001807" src="//ads.test/get/10001807?ata=x"></iframe></div></div>
            <div class="player"><iframe allowfullscreen="" scrolling="no" loading="lazy" style="width:100%;height:100%" src="{player}"></iframe></div>
            <div class="info_top"><h1 class="video_title">{series} Episode {episode}</h1><div class="video_views">31,784<!-- --> views</div><div class="buttons"><input type="hidden" id="video_id" value="259"/><div class="button_item like tt" data-vote=""><span class="tt_text">Like</span><i class="fa fa-heart"></i><span class="ln">30</span></div><div class="button_item dislike tt" data-vote=""><span class="tt_text">Dislike</span><i class="fa fa-heart-broken"></i><span class="dn">2</span></div></div></div>
            <div class="info_bottom"><div class="cover"><img alt="{series} Episode {episode} cover" loading="lazy" width="150" height="200" decoding="async" data-nimg="1" class="lazy" style="color:transparent" src="/uploads/posters/{slug}-episode-{episode}-feature.jpg"/></div><div class="r_info_b"><div class="flex_wrap"><div class="r_item half"><span>Brand</span><span class="sub_r"><a href="/brand/magin-label/" rel="nofollow">Magin Label</a></span></div><div class="r_item half"><span>Episodes</span><span class="sub_r">47</span></div><div class="r_item half"><span>Views</span><span class="sub_r">31,784</span></div><div class="r_item half"><span>Series</span><span class="sub_r"><a href="/series/{slug}/">{series}</a></span></div><div class="r_item half"><span>Release Date</span><span class="sub_r">2011-12-22</span></div><div class="r_item half"><span>Upload Date</span><span class="sub_r">2014-07-23</span></div></div></div><div class="clear"></div></div>
            <div class="video_tags"><a href="/genre/bdsm/" rel="nofollow">BDSM</a><a href="/genre/censored/" rel="nofollow">censored</a><a href="/tag/hentai/" rel="nofollow">hentai</a><span class="more-tags">... and <!-- -->2<!-- --> more</span><div class="video_description"><p>First paragraph &amp; more.</p><p>Second paragraph.</p></div></div>
            </div><div class="right"><div class="mfs_item"><div class="now_playing">Now Playing</div><div class="poster"><a href="/watch/{slug}-episode-{episode}/" rel="nofollow"><img alt="{series} Episode {episode}" loading="lazy" width="120" height="80" class="lazy" src="/uploads/thumbs/{slug}-episode-{episode}-backdrop.jpg"/></a></div></div></div></div>
            </body></html>
            """;

        // nhplayer's page: a server per video, its address in base64 "address|expiry|signature"
        internal static string PlayerPage(string video) =>
            $"""<html><body><div class="frame"><header class="header"><div class="servers"><ul><li data-id="/player.php?vid={Convert.ToBase64String(Encoding.UTF8.GetBytes(video + "|1791477583|cb4b317cdc15fee3"))}&i=aW1n&type=">Main Player</li></ul></div></header><iframe src="" allowFullScreen="true"></iframe></div></body></html>""";

        private HentaiHavenClient Create(string? cacheFile = null)
        {
            var handler = new Handler(this);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            return new HentaiHavenClient(factory.Object, () => _config, cacheFile, NullLogger.Instance, new FixedTime(), TimeSpan.Zero);
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
                Site => Home(2, ("ane-no-show", "Ane no Show", 2), ("other-show", "Other Show", 1)),
                Site + "?page=2" => Home(2, ("ane-no-show", "Ane no Show", 1)),
                Site + "watch/ane-no-show-episode-2/" => EpisodePage("ane-no-show", "Ane no Show", 2, Player + "v/abc/"),
                Site + "watch/ane-no-show-episode-1/" => EpisodePage("ane-no-show", "Ane no Show", 1, Player + "v/def/"),
                Site + "watch/other-show-episode-1/" => EpisodePage("other-show", "Other Show", 1, Player + "v/ghi/"),
                Player + "v/abc/" => PlayerPage("https://cdn.test/ane-no-show-2.mp4"),
                _ when url.StartsWith(Player + "player.php", StringComparison.Ordinal) => "<html><body><div id=\"player\"></div></body></html>",
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>Not found</html>") }
                : Ok(body);
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

        // Cloudflare's bot check, as it answers browsers it does not trust yet
        private static HttpResponseMessage Challenge()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><head><title>Just a moment...</title></head><body><script>window._cf_chl_opt={cZone:'haven.test'};</script></body></html>"),
            };
            response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return response;
        }

        private List<string> EpisodeRequests()
        {
            lock (_requests)
            {
                return _requests.Select(r => r.RequestUri!.AbsolutePath).Where(p => p.StartsWith("/watch/", StringComparison.Ordinal)).ToList();
            }
        }

        [Fact]
        public void Listing_ReadsTheCardsAndPages()
        {
            var html = Home(85, ("pure-x-holic", "Pure x Holic: Junketsu Otome &amp; Konin!?", 2), ("other", "Other", 1));

            var listing = HentaiHavenPage.Listing(html, new Uri(Site));

            Assert.Equal(2, listing.Count);
            Assert.Equal(new HentaiHavenListing(Site + "watch/pure-x-holic-episode-2/", "Pure x Holic: Junketsu Otome & Konin!? Episode 2", Site + "uploads/img_pure-x-holic-2.jpg", 2), listing[0]);
            Assert.Equal(85, HentaiHavenPage.LastPage(html));
            Assert.Equal(1, HentaiHavenPage.LastPage("<html></html>"));
        }

        [Fact]
        public void Episode_ReadsTheDetailsAndThePlayer()
        {
            var page = HentaiHavenPage.Episode(EpisodePage("euphoria", "Euphoria", 1, "https://nhplayer.com/v/3V4k3I8gxvHUJjA/"), new Uri(Site + "watch/euphoria-episode-1/"));

            Assert.Equal("Euphoria Episode 1", page.Title);
            Assert.Equal("Euphoria", page.SeriesName);
            Assert.Equal("Magin Label", page.Studio);
            Assert.Equal("First paragraph & more.\n\nSecond paragraph.", page.Description);
            Assert.Equal(new[] { "BDSM", "censored" }, page.Genres);
            Assert.Equal(Site + "uploads/posters/euphoria-episode-1-feature.jpg", page.PosterUrl);
            Assert.Equal(Site + "uploads/thumbs/euphoria-episode-1-backdrop.jpg", page.ThumbnailUrl);
            Assert.Equal(new DateTime(2011, 12, 22, 0, 0, 0, DateTimeKind.Utc), page.ReleasedAt);
            Assert.Equal(new DateTime(2014, 7, 23, 0, 0, 0, DateTimeKind.Utc), page.UploadedAt);
            Assert.Equal((31784L, 30L, 2L), (page.Views, page.Likes, page.Dislikes));
            // Not the ad's frame before it
            Assert.Equal("https://nhplayer.com/v/3V4k3I8gxvHUJjA/", page.Player?.AbsoluteUri);
        }

        [Fact]
        public void PlayerServers_DecodeTheVideosAddress()
        {
            var servers = HentaiHavenPage.PlayerServers(PlayerPage("https://r2.1hanime.com/euphoria-1.mp4"), new Uri("https://nhplayer.com/v/3V4k3I8gxvHUJjA/"));

            var (player, video) = Assert.Single(servers);
            Assert.StartsWith("https://nhplayer.com/player.php?vid=", player.AbsoluteUri, StringComparison.Ordinal);
            Assert.Equal("https://r2.1hanime.com/euphoria-1.mp4", video);
        }

        [Theory]
        [InlineData("aHR0cHM6Ly9yMi4xaGFuaW1lLmNvbS9ldXBob3JpYS0xLm1wNHwxNzkxNDc3NTgzfGNiNGIzMTdjZGMxNWZlZTM=", "https://r2.1hanime.com/euphoria-1.mp4")]
        [InlineData("aHR0cHM6Ly9yMi4xaGFuaW1lLmNvbS9ldXBob3JpYS0xLm1wNHwxNzkxNDc3NTgzfGNiNGIzMTdjZGMxNWZlZTM", "https://r2.1hanime.com/euphoria-1.mp4")]
        [InlineData("bm90IGEgdXJs", null)]
        [InlineData("%%%", null)]
        [InlineData(null, null)]
        public void DecodeVid_ReadsTheAddress(string? vid, string? expected) => Assert.Equal(expected, HentaiHavenPage.DecodeVid(vid));

        [Fact]
        public void DirectStreams_FindsAddressesInScripts()
        {
            const string Html = """<script>player.setup({file:"https:\/\/cdn.test\/v\/720p.mp4"}); var hls = 'https://cdn.test/v/master.m3u8?t=1';</script>""";

            var streams = HentaiHavenPage.DirectStreams(Html, new Uri(Player));

            Assert.Equal(new[] { "https://cdn.test/v/master.m3u8?t=1", "https://cdn.test/v/720p.mp4" }, streams.Select(s => s.Url));
            Assert.All(streams, s => Assert.Equal(Player, s.Referer));
        }

        [Theory]
        [InlineData("Euphoria Episode 1", "Euphoria", 1)]
        [InlineData("Pure x Holic: The Animation Episode 12", "Pure x Holic: The Animation", 12)]
        [InlineData("2x1 Ep 3", "2x1", 3)]
        [InlineData("Aki Sora Ova", "Aki Sora Ova", null)]
        public void SeriesAndNumber_SplitTheTitle(string title, string series, int? number) =>
            Assert.Equal((series, number), HentaiHavenPage.SeriesAndNumber(title));

        [Fact]
        public void NumberInUrl_ReadsTheSlug()
        {
            Assert.Equal(4, HentaiHavenPage.NumberInUrl(Site + "watch/euphoria-episode-4/"));
            Assert.Null(HentaiHavenPage.NumberInUrl(Site + "watch/aki-sora-ova/"));
        }

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
            Assert.False(HentaiHavenPage.IsChallenge(Home(1)));
            // The script Cloudflare adds to the sites' own pages
            Assert.False(HentaiHavenPage.IsChallenge(Home(1) + "<script src=\"/cdn-cgi/challenge-platform/scripts/jsd/main.js\"></script>"));
        }

        [Fact]
        public async Task GetCatalog_ReadsEveryPageAndEpisode()
        {
            var catalog = await Create().GetCatalogAsync(CancellationToken.None);

            Assert.Equal(new[] { "Ane no Show 2", "Other Show 1", "Ane no Show 1" }, catalog.Select(v => v.Name));
            var newest = catalog[0];
            Assert.Equal(HentaiSource.HentaiHaven, newest.Source);
            Assert.Equal("watch/ane-no-show-episode-2/", newest.Id);
            Assert.Equal(("Ane no Show", 2), newest.SeriesInfo());
            Assert.Equal("Magin Label", newest.Brand);
            Assert.Equal(Site + "uploads/posters/ane-no-show-episode-2-feature.jpg", newest.PosterUrl);
            Assert.Equal(Site + "uploads/thumbs/ane-no-show-episode-2-backdrop.jpg", newest.ThumbnailUrl);
            Assert.Equal(new DateTime(2014, 7, 23, 0, 0, 0, DateTimeKind.Utc), newest.CreatedAt);
            Assert.True(newest.IsCensored);
            Assert.True(newest.StreamIsFile);
            Assert.Equal(30, newest.Likes);
        }

        [Fact]
        public async Task GetCatalog_ReadsOnlyNewEpisodesAgain()
        {
            _config.CatalogCacheHours = 1;
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _requests.Clear();
            _override = r => r.RequestUri!.AbsoluteUri == Site
                ? Ok(Home(2, ("ane-no-show", "Ane no Show", 3), ("ane-no-show", "Ane no Show", 2), ("other-show", "Other Show", 1)))
                : r.RequestUri.AbsoluteUri == Site + "watch/ane-no-show-episode-3/" ? Ok(EpisodePage("ane-no-show", "Ane no Show", 3, Player + "v/jkl/")) : null;

            var catalog = await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Equal(4, catalog.Count);
            Assert.Equal(new[] { "/", "/", "/watch/ane-no-show-episode-3/" }, _requests.Select(r => r.RequestUri!.AbsolutePath).Order(StringComparer.Ordinal));
        }

        [Fact]
        public async Task GetCatalog_KeepsGoingWhenAnEpisodePageFails()
        {
            _override = r => r.RequestUri!.AbsolutePath == "/watch/other-show-episode-1/" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") } : null;

            var catalog = await Create().GetCatalogAsync(CancellationToken.None);

            Assert.Equal(new[] { "Ane no Show 2", "Ane no Show 1" }, catalog.Select(v => v.Name));
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
        public async Task GetCatalog_DescribesAnotherSite()
        {
            _override = _ => Ok("<html><head><title>Something Else</title></head><body><a href=\"/foo/bar\">x</a></body></html>");

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("Found no episodes", error.Message);
            Assert.Contains("titled \"Something Else\"", error.Message);
        }

        [Fact]
        public async Task GetCatalog_ReportsBotChecks()
        {
            _override = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title><script src=\"/cdn-cgi/challenge-platform/x\"></script>") };

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("bot check", error.Message);
        }

        [Fact]
        public async Task GetCatalog_StopsAtABotCheckAndKeepsWhatItHas()
        {
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            // Twenty new episodes, and Cloudflare now checks every episode page
            var added = Enumerable.Range(1, 20).Select(n => ("new-show", "New Show", n)).ToArray();
            _override = r => r.RequestUri!.AbsoluteUri == Site
                ? Ok(Home(2, added.Concat([("ane-no-show", "Ane no Show", 2), ("other-show", "Other Show", 1)]).ToArray()))
                : r.RequestUri.AbsolutePath.StartsWith("/watch/new-show", StringComparison.Ordinal) ? Challenge() : null;
            _requests.Clear();

            var catalog = await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Equal(new[] { "Ane no Show 2", "Other Show 1", "Ane no Show 1" }, catalog.Select(v => v.Name));
            // The episodes read before were not read again, and the crawl stopped soon
            Assert.DoesNotContain("/watch/ane-no-show-episode-2/", EpisodeRequests());
            Assert.InRange(EpisodeRequests().Count, 1, 8);
        }

        [Fact]
        public async Task GetCatalog_ReportsABotCheckWhenItHasNothing()
        {
            _override = r => r.RequestUri!.AbsolutePath.StartsWith("/watch/", StringComparison.Ordinal) ? Challenge() : null;

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("bot check", error.Message);
            Assert.Contains("FlareSolverr", error.Message);
        }

        [Fact]
        public async Task GetCatalog_ReadsEpisodesAgainThatHaveNoPlayer()
        {
            // Read by version 0.3.0, which did not keep the player
            File.WriteAllText(_cacheFile, $$"""{"Site":"{{Site}}","Episodes":[{"Url":"{{Site}}watch/ane-no-show-episode-1/","Title":"Ane no Show Episode 1","SeriesName":"Ane no Show","Number":1,"FetchedAt":"{{Now:O}}"}]}""");

            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Contains("/watch/ane-no-show-episode-1/", EpisodeRequests());
        }

        [Fact]
        public async Task GetCatalog_DoesNotReadPagesWithoutPlayerAgain()
        {
            _override = r => r.RequestUri!.AbsolutePath == "/watch/other-show-episode-1/" ? Ok("<html><h1 class=\"video_title\">Other Show Episode 1</h1></html>") : null;
            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);
            _requests.Clear();

            await Create(_cacheFile).GetCatalogAsync(CancellationToken.None);

            Assert.Empty(EpisodeRequests());
        }

        [Fact]
        public async Task GetCatalog_PassesBotChecksWithFlareSolverr()
        {
            _config.FlareSolverrUrl = "http://solver.test:8191/";
            var solved = 0;
            _override = r =>
            {
                if (r.RequestUri!.Host == "solver.test")
                {
                    Interlocked.Increment(ref solved);
                    var url = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("url").GetString()!;
                    using var page = Respond(new HttpRequestMessage(HttpMethod.Get, url) { Headers = { { "Cookie", "cf_clearance=ok" }, { "User-Agent", "Solver Browser/1.0" } } });
                    var html = System.Text.Json.JsonSerializer.Serialize(page.Content.ReadAsStringAsync().Result);
                    return Ok($$"""{"status":"ok","message":"Challenge solved!","solution":{"url":"{{url}}","status":{{(int)page.StatusCode}},"headers":{},"response":{{html}},"cookies":[{"name":"cf_clearance","value":"ok","domain":".haven.test"}],"userAgent":"Solver Browser/1.0"},"version":"3.3.21"}""");
                }

                // Pages of the site only with FlareSolverr's cookie and browser
                return r.RequestUri.Host == "haven.test"
                    && !(r.Headers.TryGetValues("Cookie", out var cookie) && cookie.Single().Contains("cf_clearance=ok", StringComparison.Ordinal)
                        && r.Headers.UserAgent.ToString() == "Solver Browser/1.0")
                    ? Challenge() : null;
            };

            var catalog = await Create().GetCatalogAsync(CancellationToken.None);

            Assert.Equal(3, catalog.Count);
            // Once: the requests after it go in with its cookie
            Assert.Equal(1, solved);
            var solverRequest = Assert.Single(_requests, r => r.RequestUri!.Host == "solver.test");
            Assert.Equal("http://solver.test:8191/v1", solverRequest.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task GetCatalog_ReportsFlareSolverrsErrors()
        {
            _config.FlareSolverrUrl = "http://solver.test:8191";
            _override = r => r.RequestUri!.Host == "solver.test"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"status":"error","message":"Error: Error solving the challenge. Timeout after 60.0 seconds."}""") }
                : Challenge();

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetCatalogAsync(CancellationToken.None));

            Assert.Contains("FlareSolverr could not get https://haven.test/: Error: Error solving the challenge", error.Message);
        }

        [Fact]
        public async Task GetStreams_GoesToTheKnownPlayerWithoutTheSite()
        {
            var client = Create();
            await client.GetCatalogAsync(CancellationToken.None);
            _requests.Clear();
            // Cloudflare checks the site, but not the player
            _override = r => r.RequestUri!.Host == "haven.test" ? Challenge() : null;

            var streams = await client.GetStreamsAsync("watch/ane-no-show-episode-2/", CancellationToken.None);

            Assert.Equal("https://cdn.test/ane-no-show-2.mp4", Assert.Single(streams).Url);
            Assert.Empty(EpisodeRequests());
        }

        [Fact]
        public async Task GetStreams_FollowsThePlayerToTheVideo()
        {
            var streams = await Create().GetStreamsAsync("watch/ane-no-show-episode-2/", CancellationToken.None);

            var stream = Assert.Single(streams);
            Assert.Equal("https://cdn.test/ane-no-show-2.mp4", stream.Url);
            Assert.False(stream.IsHls);
            // The player is asked for with the episode's page as Referer, its server with the player's
            var player = Assert.Single(_requests, r => r.RequestUri!.AbsoluteUri == Player + "v/abc/");
            Assert.Equal(Site + "watch/ane-no-show-episode-2/", player.Headers.Referrer?.AbsoluteUri);
            var server = Assert.Single(_requests, r => r.RequestUri!.AbsolutePath == "/player.php");
            Assert.Equal(Player + "v/abc/", server.Headers.Referrer?.AbsoluteUri);
        }

        [Fact]
        public async Task GetStreams_PrefersWhatThePlayersServerNames()
        {
            _override = r => r.RequestUri!.AbsolutePath == "/player.php"
                ? Ok("<video><source src=\"https://cdn2.test/ane-no-show-2/1080p.mp4\" type=\"video/mp4\"></video>")
                : null;

            var streams = await Create().GetStreamsAsync("watch/ane-no-show-episode-2/", CancellationToken.None);

            Assert.Equal("https://cdn2.test/ane-no-show-2/1080p.mp4", Assert.Single(streams).Url);
        }

        [Fact]
        public async Task GetStreams_ExplainsAPageWithoutPlayer()
        {
            _override = r => r.RequestUri!.AbsolutePath == "/watch/ane-no-show-episode-2/" ? Ok("<html><title>Removed</title></html>") : null;

            var error = await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetStreamsAsync("watch/ane-no-show-episode-2/", CancellationToken.None));

            Assert.Contains("no player", error.Message);
        }

        [Fact]
        public async Task GetStreams_OnlyReadsTheSitesPages()
        {
            await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetStreamsAsync("https://elsewhere.test/watch/a/", CancellationToken.None));
            await Assert.ThrowsAsync<HentaiHavenException>(() => Create().GetStreamsAsync("//elsewhere.test/watch/a/", CancellationToken.None));
        }

        [Fact]
        public async Task FetchMedia_SendsThePlayerAsReferer()
        {
            var client = Create();
            await client.GetStreamsAsync("watch/ane-no-show-episode-2/", CancellationToken.None);
            _requests.Clear();
            _override = _ => Ok("video");

            using var video = await client.FetchMediaAsync(new Uri("https://cdn.test/ane-no-show-2.mp4"), "bytes=0-99", CancellationToken.None);
            using var other = await client.FetchMediaAsync(new Uri("https://unknown.test/a.mp4"), null, CancellationToken.None);

            Assert.Equal(Player + "v/abc/", _requests[0].Headers.Referrer?.AbsoluteUri);
            Assert.Equal("https://player.test", _requests[0].Headers.GetValues("Origin").Single());
            Assert.Equal("bytes=0-99", _requests[0].Headers.GetValues("Range").Single());
            Assert.Equal(Site, _requests[1].Headers.Referrer?.AbsoluteUri);
        }

        [Theory]
        [InlineData("https://haven.test/", "https://haven.test/watch/a-episode-1/", "watch/a-episode-1/")]
        [InlineData("https://haven.test/sub/", "https://haven.test/sub/watch/a-episode-1/", "watch/a-episode-1/")]
        [InlineData("https://haven.test/sub/", "https://www.haven.test/watch/a/?p=1", "/watch/a/?p=1")]
        [InlineData("https://haven.test/", "https://cdn.test/watch/a/", "https://cdn.test/watch/a/")]
        public void RelativePath_ResolvesBackToTheEpisode(string site, string episode, string expected)
        {
            var path = HentaiHavenClient.RelativePath(new Uri(site), episode);

            Assert.Equal(expected, path);
            Assert.Equal(new Uri(new Uri(site), path).PathAndQuery, new Uri(episode).PathAndQuery);
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
            var haven = new[] { Haven("Show", 1), Haven("Show", 1, "watch/show-episode-1-preview/") };

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

        private sealed class Handler : HttpMessageHandler
        {
            private readonly HentaiHavenTests _test;

            public Handler(HentaiHavenTests test) => _test = test;

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
