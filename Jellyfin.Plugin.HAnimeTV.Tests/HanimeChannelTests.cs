using System.Net;
using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    [Collection(StaticStateCollection.Name)]
    public class HanimeChannelTests
    {
        private static readonly Guid Allowed = Guid.NewGuid();
        private static readonly Guid Denied = Guid.NewGuid();
        private readonly PluginConfiguration _config = new() { AllowedUsers = [Allowed] };
        private int _downloads;

        public HanimeChannelTests()
        {
            var paths = new Mock<IApplicationPaths>();
            paths.Setup(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
            paths.Setup(p => p.PluginsPath).Returns(Path.GetTempPath());
            var xml = new Mock<IXmlSerializer>();
            xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(_config);
            _ = new Plugin(paths.Object, xml.Object);
        }

        private HanimeChannel Create(Mock<IMediaEncoder>? encoder = null)
        {
            var handler = new CatalogHandler(() => _downloads++);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            var client = new HanimeClient(factory.Object, () => _config, NullLogger.Instance);
            return new HanimeChannel(client, (encoder ?? new Mock<IMediaEncoder>()).Object, NullLogger<HanimeChannel>.Instance);
        }

        [Fact]
        public void IsEnabledFor_OnlySelectedUsers()
        {
            var channel = Create();

            Assert.True(channel.IsEnabledFor(Allowed.ToString("N")));
            Assert.False(channel.IsEnabledFor(Denied.ToString("N")));
            Assert.False(channel.IsEnabledFor(string.Empty));
        }

        [Fact]
        public async Task GetChannelItems_RefusesOtherUsers()
        {
            var channel = Create();

            // Not an empty result, which Jellyfin would take as an emptied folder and delete its items
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => channel.GetChannelItems(new InternalChannelItemQuery { UserId = Denied, FolderId = CatalogBrowser.Latest }, CancellationToken.None));
            Assert.Equal(0, _downloads);

            var allowed = await channel.GetChannelItems(new InternalChannelItemQuery { UserId = Allowed, FolderId = CatalogBrowser.Latest }, CancellationToken.None);
            Assert.Equal("latest|video-1", Assert.Single(allowed.Items).Id);
        }

        [Fact]
        public async Task GetLatestMedia_IsEmptyForOtherUsers()
        {
            var channel = Create();

            Assert.Empty(await channel.GetLatestMedia(new ChannelLatestMediaSearch { UserId = Denied.ToString("N") }, CancellationToken.None));
            Assert.Single(await channel.GetLatestMedia(new ChannelLatestMediaSearch { UserId = Allowed.ToString("N") }, CancellationToken.None));
        }

        [Fact]
        public void GetCacheKey_ChangesWithTheSettings()
        {
            var channel = Create();
            var key = channel.GetCacheKey(Allowed.ToString("N"));

            Assert.NotEqual(key, channel.GetCacheKey(Denied.ToString("N")));
            _config.HiddenTags = ["scat"];
            Assert.NotEqual(key, channel.GetCacheKey(Allowed.ToString("N")));
        }

        [Fact]
        public void CreateMediaSource_IsRemuxedFromHls()
        {
            var source = HanimeChannel.CreateMediaSource("video-1", new HanimeStream("https://hanime.tv/hls/x.m3u8", 480, false));

            Assert.Equal(MediaProtocol.Http, source.Protocol);
            Assert.Equal("hls", source.Container);
            Assert.Equal("480p", source.Name);
            Assert.False(source.SupportsDirectPlay);
            Assert.True(source.SupportsDirectStream);
            Assert.Equal(HanimeClient.Referer, source.RequiredHttpHeaders["Referer"]);
            var video = source.MediaStreams.Single(s => s.Type == MediaStreamType.Video);
            Assert.Equal((854, 480), (video.Width!.Value, video.Height!.Value));
            Assert.NotEqual(source.Id, HanimeChannel.CreateMediaSource("video-1", new HanimeStream("https://hanime.tv/hls/y.m3u8", 720, false)).Id);
        }

        [Fact]
        public async Task GetChannelItemMediaInfo_UsesTheProbe()
        {
            var encoder = new Mock<IMediaEncoder>();
            encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaInfo
                {
                    RunTimeTicks = TimeSpan.FromMinutes(20).Ticks,
                    MediaStreams =
                    [
                        new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Height = 720, Width = 1280 },
                        new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
                    ],
                });

            var sources = (await Create(encoder).GetChannelItemMediaInfo("genre:hd|video-1", CancellationToken.None)).ToList();

            var source = Assert.Single(sources);
            Assert.Equal(TimeSpan.FromMinutes(20).Ticks, source.RunTimeTicks);
            Assert.Equal("hevc", source.MediaStreams[0].Codec);
            encoder.Verify(e => e.GetMediaInfo(It.Is<MediaInfoRequest>(r => r.MediaSource.Path == "https://hanime.tv/hls/video-1.m3u8"), It.IsAny<CancellationToken>()));
        }

        [Fact]
        public async Task GetChannelItemMediaInfo_AssumesH264WhenTheProbeFails()
        {
            var encoder = new Mock<IMediaEncoder>();
            encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("ffprobe failed"));

            var source = Assert.Single(await Create(encoder).GetChannelItemMediaInfo("latest|video-1", CancellationToken.None));

            Assert.Equal("h264", source.MediaStreams[0].Codec);
            Assert.Null(source.RunTimeTicks);
        }

        private sealed class CatalogHandler : HttpMessageHandler
        {
            private readonly Action _onCatalog;

            public CatalogHandler(Action onCatalog) => _onCatalog = onCatalog;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get)
                {
                    _onCatalog();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""[{ "slug": "video-1", "name": "Video 1", "created_at_unix": 1700000000 }]"""),
                    });
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.Add("X-Token", HanimeCrypto.Seal(System.Text.Json.Nodes.JsonNode.Parse("""{ "sources": [{ "kind": "normal", "src": "/hls/video-1.m3u8", "height": 720 }] }""")!));
                return Task.FromResult(response);
            }
        }
    }
}
