using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV
{
    /// <summary>
    /// Registers the channel and the enforcement of its user selection.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton(services => new HanimeClient(
                services.GetRequiredService<IHttpClientFactory>(),
                () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<HanimeClient>()));
            // Jellyfin's channel manager takes the channels from the container
            serviceCollection.AddSingleton<IChannel, HanimeChannel>();
            serviceCollection.AddSingleton<ChannelAccessSync>();
            serviceCollection.AddHostedService<ChannelAccessService>();
            serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        }
    }
}
