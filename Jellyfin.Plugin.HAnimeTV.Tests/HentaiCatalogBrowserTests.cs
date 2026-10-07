using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.Library;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HentaiCatalogBrowserTests
    {
        private readonly HentaiSettings _config = new();

        private static readonly HentaiVideo[] Catalog =
        [
            Video("show-1", "Show 1", "Pink", ["hd", "plot"], views: 50, created: 1, released: 2010),
            Video("show-2", "Show 2", "Pink", ["hd"], views: 10, created: 5, released: 2012),
            Video("other", "Another One", "Blue", ["milf"], views: 99, created: 3, released: 2020, censored: true),
            Video("numeric", "1LDK", null, ["hd", "scat"], views: 1, created: 4, released: 2001),
        ];

        private static HentaiVideo Video(string slug, string name, string? brand, string[] tags, long views, int created, int released, bool censored = false) => new()
        {
            Id = slug,
            Name = name,
            Brand = brand,
            Tags = tags,
            Views = views,
            Likes = views / 2,
            Dislikes = 1,
            IsCensored = censored,
            CreatedAt = new DateTime(2026, 1, created, 0, 0, 0, DateTimeKind.Utc),
            ReleasedAt = new DateTime(released, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ThumbnailUrl = "https://cdn/" + slug + "-thumb.jpg",
            PosterUrl = "https://cdn/" + slug + "-poster.jpg",
        };

        private IReadOnlyList<ChannelItemInfoView> Items(string? folder) =>
            HentaiCatalogBrowser.GetItems(Catalog, _config, folder).Items.Select(i => new ChannelItemInfoView(i.Id, i.Name, i.Type)).ToList();

        private sealed record ChannelItemInfoView(string Id, string Name, ChannelItemType Type);

        [Fact]
        public void Root_ListsTheCategories()
        {
            var root = Items(null);

            Assert.All(root, i => Assert.Equal(ChannelItemType.Folder, i.Type));
            Assert.Equal(
                new[] { HentaiCatalogBrowser.Latest, HentaiCatalogBrowser.Released, HentaiCatalogBrowser.Popular, HentaiCatalogBrowser.Liked, HentaiCatalogBrowser.Series, HentaiCatalogBrowser.Genres, HentaiCatalogBrowser.Studios },
                root.Select(i => i.Id));
        }

        [Fact]
        public void Categories_AreSortedAndLimited()
        {
            Assert.Equal(new[] { "latest|hanime:show-2", "latest|hanime:numeric", "latest|hanime:other", "latest|hanime:show-1" }, Items(HentaiCatalogBrowser.Latest).Select(i => i.Id));
            Assert.Equal(new[] { "hanime:other", "hanime:show-1", "hanime:show-2", "hanime:numeric" }, Items(HentaiCatalogBrowser.Popular).Select(i => HentaiCatalogBrowser.KeyOf(i.Id)));
            Assert.Equal(new[] { "hanime:other", "hanime:show-2", "hanime:show-1", "hanime:numeric" }, Items(HentaiCatalogBrowser.Released).Select(i => HentaiCatalogBrowser.KeyOf(i.Id)));

            _config.MaxItemsPerFolder = 1;
            // At least 10, whatever the setting says
            Assert.Equal(4, Items(HentaiCatalogBrowser.Latest).Count);
        }

        [Fact]
        public void HiddenTagsAndCensoredVideos_AreLeftOutEverywhere()
        {
            _config.HiddenTags = [" SCAT ", ""];
            _config.HideCensored = true;

            Assert.Equal(new[] { "hanime:show-2", "hanime:show-1" }, Items(HentaiCatalogBrowser.Latest).Select(i => HentaiCatalogBrowser.KeyOf(i.Id)));
            Assert.DoesNotContain(Items(HentaiCatalogBrowser.Genres), i => i.Name.StartsWith("Scat", StringComparison.Ordinal));
            Assert.Empty(Items("genre:scat"));
            Assert.Empty(Items("studio:blue"));
        }

        [Fact]
        public void GenresAndStudios_GroupTheVideos()
        {
            Assert.Equal(
                new[] { ("genre:hd", "HD (3)"), ("genre:milf", "MILF (1)"), ("genre:plot", "Plot (1)"), ("genre:scat", "Scat (1)") },
                Items(HentaiCatalogBrowser.Genres).Select(i => (i.Id, i.Name)));
            Assert.Equal(new[] { "hanime:show-2", "hanime:show-1", "hanime:numeric" }, Items("genre:hd").Select(i => HentaiCatalogBrowser.KeyOf(i.Id)));
            Assert.Equal(new[] { "studio:blue", "studio:pink" }, Items(HentaiCatalogBrowser.Studios).Select(i => i.Id));
            Assert.Equal(new[] { "studio:pink|hanime:show-2", "studio:pink|hanime:show-1" }, Items("studio:pink").Select(i => i.Id));
        }

        [Fact]
        public void Series_AreGroupedByLetter()
        {
            Assert.Equal(new[] { ("series:#", "# (1)"), ("series:A", "A (1)"), ("series:S", "S (1)") }, Items(HentaiCatalogBrowser.Series).Select(i => (i.Id, i.Name)));

            var shows = HentaiCatalogBrowser.GetItems(Catalog, _config, "series:S").Items;
            var show = Assert.Single(shows);
            Assert.Equal("show:Show", show.Id);
            Assert.Equal("https://cdn/show-1-poster.jpg", show.ImageUrl);
            Assert.Equal(LibraryLayout.AdultRating, show.OfficialRating);

            var episodes = HentaiCatalogBrowser.GetItems(Catalog, _config, show.Id).Items;
            Assert.Equal(new[] { "show:Show|hanime:show-1", "show:Show|hanime:show-2" }, episodes.Select(e => e.Id));
            Assert.Equal(new int?[] { 1, 2 }, episodes.Select(e => e.IndexNumber));
        }

        [Fact]
        public void Videos_CarryTheirMetadata()
        {
            var item = HentaiCatalogBrowser.GetItems(Catalog, _config, "show:Show").Items[0];

            Assert.Equal(ChannelItemType.Media, item.Type);
            Assert.Equal(ChannelMediaType.Video, item.MediaType);
            Assert.Equal(ChannelMediaContentType.Episode, item.ContentType);
            Assert.Equal("Show", item.SeriesName);
            Assert.Equal(LibraryLayout.AdultRating, item.OfficialRating);
            Assert.Equal("https://cdn/show-1-thumb.jpg", item.ImageUrl);
            Assert.Equal(new[] { "HD", "Plot" }, item.Genres);
            Assert.Equal(new[] { "Pink" }, item.Studios);
            Assert.Equal(9.6f, item.CommunityRating);
            Assert.Equal(2010, item.ProductionYear);
            Assert.Empty(item.MediaSources);
        }

        [Theory]
        [InlineData("latest|hanime:some-slug", "hanime:some-slug")]
        [InlineData("genre:a|b|hentaihaven:watch/a/episode-1/", "hentaihaven:watch/a/episode-1/")]
        [InlineData("latest", null)]
        [InlineData("latest|", null)]
        public void KeyOf_TakesTheLastPart(string id, string? key) => Assert.Equal(key, HentaiCatalogBrowser.KeyOf(id));

        [Fact]
        public void SeriesOfBothSources_AreOneFolder()
        {
            var haven = new HentaiVideo
            {
                Source = HentaiSource.HentaiHaven,
                Id = "watch/show/episode-3/",
                Name = "Show 3",
                SeriesName = "Show!",
                EpisodeNumber = 3,
                PageUrl = "https://haven/watch/show/episode-3/",
            };
            var catalog = Catalog.Append(haven).ToList();

            var show = Assert.Single(HentaiCatalogBrowser.GetItems(catalog, _config, "series:S").Items);
            var episodes = HentaiCatalogBrowser.GetItems(catalog, _config, show.Id).Items;

            Assert.Equal(new[] { "hanime:show-1", "hanime:show-2", "hentaihaven:watch/show/episode-3/" }, episodes.Select(e => HentaiCatalogBrowser.KeyOf(e.Id)));
            Assert.Equal("https://haven/watch/show/episode-3/", episodes[2].HomePageUrl);
        }

        [Fact]
        public void UnknownFolders_AreEmpty() => Assert.Empty(Items("nonsense"));
    }
}
