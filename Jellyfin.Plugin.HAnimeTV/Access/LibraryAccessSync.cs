using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HAnimeTV.Library;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Makes Jellyfin's user policies grant the library to the selected users only, so that
    /// Jellyfin itself hides it and its videos from everyone else.
    /// </summary>
    public sealed class LibraryAccessSync
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryLocator _locator;
        private readonly ILogger<LibraryAccessSync> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public LibraryAccessSync(IUserManager userManager, ILibraryManager libraryManager, LibraryLocator locator, ILogger<LibraryAccessSync> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _locator = locator;
            _logger = logger;
        }

        /// <summary>
        /// Gets when the policies were last checked, or null.
        /// </summary>
        public DateTimeOffset? LastSync { get; private set; }

        /// <summary>
        /// Gets the users whose access was changed by the last check.
        /// </summary>
        public IReadOnlyList<string> LastChanged { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Checks all users' access, unless enforcing it is off or there is no library yet.
        /// </summary>
        /// <returns>The names of the users whose access changed.</returns>
        public async Task<IReadOnlyList<string>> SyncAllAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnforceAccess || _locator.Find() is not { } libraryId)
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
                    if (await SyncUserInternalAsync(user, libraryId, config.IsAllowed(user.Id)).ConfigureAwait(false))
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
        /// Checks one user's access, unless enforcing it is off or there is no library yet.
        /// </summary>
        public async Task SyncUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnforceAccess || _locator.Find() is not { } libraryId)
            {
                return;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_userManager.GetUserById(userId) is { } user)
                {
                    await SyncUserInternalAsync(user, libraryId, config.IsAllowed(user.Id)).ConfigureAwait(false);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets whether Jellyfin's policy currently grants the user the library.
        /// </summary>
        public static bool PolicyGrantsAccess(User user, Guid libraryId)
        {
            var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
            return blocked.Length != 0
                ? !blocked.Contains(libraryId)
                : user.HasPermission(PermissionKind.EnableAllFolders) || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(libraryId);
        }

        private async Task<bool> SyncUserInternalAsync(User user, Guid libraryId, bool allowed)
        {
            var changed = false;
            try
            {
                if (LibraryAccessPolicy.BlockedLibraries(user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders), libraryId, allowed) is { } blocked)
                {
                    user.SetPreference(PreferenceKind.BlockedMediaFolders, blocked);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    changed = true;
                }

                // A list of blocked libraries replaces the policy's library selection
                if (user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders).Length == 0)
                {
                    var policy = _userManager.GetUserDto(user).Policy;
                    var others = _libraryManager.GetVirtualFolders()
                        .Select(f => Guid.TryParse(f.ItemId, CultureInfo.InvariantCulture, out var id) ? id : Guid.Empty)
                        .Where(id => id != Guid.Empty);
                    if (LibraryAccessPolicy.Apply(policy, libraryId, allowed, others))
                    {
                        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                        changed = true;
                    }
                }

                if (changed)
                {
                    _logger.LogInformation(
                        "hanime.tv: {Action} the library for {User} in the user's policy",
                        allowed ? "granted" : "revoked",
                        user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "hanime.tv: could not update the library access of {User}", user.Username);
            }

            return changed;
        }
    }
}
