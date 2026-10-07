using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// Takes the providers' channels and library away again from users who were given all
    /// channels or libraries in the dashboard. Jellyfin reports no changes to a user's access,
    /// so they are checked regularly.
    /// </summary>
    public class AccessTask : IScheduledTask
    {
        private readonly AccessSync _sync;

        public AccessTask(AccessSync sync)
        {
            _sync = sync;
        }

        public string Name => "Enforce Adult Media access";

        public string Key => "AdultMediaAccess";

        public string Description => "Grants each provider's channel or library to the users selected for it in the plugin's settings and takes it away from everyone else.";

        public string Category => "Adult Media";

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
