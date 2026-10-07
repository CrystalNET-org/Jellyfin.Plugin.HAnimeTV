using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Brings a user's library access in line with the plugin's user selection.
    /// </summary>
    /// <remarks>
    /// Jellyfin shows a library to a user whose policy has "all libraries" or lists the
    /// library (Dashboard → Users → Access). A legacy list of blocked libraries, if not empty,
    /// overrides both; nothing in the dashboard sets it any more.
    /// </remarks>
    public static class LibraryAccessPolicy
    {
        /// <summary>
        /// Gets the blocked libraries the user needs, or null if they are right already.
        /// </summary>
        public static Guid[]? BlockedLibraries(Guid[] blocked, Guid libraryId, bool allowed)
        {
            if (blocked.Length == 0)
            {
                // Not in use, and must stay so: once not empty, it replaces the user's library
                // selection and would grant every other library
                return null;
            }

            var contains = blocked.Contains(libraryId);
            if (allowed && contains)
            {
                return blocked.Where(id => id != libraryId).ToArray();
            }

            if (!allowed && !contains)
            {
                return [.. blocked, libraryId];
            }

            return null;
        }

        /// <summary>
        /// Changes the policy's library access so that it grants or denies the library; the
        /// user's access to other libraries stays the same.
        /// </summary>
        /// <param name="policy">The user's policy, changed in place.</param>
        /// <param name="libraryId">The library.</param>
        /// <param name="allowed">Whether the user may access the library.</param>
        /// <param name="otherLibraryIds">All other libraries, which a user with access to all
        /// libraries keeps access to.</param>
        /// <returns>Whether the policy changed.</returns>
        public static bool Apply(UserPolicy policy, Guid libraryId, bool allowed, IEnumerable<Guid> otherLibraryIds)
        {
            var enabled = policy.EnabledFolders ?? Array.Empty<Guid>();
            if (allowed)
            {
                if (policy.EnableAllFolders || enabled.Contains(libraryId))
                {
                    return false;
                }

                policy.EnabledFolders = [.. enabled, libraryId];
                return true;
            }

            if (policy.EnableAllFolders)
            {
                policy.EnableAllFolders = false;
                policy.EnabledFolders = otherLibraryIds.Where(id => id != libraryId).Distinct().ToArray();
                return true;
            }

            if (enabled.Contains(libraryId))
            {
                policy.EnabledFolders = enabled.Where(id => id != libraryId).ToArray();
                return true;
            }

            return false;
        }
    }
}
