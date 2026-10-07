using Jellyfin.Plugin.HAnimeTV.Access;
using MediaBrowser.Model.Users;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class LibraryAccessPolicyTests
    {
        private static readonly Guid Library = Guid.NewGuid();
        private static readonly Guid Other = Guid.NewGuid();
        private static readonly Guid Third = Guid.NewGuid();

        [Fact]
        public void Allowed_UserWithAllLibraries_IsLeftAlone()
        {
            var policy = new UserPolicy { EnableAllFolders = true };

            Assert.False(LibraryAccessPolicy.Apply(policy, Library, allowed: true, [Library, Other]));
            Assert.True(policy.EnableAllFolders);
        }

        [Fact]
        public void Allowed_UserWithSelectedLibraries_GetsTheLibrary()
        {
            var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = [Other] };

            Assert.True(LibraryAccessPolicy.Apply(policy, Library, allowed: true, [Library, Other]));
            Assert.Equal(new[] { Other, Library }, policy.EnabledFolders);
            Assert.False(LibraryAccessPolicy.Apply(policy, Library, allowed: true, [Library, Other]));
        }

        [Fact]
        public void Denied_UserWithAllLibraries_KeepsTheOtherLibraries()
        {
            var policy = new UserPolicy { EnableAllFolders = true };

            Assert.True(LibraryAccessPolicy.Apply(policy, Library, allowed: false, [Other, Library, Third, Other]));
            Assert.False(policy.EnableAllFolders);
            Assert.Equal(new[] { Other, Third }, policy.EnabledFolders);
        }

        [Fact]
        public void Denied_UserWithSelectedLibraries_LosesOnlyTheLibrary()
        {
            var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = [Library, Other] };

            Assert.True(LibraryAccessPolicy.Apply(policy, Library, allowed: false, [Library, Other, Third]));
            Assert.Equal(new[] { Other }, policy.EnabledFolders);
            Assert.False(LibraryAccessPolicy.Apply(policy, Library, allowed: false, [Library, Other, Third]));
        }

        [Fact]
        public void BlockedLibraries_AreOnlyChangedWhenInUse()
        {
            Assert.Null(LibraryAccessPolicy.BlockedLibraries([], Library, allowed: false));
            Assert.Null(LibraryAccessPolicy.BlockedLibraries([], Library, allowed: true));
            Assert.Equal(new[] { Other, Library }, LibraryAccessPolicy.BlockedLibraries([Other], Library, allowed: false));
            Assert.Null(LibraryAccessPolicy.BlockedLibraries([Other, Library], Library, allowed: false));
            Assert.Equal(new[] { Other }, LibraryAccessPolicy.BlockedLibraries([Other, Library], Library, allowed: true));
            Assert.Null(LibraryAccessPolicy.BlockedLibraries([Other], Library, allowed: true));
        }
    }
}
