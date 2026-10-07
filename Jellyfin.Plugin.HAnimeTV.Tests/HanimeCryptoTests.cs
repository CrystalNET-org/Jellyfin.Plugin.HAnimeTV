using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class HanimeCryptoTests
    {
        // Sealed by Node's crypto the way hanime.tv's player and other clients do
        private const string NodeToken = "eyJ2IjoxLCJhbGciOiJBRVMtMjU2LUdDTSIsIml2IjoiQndjSEJ3Y0hCd2NIQndjSCIsInRhZyI6IlVwTlFodGNTamZxQmlvRzRXQmpOdUEiLCJkYXRhIjoiM2s5dkpPNFFDZDVxNlYybVZfV3h1Mmo5b2Y3YXoyUlp4T19JTnpDWnV0RFJqcGppc1BVMFhGckNvYTduaUZhZjFUZUg4N0lVZWNYNnR5cGN2aEl4WnZ1RDhOZXpBaGVYV0RycE1RX2xjZXNmazBPcXpKMDF2VHZ2RHZhWkdtU2ljZFRKRUE4VUFwZExzZDZ1NHA1bGN3cjRtcDVrdDVfSXhvd2s3WHByV2pWS2xSajV4ZU0ifQ";

        [Fact]
        public void Signatures_MatchHanimeTvs()
        {
            Assert.Equal("bc2348fd32ead4c3d79e87ad1abecd3759e573bf2f31024f7313a2fe054703ee", HanimeCrypto.WebSignature(1700000000));
            Assert.Equal("7dbfd465f97a94cbfe9b00940f70e135c212c5d3733afa87d24596d742ff8447", HanimeCrypto.AppSignature(1700000000));
        }

        [Fact]
        public void Open_ReadsTokensSealedElsewhere()
        {
            var payload = HanimeCrypto.Open(NodeToken);

            var sources = Assert.IsType<JsonArray>(payload["sources"]);
            Assert.Equal("/hls/test-720.m3u8", sources[0]!["src"]!.GetValue<string>());
            Assert.Equal(1080, sources[1]!["height"]!.GetValue<int>());
        }

        [Fact]
        public void Seal_RoundTrips()
        {
            var token = HanimeCrypto.Seal(new JsonObject { ["directive"] = "htv_player_handshake", ["slug"] = "some-video-1" });

            Assert.DoesNotContain('=', token);
            Assert.DoesNotContain('+', token);
            Assert.Equal("some-video-1", HanimeCrypto.Open(token)["slug"]!.GetValue<string>());
        }

        [Fact]
        public void Open_RejectsTamperedTokens()
        {
            var envelope = JsonNode.Parse(HanimeCrypto.FromBase64Url(HanimeCrypto.Seal(new JsonObject { ["a"] = 1 })))!;
            var data = HanimeCrypto.FromBase64Url(envelope["data"]!.GetValue<string>());
            data[0] ^= 1;
            envelope["data"] = HanimeCrypto.Base64Url(data);

            Assert.ThrowsAny<CryptographicException>(() => HanimeCrypto.Open(HanimeCrypto.Base64Url(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()))));
            Assert.Throws<FormatException>(() => HanimeCrypto.Open("not a token!"));
        }
    }
}
