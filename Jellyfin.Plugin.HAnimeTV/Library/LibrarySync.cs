using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Controller;
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
    public sealed record SyncReport(DateTimeOffset Time, int Series, int Episodes, WriteResult? Files, bool LibraryCreated, string? Error);

    /// <summary>
    /// Writes hanime.tv's catalog into the library's folder, creates the Jellyfin library for
    /// it, applies the user selection and has Jellyfin scan what changed.
    /// </summary>
    public sealed class LibrarySync
    {
        private readonly HanimeClient _client;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IServerApplicationHost _applicationHost;
        private readonly LibraryLocator _locator;
        private readonly LibraryAccessSync _access;
        private readonly ILogger<LibrarySync> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public LibrarySync(
            HanimeClient client,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IServerApplicationHost applicationHost,
            LibraryLocator locator,
            LibraryAccessSync access,
            ILogger<LibrarySync> logger)
        {
            _client = client;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _applicationHost = applicationHost;
            _locator = locator;
            _access = access;
            _logger = logger;
        }

        /// <summary>
        /// Gets the last sync's outcome, or null before the first.
        /// </summary>
        public SyncReport? LastReport { get; private set; }

        /// <summary>
        /// Gets the address the stream links use: the configured one, or Jellyfin's local one.
        /// </summary>
        public string StreamBaseUrl(PluginConfiguration config) =>
            (string.IsNullOrWhiteSpace(config.StreamBaseUrl) ? _applicationHost.GetApiUrlForLocalAccess(null, false) : config.StreamBaseUrl.Trim()).TrimEnd('/');

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
            catch (Exception ex) when (ex is HanimeException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _logger.LogError("hanime.tv: library sync failed: {Error}", ex.Message);
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
            var config = plugin.Configuration;
            if (string.IsNullOrEmpty(config.StreamToken))
            {
                config.StreamToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
                // Not UpdateConfiguration, which would start another sync
                plugin.SaveConfiguration(config);
            }

            var catalog = await _client.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            var baseUrl = StreamBaseUrl(config);
            var token = Uri.EscapeDataString(config.StreamToken);
            var series = LibraryLayout.Series(LibraryLayout.Visible(catalog, config));
            var files = LibraryLayout.Build(catalog, config, slug => $"{baseUrl}/HanimeTV/Stream/{Uri.EscapeDataString(slug)}/index.m3u8?token={token}");

            var folder = _locator.FolderPath;
            var written = LibraryWriter.Write(folder, files, cancellationToken);
            _logger.LogInformation(
                "hanime.tv: library at {Folder}: {Series} series, {Episodes} episodes; {Written} files written, {Deleted} episodes removed",
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
                _logger.LogInformation("hanime.tv: created the library {Name} for {Folder}", LibraryName(config), folder);
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

        private static string LibraryName(PluginConfiguration config) =>
            string.IsNullOrWhiteSpace(config.LibraryName) ? PluginConfiguration.DefaultLibraryName : config.LibraryName.Trim();

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
