using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class CatalogBrowserTests
    {
        private readonly PluginConfiguration _config = new();

        private static readonly HanimeVideo[] Catalog =
        [
            Video("show-1", "Show 1", "Pink", ["hd", "plot"], views: 50, created: 1, released: 2010),
            Video("show-2", "Show 2", "Pink", ["hd"], views: 10, created: 5, released: 2012),
            Video("other", "Another One", "Blue", ["milf"], views: 99, created: 3, released: 2020, censored: true),
            Video("numeric", "1LDK", null, ["hd", "scat"], views: 1, created: 4, released: 2001),
        ];

        private static HanimeVideo Video(string slug, string name, string? brand, string[] tags, long views, int created, int released, bool censored = false) => new()
        {
            Slug = slug,
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
            CatalogBrowser.GetItems(Catalog, _config, folder).Items.Select(i => new ChannelItemInfoView(i.Id, i.Name, i.Type)).ToList();

        private sealed record ChannelItemInfoView(string Id, string Name, ChannelItemType Type);

        [Fact]
        public void Root_ListsTheCategories()
        {
            var root = Items(null);

            Assert.All(root, i => Assert.Equal(ChannelItemType.Folder, i.Type));
            Assert.Equal(
                new[] { CatalogBrowser.Latest, CatalogBrowser.Released, CatalogBrowser.Popular, CatalogBrowser.Liked, CatalogBrowser.Series, CatalogBrowser.Genres, CatalogBrowser.Studios },
                root.Select(i => i.Id));
        }

        [Fact]
        public void Categories_AreSortedAndLimited()
        {
            Assert.Equal(new[] { "latest|show-2", "latest|numeric", "latest|other", "latest|show-1" }, Items(CatalogBrowser.Latest).Select(i => i.Id));
            Assert.Equal(new[] { "other", "show-1", "show-2", "numeric" }, Items(CatalogBrowser.Popular).Select(i => CatalogBrowser.SlugOf(i.Id)));
            Assert.Equal(new[] { "other", "show-2", "show-1", "numeric" }, Items(CatalogBrowser.Released).Select(i => CatalogBrowser.SlugOf(i.Id)));

            _config.MaxItemsPerFolder = 1;
            // At least 10, whatever the setting says
            Assert.Equal(4, Items(CatalogBrowser.Latest).Count);
        }

        [Fact]
        public void HiddenTagsAndCensoredVideos_AreLeftOutEverywhere()
        {
            _config.HiddenTags = [" SCAT ", ""];
            _config.HideCensored = true;

            Assert.Equal(new[] { "show-2", "show-1" }, Items(CatalogBrowser.Latest).Select(i => CatalogBrowser.SlugOf(i.Id)));
            Assert.DoesNotContain(Items(CatalogBrowser.Genres), i => i.Name.StartsWith("Scat", StringComparison.Ordinal));
            Assert.Empty(Items("genre:scat"));
            Assert.Empty(Items("studio:blue"));
        }

        [Fact]
        public void GenresAndStudios_GroupTheVideos()
        {
            Assert.Equal(
                new[] { ("genre:hd", "HD (3)"), ("genre:milf", "Milf (1)"), ("genre:plot", "Plot (1)"), ("genre:scat", "Scat (1)") },
                Items(CatalogBrowser.Genres).Select(i => (i.Id, i.Name)));
            Assert.Equal(new[] { "show-2", "show-1", "numeric" }, Items("genre:hd").Select(i => CatalogBrowser.SlugOf(i.Id)));
            Assert.Equal(new[] { "studio:blue", "studio:pink" }, Items(CatalogBrowser.Studios).Select(i => i.Id));
            Assert.Equal(new[] { "studio:pink|show-2", "studio:pink|show-1" }, Items("studio:pink").Select(i => i.Id));
        }

        [Fact]
        public void Series_AreGroupedByLetter()
        {
            Assert.Equal(new[] { ("series:#", "# (1)"), ("series:A", "A (1)"), ("series:S", "S (1)") }, Items(CatalogBrowser.Series).Select(i => (i.Id, i.Name)));

            var shows = CatalogBrowser.GetItems(Catalog, _config, "series:S").Items;
            var show = Assert.Single(shows);
            Assert.Equal("show:Show", show.Id);
            Assert.Equal("https://cdn/show-1-poster.jpg", show.ImageUrl);
            Assert.Equal(CatalogBrowser.AdultRating, show.OfficialRating);

            var episodes = CatalogBrowser.GetItems(Catalog, _config, show.Id).Items;
            Assert.Equal(new[] { "show:Show|show-1", "show:Show|show-2" }, episodes.Select(e => e.Id));
            Assert.Equal(new int?[] { 1, 2 }, episodes.Select(e => e.IndexNumber));
        }

        [Fact]
        public void Videos_CarryTheirMetadata()
        {
            var item = CatalogBrowser.GetItems(Catalog, _config, "show:Show").Items[0];

            Assert.Equal(ChannelItemType.Media, item.Type);
            Assert.Equal(ChannelMediaType.Video, item.MediaType);
            Assert.Equal(ChannelMediaContentType.Episode, item.ContentType);
            Assert.Equal("Show", item.SeriesName);
            Assert.Equal(CatalogBrowser.AdultRating, item.OfficialRating);
            Assert.Equal("https://cdn/show-1-thumb.jpg", item.ImageUrl);
            Assert.Equal(new[] { "HD", "Plot" }, item.Genres);
            Assert.Equal(new[] { "Pink" }, item.Studios);
            Assert.Equal(9.6f, item.CommunityRating);
            Assert.Equal(2010, item.ProductionYear);
            Assert.Empty(item.MediaSources);
        }

        [Theory]
        [InlineData("latest|some-slug", "some-slug")]
        [InlineData("genre:a|b|slug", "slug")]
        [InlineData("latest", null)]
        [InlineData("latest|", null)]
        public void SlugOf_TakesTheLastPart(string id, string? slug) => Assert.Equal(slug, CatalogBrowser.SlugOf(id));

        [Fact]
        public void UnknownFolders_AreEmpty() => Assert.Empty(Items("nonsense"));
    }
}
