using System.Xml.Linq;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.Library;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class LibraryLayoutTests
    {
        private readonly PluginConfiguration _config = new();

        private static HanimeVideo Video(string slug, string name, int released, params string[] tags) => new()
        {
            Slug = slug,
            Name = name,
            Brand = "Studio",
            Tags = tags,
            Likes = 9,
            Dislikes = 1,
            Description = "<p>About " + name + ".</p>",
            ReleasedAt = new DateTime(released, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc),
            PosterUrl = "https://cdn/" + slug + "-cover.png",
            ThumbnailUrl = "https://cdn/" + slug + "-poster.jpg",
            IsCensored = tags.Contains("censored"),
        };

        private IReadOnlyList<LibraryFile> Build(params HanimeVideo[] videos) =>
            LibraryLayout.Build(videos, _config, slug => "http://jellyfin:8096/HanimeTV/Stream/" + slug + "/index.m3u8?token=t");

        private static Dictionary<string, string> ByPath(IReadOnlyList<LibraryFile> files) =>
            files.ToDictionary(f => f.RelativePath.Replace('\\', '/'), f => f.Content);

        [Fact]
        public void Build_WritesSeriesSeasonsAndEpisodes()
        {
            var files = ByPath(Build(Video("show-2", "Show 2", 2011, "hd"), Video("show-1", "Show 1", 2010, "hd", "plot"), Video("solo", "Solo", 2020)));

            Assert.Equal(
                new[]
                {
                    "Show/tvshow.nfo", "Show/Season 01/Show S01E01.strm", "Show/Season 01/Show S01E01.nfo",
                    "Show/Season 01/Show S01E02.strm", "Show/Season 01/Show S01E02.nfo",
                    "Solo/tvshow.nfo", "Solo/Season 01/Solo S01E01.strm", "Solo/Season 01/Solo S01E01.nfo",
                },
                files.Keys);
            Assert.Equal("http://jellyfin:8096/HanimeTV/Stream/show-1/index.m3u8?token=t\n", files["Show/Season 01/Show S01E01.strm"]);
        }

        [Fact]
        public void EpisodeNfo_CarriesTheMetadata()
        {
            var nfo = XElement.Parse(ByPath(Build(Video("show-1", "Show 1", 2010, "hd", "big boobs", "censored")))["Show/Season 01/Show S01E01.nfo"]);

            Assert.Equal("episodedetails", nfo.Name.LocalName);
            Assert.Equal("Show 1", (string?)nfo.Element("title"));
            Assert.Equal("Show", (string?)nfo.Element("showtitle"));
            Assert.Equal("1", (string?)nfo.Element("episode"));
            Assert.Equal("About Show 1.", (string?)nfo.Element("plot"));
            Assert.Equal("2010-01-02", (string?)nfo.Element("aired"));
            Assert.Equal(new[] { "HD", "Big Boobs" }, nfo.Elements("genre").Select(g => g.Value));
            Assert.Equal("censored", (string?)nfo.Element("tag"));
            Assert.Equal("9.0", (string?)nfo.Element("rating"));
            Assert.Equal(LibraryLayout.AdultRating, (string?)nfo.Element("mpaa"));
            Assert.Equal("2026-01-01 12:30:00", (string?)nfo.Element("dateadded"));
            Assert.Equal("show-1", (string?)nfo.Element("uniqueid"));
            Assert.Equal("https://cdn/show-1-poster.jpg", (string?)nfo.Element("thumb"));
        }

        [Fact]
        public void SeriesNfo_CarriesTheMetadata()
        {
            var nfo = XElement.Parse(ByPath(Build(Video("show-2", "Show 2", 2011, "milf"), Video("show-1", "Show 1", 2010, "hd")))["Show/tvshow.nfo"]);

            Assert.Equal("Show", (string?)nfo.Element("title"));
            Assert.Equal("About Show 1.", (string?)nfo.Element("plot"));
            Assert.Equal(new[] { "HD", "MILF" }, nfo.Elements("genre").Select(g => g.Value));
            Assert.Equal("2010-01-02", (string?)nfo.Element("premiered"));
            Assert.Equal("https://cdn/show-1-cover.png", nfo.Elements("thumb").Single(t => (string?)t.Attribute("aspect") == "poster").Value);
            Assert.Equal("https://cdn/show-1-poster.jpg", nfo.Element("fanart")?.Element("thumb")?.Value);
        }

        [Fact]
        public void Episodes_AreNumberedInOrderWhenNumbersClash()
        {
            // "Title" and "Title 1" both claim episode 1
            var series = Assert.Single(LibraryLayout.Series([Video("b", "Title 1", 2012), Video("a", "Title", 2010), Video("c", "Title 2", 2013)]));

            Assert.Equal(new[] { ("a", 1), ("b", 2), ("c", 3) }, series.Episodes.Select(e => (e.Video.Slug, e.Number)));
        }

        [Fact]
        public void Filters_LeaveVideosOut()
        {
            _config.HiddenTags = ["scat"];
            _config.HideCensored = true;

            var files = ByPath(Build(Video("a", "A", 2010, "scat"), Video("b", "B", 2010, "censored"), Video("c", "C", 2010, "hd")));

            Assert.Equal(new[] { "C/tvshow.nfo", "C/Season 01/C S01E01.strm", "C/Season 01/C S01E01.nfo" }, files.Keys);
        }

        [Theory]
        [InlineData("Re:Zero? \"Special\"", "Re Zero Special")]
        [InlineData("A/B\\C", "A B C")]
        [InlineData("Dots...", "Dots")]
        [InlineData("???", "Unnamed")]
        public void FileName_IsSafeEverywhere(string name, string expected) => Assert.Equal(expected, LibraryLayout.FileName(name));

        [Fact]
        public void SeriesWithTheSameFileName_GetTheirOwnFolders()
        {
            var files = ByPath(Build(Video("a", "What?", 2010), Video("b", "What", 2011)));

            Assert.Contains("What/tvshow.nfo", files.Keys);
            Assert.Contains("What (2)/tvshow.nfo", files.Keys);
        }
    }
}
