using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    /// <summary>
    /// Tests that set <see cref="Plugin.Instance"/> run one after another.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class StaticStateCollection
    {
        public const string Name = "Static state";
    }
}
