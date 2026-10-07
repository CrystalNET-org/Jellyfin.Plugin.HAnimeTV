using Jellyfin.Plugin.HAnimeTV.Access;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Library
{
    /// <summary>
    /// Syncs the library once the server is up and whenever the settings are saved.
    /// </summary>
    public sealed class LibraryService : IHostedService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

        private readonly LibrarySync _sync;
        private readonly ILogger<LibraryService> _logger;
        private readonly CancellationTokenSource _stopping = new();
        private Plugin? _plugin;

        public LibraryService(LibrarySync sync, ILogger<LibraryService> logger)
        {
            _sync = sync;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _plugin = Plugin.Instance;
            if (_plugin is not null)
            {
                _plugin.ConfigurationChanged += OnConfigurationChanged;
            }

            // Not delaying the server's start
            Run(StartupDelay);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_plugin is not null)
            {
                _plugin.ConfigurationChanged -= OnConfigurationChanged;
            }

            _stopping.Cancel();
            return Task.CompletedTask;
        }

        private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => Run(TimeSpan.Zero);

        private void Run(TimeSpan delay)
        {
            var token = _stopping.Token;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        await _sync.SyncAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "hanime.tv: syncing the library failed");
                    }
                },
                token);
        }
    }
}
