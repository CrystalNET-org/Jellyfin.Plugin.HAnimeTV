using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Access
{
    /// <summary>
    /// New users get access to all channels and libraries: takes the providers' away again unless they were selected.
    /// </summary>
    public sealed class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
    {
        /// <summary>
        /// The dashboard saves a new user's access right after creating the user, which
        /// grants all channels and libraries again: check once more after that.
        /// </summary>
        private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

        private readonly AccessSync _sync;
        private readonly ILogger<UserCreatedConsumer> _logger;

        public UserCreatedConsumer(AccessSync sync, ILogger<UserCreatedConsumer> logger)
        {
            _sync = sync;
            _logger = logger;
        }

        public async Task OnEvent(UserCreatedEventArgs eventArgs)
        {
            var userId = eventArgs.Argument.Id;
            await _sync.SyncUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Recheck).ConfigureAwait(false);
                    await _sync.SyncUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Adult Media: checking the access of a new user failed");
                }
            });
        }
    }
}
