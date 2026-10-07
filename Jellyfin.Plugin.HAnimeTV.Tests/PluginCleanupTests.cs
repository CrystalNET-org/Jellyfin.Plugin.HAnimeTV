using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class PluginCleanupTests : IDisposable
    {
        private static readonly Guid Id = Guid.Parse("1029189A-8A81-4419-8B08-78EB68071A0D");
        private static readonly Version Own = new(0, 3, 1, 0);

        private readonly string _plugins = Path.Combine(Path.GetTempPath(), "plugins-test-" + Guid.NewGuid().ToString("N"));

        public PluginCleanupTests() => Directory.CreateDirectory(_plugins);

        public void Dispose() => Directory.Delete(_plugins, recursive: true);

        [Fact]
        public void RemovesOlderVersionsUnderTheOldName()
        {
            var own = Plugin("Adult Media_0.3.1.0", Id, "Adult Media", "0.3.1.0");
            var old = Plugin("hanime.tv_0.1.5.0", Id, "hanime.tv", "0.1.5.0");

            var removed = Run(own);

            Assert.Equal([old], removed);
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(own));
        }

        [Fact]
        public void LeavesOtherPluginsNewerVersionsAndTheSameNameAlone()
        {
            var own = Plugin("Adult Media_0.3.1.0", Id, "Adult Media", "0.3.1.0");
            var other = Plugin("Other_1.0.0.0", Guid.NewGuid(), "Other", "0.0.1.0");
            var newer = Plugin("hanime.tv_9.0.0.0", Id, "hanime.tv", "9.0.0.0");
            var sameName = Plugin("Adult Media_0.3.0.0", Id, "Adult Media", "0.3.0.0");
            var noManifest = Directory.CreateDirectory(Path.Combine(_plugins, "Manual")).FullName;
            var broken = Directory.CreateDirectory(Path.Combine(_plugins, "Broken")).FullName;
            File.WriteAllText(Path.Combine(broken, "meta.json"), "{ not json");

            Assert.Empty(Run(own));
            Assert.All([own, other, newer, sameName, noManifest, broken], f => Assert.True(Directory.Exists(f)));
        }

        [Fact]
        public void MissingPluginsFolderIsNoError()
        {
            Assert.Empty(PluginCleanup.RemoveOlderVersions(Path.Combine(_plugins, "missing"), Id, "Adult Media", _plugins, Own, NullLogger.Instance));
        }

        private IReadOnlyList<string> Run(string ownFolder) =>
            PluginCleanup.RemoveOlderVersions(_plugins, Id, "Adult Media", ownFolder, Own, NullLogger.Instance);

        private string Plugin(string folder, Guid id, string name, string version)
        {
            var path = Directory.CreateDirectory(Path.Combine(_plugins, folder)).FullName;
            File.WriteAllText(Path.Combine(path, "Jellyfin.Plugin.HAnimeTV.dll"), string.Empty);
            File.WriteAllText(
                Path.Combine(path, "meta.json"),
                new JsonObject { ["guid"] = id.ToString(), ["name"] = name, ["version"] = version, ["status"] = "Active" }.ToJsonString());
            return path;
        }
    }
}
