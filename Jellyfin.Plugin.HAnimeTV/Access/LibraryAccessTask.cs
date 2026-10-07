using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Takes the library away again from users who were given all libraries in the dashboard.
    /// Jellyfin reports no changes to a user's access, so they are checked regularly.
    /// </summary>
    public class LibraryAccessTask : IScheduledTask
    {
        private readonly LibraryAccessSync _sync;

        public LibraryAccessTask(LibraryAccessSync sync)
        {
            _sync = sync;
        }

        public string Name => "Enforce hanime.tv library access";

        public string Key => "HAnimeTVLibraryAccess";

        public string Description => "Grants the hanime.tv library to the users selected in the plugin's settings and takes it away from everyone else.";

        public string Category => "hanime.tv";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await _sync.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(15).Ticks },
        ];
    }
}
