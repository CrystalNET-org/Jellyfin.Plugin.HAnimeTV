using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV
{
    /// <summary>
    /// Removes older versions of the plugin that Jellyfin keeps beside it under another name.
    /// </summary>
    /// <remarks>
    /// Jellyfin only treats plugin folders with the same name as versions of one plugin. The
    /// plugin was called "hanime.tv" before 0.2, so the folder of such a version stays, and
    /// when Jellyfin updates the plugin it activates the next older version with the same id:
    /// that one. Both then load, and their API routes clash.
    /// </remarks>
    public static class PluginCleanup
    {
        /// <summary>
        /// Deletes the folders of older versions with the plugin's id and another name; where a
        /// folder cannot be deleted (its DLL in use, on Windows), marks it as deleted, which
        /// Jellyfin honours on its next start. Older versions with the same name are left to
        /// Jellyfin.
        /// </summary>
        /// <param name="pluginsPath">Jellyfin's plugins folder.</param>
        /// <param name="id">The plugin's id.</param>
        /// <param name="ownName">The plugin's name.</param>
        /// <param name="ownFolder">The folder of the running version, which stays.</param>
        /// <param name="ownVersion">The running version: only older ones are removed.</param>
        /// <param name="logger">Logs what was removed.</param>
        /// <returns>The folders removed or marked.</returns>
        public static IReadOnlyList<string> RemoveOlderVersions(string pluginsPath, Guid id, string ownName, string ownFolder, Version ownVersion, ILogger logger)
        {
            var removed = new List<string>();
            if (!Directory.Exists(pluginsPath))
            {
                return removed;
            }

            ownFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownFolder));
            foreach (var folder in Directory.EnumerateDirectories(pluginsPath))
            {
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), ownFolder, StringComparison.Ordinal)
                    || ReadManifest(Path.Combine(folder, "meta.json")) is not { } manifest
                    || !Guid.TryParse(manifest["guid"]?.GetValue<string>(), out var guid) || guid != id
                    || !Version.TryParse(manifest["version"]?.GetValue<string>(), out var version) || version >= ownVersion)
                {
                    continue;
                }

                var name = manifest["name"]?.GetValue<string>();
                if (string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                    logger.LogWarning("Adult Media: removed the old version {Name} {Version} at {Folder}; restart Jellyfin to unload it", name, version, folder);
                    removed.Add(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    try
                    {
                        manifest["status"] = "Deleted";
                        File.WriteAllText(Path.Combine(folder, "meta.json"), manifest.ToJsonString());
                        logger.LogWarning("Adult Media: could not delete the old version {Name} {Version} at {Folder} ({Error}); marked it as deleted, restart Jellyfin to remove it", name, version, folder, ex.Message);
                        removed.Add(folder);
                    }
                    catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError("Adult Media: the old version {Name} {Version} at {Folder} conflicts with this one; delete the folder and restart Jellyfin ({Error})", name, version, folder, inner.Message);
                    }
                }
            }

            return removed;
        }

        private static JsonObject? ReadManifest(string path)
        {
            try
            {
                return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
