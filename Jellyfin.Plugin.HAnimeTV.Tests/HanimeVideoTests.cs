using System.Text.Json;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HanimeVideoTests
    {
        private static HanimeVideo? Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return HanimeVideo.FromJson(document.RootElement);
        }

        [Fact]
        public void FromJson_ReadsTheSearchDataset()
        {
            var video = Parse("""
                {
                  "id": 3405, "name": "Some &amp; Title 2", "slug": "some-title-2",
                  "description": "<p>First line.</p><p>Second &quot;line&quot;</p>",
                  "brand": "Studio", "tags": ["hd", "Plot", "hd"], "views": 1234, "likes": 90, "dislikes": 10,
                  "cover_url": "https://cdn/cover.jpg", "poster_url": "https://cdn/poster.jpg",
                  "is_censored": true, "created_at_unix": 1700000000, "released_at_unix": 1699000000
                }
                """)!;

            Assert.Equal("some-title-2", video.Slug);
            Assert.Equal("Some & Title 2", video.Name);
            Assert.Equal(new[] { "hd", "Plot" }, video.Tags);
            Assert.Equal("https://cdn/cover.jpg", video.PosterUrl);
            Assert.Equal("https://cdn/poster.jpg", video.ThumbnailUrl);
            Assert.True(video.IsCensored);
            Assert.Equal(1234, video.Views);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), video.CreatedAt);
            Assert.Equal("First line.\nSecond \"line\"", video.PlainDescription());
            Assert.Null(video.DurationMs);
        }

        [Fact]
        public void FromJson_ReadsOlderFormats()
        {
            var video = Parse("""
                { "name": "X", "slug": "x", "tags": [{ "text": "milf" }], "created_at": 1700000000000,
                  "released_at": "2020-05-01T00:00:00Z", "duration_in_ms": 1500000, "views": "17" }
                """)!;

            Assert.Equal(new[] { "milf" }, video.Tags);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), video.CreatedAt);
            Assert.Equal(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc), video.ReleasedAt);
            Assert.Equal(1500000, video.DurationMs);
            Assert.Equal(17, video.Views);
        }

        [Theory]
        [InlineData("""{ "name": "No slug" }""")]
        [InlineData("""{ "slug": "no-name" }""")]
        [InlineData("""[1]""")]
        public void FromJson_SkipsIncompleteEntries(string json) => Assert.Null(Parse(json));

        [Theory]
        [InlineData("Bible Black 6", "Bible Black", 6)]
        [InlineData("Overflow - Episode 3", "Overflow", 3)]
        [InlineData("Some Show Ep. 12", "Some Show", 12)]
        [InlineData("Title: 2", "Title", 2)]
        [InlineData("Stand Alone", "Stand Alone", 1)]
        [InlineData("Year 2020", "Year 2020", 1)]
        [InlineData("Re:Zero2", "Re:Zero2", 1)]
        public void SeriesInfo_SplitsTrailingEpisodeNumbers(string name, string series, int episode)
        {
            var video = new HanimeVideo { Slug = "s", Name = name };

            Assert.Equal((series, episode), video.SeriesInfo());
        }
    }
}
