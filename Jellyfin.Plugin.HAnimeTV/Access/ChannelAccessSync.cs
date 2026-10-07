using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Makes Jellyfin's user policies grant the channel to the selected users only, so that
    /// Jellyfin itself refuses its videos to everyone else: in searches, by id and for playback.
    /// </summary>
    public sealed class ChannelAccessSync
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IEnumerable<IChannel> _channels;
        private readonly ILogger<ChannelAccessSync> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public ChannelAccessSync(IUserManager userManager, ILibraryManager libraryManager, IEnumerable<IChannel> channels, ILogger<ChannelAccessSync> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _channels = channels;
            _logger = logger;
        }

        /// <summary>
        /// Gets the channel's id in Jellyfin.
        /// </summary>
        public Guid ChannelId => HanimeChannel.InternalId(_libraryManager, HanimeChannel.ChannelName);

        /// <summary>
        /// Gets when the policies were last checked, or null.
        /// </summary>
        public DateTimeOffset? LastSync { get; private set; }

        /// <summary>
        /// Gets the users whose access was changed by the last check.
        /// </summary>
        public IReadOnlyList<string> LastChanged { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Checks all users' access, unless enforcing it is off.
        /// </summary>
        /// <returns>The names of the users whose access changed.</returns>
        public async Task<IReadOnlyList<string>> SyncAllAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnforceAccess)
            {
                return Array.Empty<string>();
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var changed = new List<string>();
                foreach (var user in _userManager.GetUsers().ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await SyncUserInternalAsync(user, config.IsAllowed(user.Id)).ConfigureAwait(false))
                    {
                        changed.Add(user.Username);
                    }
                }

                LastSync = DateTimeOffset.UtcNow;
                LastChanged = changed;
                return changed;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Checks one user's access, unless enforcing it is off.
        /// </summary>
        public async Task SyncUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnforceAccess)
            {
                return;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_userManager.GetUserById(userId) is { } user)
                {
                    await SyncUserInternalAsync(user, config.IsAllowed(user.Id)).ConfigureAwait(false);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<bool> SyncUserInternalAsync(User user, bool allowed)
        {
            var channelId = ChannelId;
            var changed = false;
            try
            {
                if (ChannelAccessPolicy.BlockedChannels(user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels), channelId, allowed) is { } blocked)
                {
                    user.SetPreference(PreferenceKind.BlockedChannels, blocked);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    changed = true;
                }

                // A list of blocked channels replaces the policy's channel selection
                if (user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels).Length == 0)
                {
                    var policy = _userManager.GetUserDto(user).Policy;
                    var others = _channels.Select(c => HanimeChannel.InternalId(_libraryManager, c.Name));
                    if (ChannelAccessPolicy.Apply(policy, channelId, allowed, others))
                    {
                        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                        changed = true;
                    }
                }

                if (changed)
                {
                    _logger.LogInformation(
                        "hanime.tv: {Action} the channel for {User} in the user's policy",
                        allowed ? "granted" : "revoked",
                        user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "hanime.tv: could not update the channel access of {User}", user.Username);
            }

            return changed;
        }
    }
}
