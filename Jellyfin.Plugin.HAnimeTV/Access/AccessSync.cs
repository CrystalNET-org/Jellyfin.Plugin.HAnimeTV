using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Library;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// A channel or library of a provider, and who may see it.
    /// </summary>
    /// <param name="Name">The provider's name, for logs and the settings page.</param>
    /// <param name="IsChannel">Whether it is a channel (else a library).</param>
    /// <param name="Id">Its id in Jellyfin.</param>
    /// <param name="Grants">Whether the settings grant it to a user.</param>
    public sealed record AccessSurface(string Name, bool IsChannel, Guid Id, Func<PluginConfiguration, Guid, bool> Grants);

    /// <summary>
    /// Makes Jellyfin's user policies grant each provider's channel and library to the users
    /// selected for it, in the mode it is in, and to nobody else. Jellyfin itself then hides
    /// them, and their videos, from everyone else.
    /// </summary>
    public sealed class AccessSync
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryLocator _locator;
        private readonly IEnumerable<IChannel> _channels;
        private readonly ILogger<AccessSync> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public AccessSync(IUserManager userManager, ILibraryManager libraryManager, LibraryLocator locator, IEnumerable<IChannel> channels, ILogger<AccessSync> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _locator = locator;
            _channels = channels;
            _logger = logger;
        }

        public DateTimeOffset? LastSync { get; private set; }

        /// <summary>
        /// Gets the users whose access was changed by the last check.
        /// </summary>
        public IReadOnlyList<string> LastChanged { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Gets the channels and the library the plugin controls.
        /// </summary>
        public IReadOnlyList<AccessSurface> Surfaces()
        {
            var surfaces = _channels.OfType<ProviderChannel>()
                .Select(c => new AccessSurface(c.Name, true, ChannelId(c.Name), (config, user) => c.Source.Settings(config).Grants(user, ProviderMode.Channel)))
                .ToList();
            if (_locator.Find() is { } libraryId)
            {
                surfaces.Add(new AccessSurface(HentaiChannelSource.ProviderName, false, libraryId, (config, user) => config.Hentai.Grants(user, ProviderMode.Library)));
            }

            return surfaces;
        }

        /// <summary>
        /// Gets a channel's id in Jellyfin, which Jellyfin derives from its name.
        /// </summary>
        public Guid ChannelId(string channelName) => _libraryManager.GetNewItemId("Channel " + channelName, typeof(MediaBrowser.Controller.Channels.Channel));

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
                var surfaces = Surfaces();
                var changed = new List<string>();
                foreach (var user in _userManager.GetUsers().ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await SyncUserInternalAsync(user, config, surfaces).ConfigureAwait(false))
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
                    await SyncUserInternalAsync(user, config, Surfaces()).ConfigureAwait(false);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets whether Jellyfin's policy currently grants the user the channel or library.
        /// </summary>
        public static bool PolicyGrants(User user, AccessSurface surface)
        {
            var (blockedKind, allKind, enabledKind) = surface.IsChannel
                ? (PreferenceKind.BlockedChannels, PermissionKind.EnableAllChannels, PreferenceKind.EnabledChannels)
                : (PreferenceKind.BlockedMediaFolders, PermissionKind.EnableAllFolders, PreferenceKind.EnabledFolders);
            var blocked = user.GetPreferenceValues<Guid>(blockedKind);
            return blocked.Length != 0
                ? !blocked.Contains(surface.Id)
                : user.HasPermission(allKind) || user.GetPreferenceValues<Guid>(enabledKind).Contains(surface.Id);
        }

        private async Task<bool> SyncUserInternalAsync(User user, PluginConfiguration config, IReadOnlyList<AccessSurface> surfaces)
        {
            var changed = new List<string>();
            try
            {
                // Legacy lists of blocked channels and libraries, which replace the policy's selection when not empty
                var blockedChannels = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
                var blockedFolders = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
                var preferencesChanged = false;
                foreach (var surface in surfaces)
                {
                    var allowed = surface.Grants(config, user.Id);
                    if (surface.IsChannel && ChannelAccessPolicy.BlockedChannels(blockedChannels, surface.Id, allowed) is { } channels)
                    {
                        blockedChannels = channels;
                        preferencesChanged = true;
                        changed.Add(Describe(surface, allowed));
                    }
                    else if (!surface.IsChannel && LibraryAccessPolicy.BlockedLibraries(blockedFolders, surface.Id, allowed) is { } folders)
                    {
                        blockedFolders = folders;
                        preferencesChanged = true;
                        changed.Add(Describe(surface, allowed));
                    }
                }

                if (preferencesChanged)
                {
                    user.SetPreference(PreferenceKind.BlockedChannels, blockedChannels);
                    user.SetPreference(PreferenceKind.BlockedMediaFolders, blockedFolders);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }

                var policy = _userManager.GetUserDto(user).Policy;
                var allChannels = _channels.Select(c => ChannelId(c.Name)).ToList();
                var allLibraries = _libraryManager.GetVirtualFolders()
                    .Select(f => Guid.TryParse(f.ItemId, CultureInfo.InvariantCulture, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .ToList();
                var policyChanged = false;
                foreach (var surface in surfaces)
                {
                    var allowed = surface.Grants(config, user.Id);
                    var applies = surface.IsChannel ? blockedChannels.Length == 0 : blockedFolders.Length == 0;
                    if (applies && (surface.IsChannel
                        ? ChannelAccessPolicy.Apply(policy, surface.Id, allowed, allChannels)
                        : LibraryAccessPolicy.Apply(policy, surface.Id, allowed, allLibraries)))
                    {
                        policyChanged = true;
                        changed.Add(Describe(surface, allowed));
                    }
                }

                if (policyChanged)
                {
                    await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                }

                if (changed.Count > 0)
                {
                    _logger.LogInformation("Adult Media: {User}: {Changes}", user.Username, string.Join(", ", changed.Distinct()));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adult Media: could not update the channel and library access of {User}", user.Username);
            }

            return changed.Count > 0;
        }

        private static string Describe(AccessSurface surface, bool allowed) =>
            (allowed ? "granted the " : "revoked the ") + surface.Name + (surface.IsChannel ? " channel" : " library");
    }
}
