using System.Xml.Serialization;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class ConfigurationTests
    {
        private static readonly Guid User = Guid.Parse("4f7e1c2a-0000-4000-8000-000000000001");

        private static PluginConfiguration Read(string xml)
        {
            using var reader = new StringReader(xml);
            return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
        }

        private static string Write(PluginConfiguration config)
        {
            using var writer = new StringWriter();
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
            return writer.ToString();
        }

        [Fact]
        public void SettingsOfVersion01_MoveIntoTheHentaiProvider()
        {
            var config = Read($"""
                <?xml version="1.0" encoding="utf-8"?>
                <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <AllowedUsers><guid>{User}</guid></AllowedUsers>
                  <EnforceAccess>true</EnforceAccess>
                  <HiddenTags><string>scat</string></HiddenTags>
                  <HideCensored>true</HideCensored>
                  <Email>a@b.c</Email>
                  <Password>secret</Password>
                  <LibraryPath>/media/hanime</LibraryPath>
                  <CreateLibrary>false</CreateLibrary>
                  <LibraryName>hanime.tv</LibraryName>
                  <StreamBaseUrl>https://jf.test</StreamBaseUrl>
                  <StreamToken>token</StreamToken>
                  <CatalogCacheHours>12</CatalogCacheHours>
                </PluginConfiguration>
                """.Trim());

            Assert.True(config.MigrateLegacySettings());

            Assert.Equal(ProviderMode.Library, config.Hentai.Mode);
            Assert.Equal(new[] { User }, config.Hentai.AllowedUsers);
            Assert.Equal(new[] { "scat" }, config.Hentai.HiddenTags);
            Assert.True(config.Hentai.HideCensored);
            Assert.Equal("a@b.c", config.Hentai.Email);
            Assert.Equal("secret", config.Hentai.Password);
            Assert.Equal("/media/hanime", config.Hentai.LibraryPath);
            Assert.False(config.Hentai.CreateLibrary);
            Assert.Equal("hanime.tv", config.Hentai.LibraryName);
            Assert.Equal(12, config.Hentai.CatalogCacheHours);
            Assert.True(config.Hentai.HanimeEnabled);
            Assert.True(config.Hentai.HentaiHavenEnabled);
            Assert.Equal("https://jf.test", config.StreamBaseUrl);
            Assert.Equal(ProviderMode.Off, config.Pornhub.Mode);
            Assert.False(config.MigrateLegacySettings());

            var written = Write(config);
            Assert.DoesNotContain("<Email>a@b.c</Email>\n  <Password>", written, StringComparison.Ordinal);
            var again = Read(written);
            Assert.False(again.MigrateLegacySettings());
            Assert.Equal(new[] { User }, again.Hentai.AllowedUsers);
            Assert.Equal("a@b.c", again.Hentai.Email);
        }

        [Fact]
        public void NewSettings_HaveNoUsers()
        {
            var config = new PluginConfiguration();

            Assert.False(config.MigrateLegacySettings());
            Assert.Equal(ProviderMode.Library, config.Hentai.Mode);
            Assert.Equal(ProviderMode.Off, config.Pornhub.Mode);
            Assert.False(config.Hentai.Grants(User, ProviderMode.Library));
            config.Hentai.AllowedUsers = [User];
            Assert.True(config.Hentai.Grants(User, ProviderMode.Library));
            Assert.False(config.Hentai.Grants(User, ProviderMode.Channel));
        }

        [Theory]
        [InlineData("watch/some-show/episode-1/")]
        [InlineData("?p=123&x=ü")]
        public void StreamIds_SurviveTheLink(string id)
        {
            var encoded = StreamLinks.EncodeId(id);

            Assert.DoesNotContain('/', encoded);
            Assert.DoesNotContain('+', encoded);
            Assert.Equal(id, StreamLinks.DecodeId(encoded));
        }
    }
}
