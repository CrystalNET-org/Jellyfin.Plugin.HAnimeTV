using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// Finds the library's folder and the Jellyfin library that contains it.
    /// </summary>
    public sealed class LibraryLocator
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _paths;

        public LibraryLocator(ILibraryManager libraryManager, IApplicationPaths paths)
        {
            _libraryManager = libraryManager;
            _paths = paths;
        }

        /// <summary>
        /// Gets the folder the library's files are written to.
        /// </summary>
        public string FolderPath
        {
            get
            {
                var configured = Plugin.Instance?.Configuration.LibraryPath;
                return Normalize(string.IsNullOrWhiteSpace(configured) ? Path.Combine(_paths.DataPath, "hanime.tv") : configured);
            }
        }

        /// <summary>
        /// Gets the id of the Jellyfin library with the folder, or null if there is none.
        /// </summary>
        public Guid? Find()
        {
            var folder = FolderPath;
            var library = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => (f.Locations ?? Array.Empty<string>()).Any(l => string.Equals(Normalize(l), folder, StringComparison.Ordinal)));
            return library is not null && Guid.TryParse(library.ItemId, CultureInfo.InvariantCulture, out var id) ? id : null;
        }

        private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
