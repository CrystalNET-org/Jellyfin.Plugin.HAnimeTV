using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// Brings new uploads into the library and removes what hanime.tv took down.
    /// </summary>
    public class LibrarySyncTask : IScheduledTask
    {
        private readonly LibrarySync _sync;

        public LibrarySyncTask(LibrarySync sync)
        {
            _sync = sync;
        }

        public string Name => "Sync hanime.tv library";

        public string Key => "HAnimeTVLibrarySync";

        public string Description => "Writes hanime.tv's catalog into the hanime.tv library and has Jellyfin scan what changed.";

        public string Category => "hanime.tv";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var report = await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
            if (report.Error is not null)
            {
                // Shown as the task's failure in the dashboard
                throw new InvalidOperationException(report.Error);
            }

            progress.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
        ];
    }
}
