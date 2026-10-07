using Jellyfin.Plugin.HAnimeTV.Access;
using MediaBrowser.Model.Users;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public class ChannelAccessPolicyTests
    {
        private static readonly Guid Channel = Guid.NewGuid();
        private static readonly Guid Other = Guid.NewGuid();
        private static readonly Guid Third = Guid.NewGuid();

        [Fact]
        public void Allowed_UserWithAllChannels_IsLeftAlone()
        {
            var policy = new UserPolicy { EnableAllChannels = true };

            Assert.False(ChannelAccessPolicy.Apply(policy, Channel, allowed: true, [Channel, Other]));
            Assert.True(policy.EnableAllChannels);
        }

        [Fact]
        public void Allowed_UserWithSelectedChannels_GetsTheChannel()
        {
            var policy = new UserPolicy { EnableAllChannels = false, EnabledChannels = [Other] };

            Assert.True(ChannelAccessPolicy.Apply(policy, Channel, allowed: true, [Channel, Other]));
            Assert.Equal(new[] { Other, Channel }, policy.EnabledChannels);
            Assert.False(ChannelAccessPolicy.Apply(policy, Channel, allowed: true, [Channel, Other]));
        }

        [Fact]
        public void Denied_UserWithAllChannels_KeepsTheOtherChannels()
        {
            var policy = new UserPolicy { EnableAllChannels = true };

            Assert.True(ChannelAccessPolicy.Apply(policy, Channel, allowed: false, [Other, Channel, Third, Other]));
            Assert.False(policy.EnableAllChannels);
            Assert.Equal(new[] { Other, Third }, policy.EnabledChannels);
        }

        [Fact]
        public void Denied_UserWithSelectedChannels_LosesOnlyTheChannel()
        {
            var policy = new UserPolicy { EnableAllChannels = false, EnabledChannels = [Channel, Other] };

            Assert.True(ChannelAccessPolicy.Apply(policy, Channel, allowed: false, [Channel, Other, Third]));
            Assert.Equal(new[] { Other }, policy.EnabledChannels);
            Assert.False(ChannelAccessPolicy.Apply(policy, Channel, allowed: false, [Channel, Other, Third]));
        }

        [Fact]
        public void BlockedChannels_AreOnlyChangedWhenInUse()
        {
            Assert.Null(ChannelAccessPolicy.BlockedChannels([], Channel, allowed: false));
            Assert.Null(ChannelAccessPolicy.BlockedChannels([], Channel, allowed: true));
            Assert.Equal(new[] { Other, Channel }, ChannelAccessPolicy.BlockedChannels([Other], Channel, allowed: false));
            Assert.Null(ChannelAccessPolicy.BlockedChannels([Other, Channel], Channel, allowed: false));
            Assert.Equal(new[] { Other }, ChannelAccessPolicy.BlockedChannels([Other, Channel], Channel, allowed: true));
            Assert.Null(ChannelAccessPolicy.BlockedChannels([Other], Channel, allowed: true));
        }
    }
}
