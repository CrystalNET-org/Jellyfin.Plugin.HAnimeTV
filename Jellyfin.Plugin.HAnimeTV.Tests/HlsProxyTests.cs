using Jellyfin.Plugin.HAnimeTV.Streaming;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HlsProxyTests
    {
        private const string Token = "secret";

        [Fact]
        public void Rewrite_SendsEveryUriThroughTheProxy()
        {
            const string Playlist = "#EXTM3U\r\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x1\n#EXTINF:2.0,\nseg-1.ts\n#EXTINF:2.0,\nhttps://cdn.example/seg-2.ts?x=1\n#EXT-X-ENDLIST\n";
            var upstream = new Uri("https://hanime.tv/hls/v/index.m3u8");

            var rewritten = HlsProxy.Rewrite(Playlist, upstream, uri => "<" + uri.AbsoluteUri + ">");

            Assert.Equal(
                "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"<https://hanime.tv/hls/v/key.bin>\",IV=0x1\n#EXTINF:2.0,\n<https://hanime.tv/hls/v/seg-1.ts>\n#EXTINF:2.0,\n<https://cdn.example/seg-2.ts?x=1>\n#EXT-X-ENDLIST\n",
                rewritten);
        }

        [Fact]
        public void Links_EndWithTheFileName_AndResolveToTheirUrl()
        {
            var upstream = new Uri("https://hanime.tv/hls/v/segment000.ts?sig=abc");

            var link = HlsProxy.Link(upstream, Token, depth: 0);
            var nested = HlsProxy.Link(upstream, Token, depth: HlsProxy.ProxyDepth);
            var parts = link.Split('/');

            // ffmpeg reads HLS segments only from URLs ending with a media extension
            Assert.EndsWith("/segment000.ts", link);
            Assert.StartsWith("proxy/secret/", link);
            Assert.StartsWith("../../../../proxy/", nested);
            Assert.Equal(upstream, HlsProxy.Resolve(parts[3], parts[2], Token));
        }

        [Fact]
        public void Resolve_RefusesForgedLinks()
        {
            var parts = HlsProxy.Link(new Uri("https://hanime.tv/a.ts"), Token, 0).Split('/');
            var other = HlsProxy.Link(new Uri("https://evil.example/a.ts"), Token, 0).Split('/');

            Assert.Null(HlsProxy.Resolve(other[3], parts[2], Token));
            Assert.Null(HlsProxy.Resolve(parts[3], parts[2], "other token"));
            Assert.Null(HlsProxy.Resolve("!!!", parts[2], Token));
        }

        [Theory]
        [InlineData("secret", "secret", true)]
        [InlineData("wrong", "secret", false)]
        [InlineData(null, "secret", false)]
        [InlineData("", "", false)]
        public void IsValidToken_NeedsTheConfiguredToken(string? given, string expected, bool valid) =>
            Assert.Equal(valid, HlsProxy.IsValidToken(given, expected));

        [Theory]
        [InlineData("application/vnd.apple.mpegurl", "https://h/x", true)]
        [InlineData("application/octet-stream", "https://h/x.m3u8", true)]
        [InlineData("video/mp2t", "https://h/x.ts", false)]
        public void IsPlaylist_ByTypeOrExtension(string type, string url, bool playlist) =>
            Assert.Equal(playlist, HlsProxy.IsPlaylist(type, new Uri(url)));
    }
}
