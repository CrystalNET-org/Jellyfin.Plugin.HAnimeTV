using System.Text;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// What a sync changed in the library's folder.
    /// </summary>
    public sealed record WriteResult(int Written, int Unchanged, int Deleted)
    {
        public bool Changed => Written > 0 || Deleted > 0;
    }

    /// <summary>
    /// Brings the library's folder in line with its files: writes new and changed files,
    /// leaves unchanged ones alone (so Jellyfin sees nothing new), and deletes what is gone.
    /// </summary>
    public static class LibraryWriter
    {
        /// <summary>
        /// Marks a folder as the plugin's: only a folder with it, or an empty one, is written
        /// to, as the plugin deletes what it did not write.
        /// </summary>
        public const string MarkerFile = ".hanime-tv-library";

        private const string MarkerText = "Written by the hanime.tv plugin for Jellyfin. Everything in this folder is replaced on each sync.\n";

        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <exception cref="InvalidOperationException">The folder holds files that are not the plugin's.</exception>
        public static WriteResult Write(string root, IReadOnlyList<LibraryFile> files, CancellationToken cancellationToken)
        {
            root = Path.GetFullPath(root);
            var marker = Path.Combine(root, MarkerFile);
            if (Directory.Exists(root) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException($"{root} is not empty and was not created by the plugin; choose an empty folder for the library");
            }

            Directory.CreateDirectory(root);
            File.WriteAllText(marker, MarkerText, Utf8);

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            int written = 0, unchanged = 0, deleted = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A library file would leave the library's folder: " + file.RelativePath);
                }

                wanted.Add(path);
                var content = Utf8.GetBytes(file.Content);
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                {
                    unchanged++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Jellyfin may scan at any time: never let it see half a file
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, content);
                File.Move(temp, path, overwrite: true);
                written++;
            }

            // Folders no longer wanted: series that are gone, with everything in them
            var wantedFolders = wanted.Select(Path.GetDirectoryName).OfType<string>()
                .SelectMany(Ancestors)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                if (!wantedFolders.Contains(directory))
                {
                    deleted += Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count(f => f.EndsWith(".strm", StringComparison.Ordinal));
                    Directory.Delete(directory, recursive: true);
                }
            }

            // Episode files by folder, for files Jellyfin saved next to them ("… S01E01-thumb.jpg")
            var episodes = wanted.Where(p => p.EndsWith(".strm", StringComparison.Ordinal))
                .ToLookup(p => Path.GetDirectoryName(p)!, p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                if (wanted.Contains(file) || file == marker)
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                var ours = name.EndsWith(".strm", StringComparison.Ordinal) || name.EndsWith(".nfo", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal);
                var ofAnEpisode = name.Contains(" S01E", StringComparison.Ordinal);
                var ofAWantedEpisode = episodes[Path.GetDirectoryName(file)!].Any(e => name.StartsWith(e, StringComparison.Ordinal));
                // Other files of wanted series, such as images Jellyfin saved, stay
                if (!ours && (!ofAnEpisode || ofAWantedEpisode))
                {
                    continue;
                }

                if (name.EndsWith(".strm", StringComparison.Ordinal))
                {
                    deleted++;
                }

                File.Delete(file);
            }

            return new WriteResult(written, unchanged, deleted);

            IEnumerable<string> Ancestors(string directory)
            {
                for (var current = directory; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
                {
                    yield return current;
                }
            }
        }
    }
}
