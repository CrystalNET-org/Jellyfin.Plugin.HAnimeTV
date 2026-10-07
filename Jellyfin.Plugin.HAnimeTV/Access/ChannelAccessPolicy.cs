using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Brings a user's channel access in line with the plugin's user selection.
    /// </summary>
    /// <remarks>
    /// Jellyfin shows a channel and its videos to a user whose policy has "all channels" or
    /// lists the channel (Dashboard → Users → Access). A legacy list of blocked channels, if
    /// not empty, overrides both; nothing in the dashboard sets it any more.
    /// </remarks>
    internal static class ChannelAccessPolicy
    {
        /// <summary>
        /// Gets the blocked channels the user needs, or null if they are right already.
        /// </summary>
        public static Guid[]? BlockedChannels(Guid[] blocked, Guid channelId, bool allowed)
        {
            if (blocked.Length == 0)
            {
                // Not in use, and must stay so: once not empty, it replaces the user's channel
                // selection and would grant every other channel
                return null;
            }

            var contains = blocked.Contains(channelId);
            if (allowed && contains)
            {
                return blocked.Where(id => id != channelId).ToArray();
            }

            if (!allowed && !contains)
            {
                return [.. blocked, channelId];
            }

            return null;
        }

        /// <summary>
        /// Changes the policy's channel access so that it grants or denies the channel; the
        /// user's access to other channels stays the same.
        /// </summary>
        /// <param name="policy">The user's policy, changed in place.</param>
        /// <param name="channelId">The channel.</param>
        /// <param name="allowed">Whether the user may access the channel.</param>
        /// <param name="otherChannelIds">All other channels, which a user with access to all
        /// channels keeps access to.</param>
        /// <returns>Whether the policy changed.</returns>
        public static bool Apply(UserPolicy policy, Guid channelId, bool allowed, IEnumerable<Guid> otherChannelIds)
        {
            var enabled = policy.EnabledChannels ?? Array.Empty<Guid>();
            if (allowed)
            {
                if (policy.EnableAllChannels || enabled.Contains(channelId))
                {
                    return false;
                }

                policy.EnabledChannels = [.. enabled, channelId];
                return true;
            }

            if (policy.EnableAllChannels)
            {
                policy.EnableAllChannels = false;
                policy.EnabledChannels = otherChannelIds.Where(id => id != channelId).Distinct().ToArray();
                return true;
            }

            if (enabled.Contains(channelId))
            {
                policy.EnabledChannels = enabled.Where(id => id != channelId).ToArray();
                return true;
            }

            return false;
        }
    }
}
