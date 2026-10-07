using Jellyfin.Data.Enums;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// The outcome of a sync.
    /// </summary>
    public sealed record SyncReport(DateTimeOffset Time, int Series, int Episodes, WriteResult? Files, bool LibraryCreated, string? Error)
    {
        /// <summary>
        /// Gets a value indicating whether the hentai provider is not in library mode, so nothing was written.
        /// </summary>
        public bool Inactive { get; init; }
    }

    /// <summary>
    /// Writes the merged hentai catalog (hanime.tv and Hentai Haven) into the library's folder, creates the Jellyfin library for
    /// it, applies the user selection and has Jellyfin scan what changed.
    /// </summary>
    public sealed class LibrarySync
    {
        private readonly HentaiCatalog _catalog;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly StreamLinks _links;
        private readonly LibraryLocator _locator;
        private readonly AccessSync _access;
        private readonly ILogger<LibrarySync> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public LibrarySync(
            HentaiCatalog catalog,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            StreamLinks links,
            LibraryLocator locator,
            AccessSync access,
            ILogger<LibrarySync> logger)
        {
            _catalog = catalog;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _links = links;
            _locator = locator;
            _access = access;
            _logger = logger;
        }

        /// <summary>
        /// Gets the last sync's outcome, or null before the first.
        /// </summary>
        public SyncReport? LastReport { get; private set; }

        public async Task<SyncReport> SyncAsync(CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded");
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var report = await SyncInternalAsync(plugin, cancellationToken).ConfigureAwait(false);
                LastReport = report;
                return report;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _logger.LogError("Hentai: library sync failed: {Error}", ex.Message);
                LastReport = new SyncReport(DateTimeOffset.UtcNow, LastReport?.Series ?? 0, LastReport?.Episodes ?? 0, null, false, ex.Message);
                return LastReport;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<SyncReport> SyncInternalAsync(Plugin plugin, CancellationToken cancellationToken)
        {
            var config = plugin.Configuration.Hentai;
            if (config.Mode != ProviderMode.Library)
            {
                // The files stay; the access check hides the library from everyone
                await _access.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                if (config.Mode == ProviderMode.Channel)
                {
                    // Reads the catalogs ahead of the channel, which would otherwise wait for
                    // Hentai Haven's first read, minutes long
                    await _catalog.GetAsync(config, cancellationToken).ConfigureAwait(false);
                }

                return new SyncReport(DateTimeOffset.UtcNow, 0, 0, null, false, null) { Inactive = true };
            }

            var catalog = await _catalog.GetAsync(config, cancellationToken).ConfigureAwait(false);
            var series = LibraryLayout.Series(LibraryLayout.Visible(catalog, config));
            var files = LibraryLayout.Build(catalog, config, _links.For);

            var folder = _locator.FolderPath;
            var written = LibraryWriter.Write(folder, files, cancellationToken);
            _logger.LogInformation(
                "Hentai: library at {Folder}: {Series} series, {Episodes} episodes; {Written} files written, {Deleted} episodes removed",
                folder,
                series.Count,
                series.Sum(s => s.Episodes.Count),
                written.Written,
                written.Deleted);

            var created = false;
            var libraryId = _locator.Find();
            if (libraryId is null && config.CreateLibrary)
            {
                await _libraryManager.AddVirtualFolder(LibraryName(config), CollectionTypeOptions.tvshows, CreateLibraryOptions(folder), refreshLibrary: false).ConfigureAwait(false);
                libraryId = _locator.Find();
                created = libraryId is not null;
                _logger.LogInformation("Hentai: created the library {Name} for {Folder}", LibraryName(config), folder);
            }

            // Before Jellyfin shows the library's contents to anyone
            await _access.SyncAllAsync(cancellationToken).ConfigureAwait(false);

            if (libraryId is { } id && (written.Changed || created))
            {
                _providerManager.QueueRefresh(
                    id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                    },
                    RefreshPriority.High);
            }

            return new SyncReport(DateTimeOffset.UtcNow, series.Count, series.Sum(s => s.Episodes.Count), written, created, null);
        }

        private static string LibraryName(HentaiSettings config) =>
            string.IsNullOrWhiteSpace(config.LibraryName) ? HentaiSettings.DefaultLibraryName : config.LibraryName.Trim();

        /// <summary>
        /// A shows library that takes its metadata from the plugin's NFO files only, and never
        /// runs ffmpeg over the streams during scans.
        /// </summary>
        internal static LibraryOptions CreateLibraryOptions(string folder)
        {
            var noFetchers = new[] { "Series", "Season", "Episode" }
                .Select(type => new TypeOptions { Type = type, MetadataFetchers = [], ImageFetchers = [] })
                .ToArray();
            return new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(folder)],
                EnableRealtimeMonitor = false,
                EnablePhotos = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableEmbeddedTitles = false,
                EnableEmbeddedEpisodeInfos = false,
                SaveLocalMetadata = false,
                AutomaticRefreshIntervalDays = 0,
                TypeOptions = noFetchers,
            };
        }
    }
}
