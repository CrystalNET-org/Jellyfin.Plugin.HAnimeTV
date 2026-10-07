using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.HAnimeTV.Access;
using Jellyfin.Plugin.HAnimeTV.Channels;
using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.Hentai;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using Jellyfin.Plugin.HAnimeTV.Library;
using Jellyfin.Plugin.HAnimeTV.Pornhub;
using Jellyfin.Plugin.HAnimeTV.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV
{
    /// <summary>
    /// Registers the providers' clients, channels and stream links, the library sync and the
    /// enforcement of the user selections.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton(services => new HanimeClient(
                services.GetRequiredService<IHttpClientFactory>(),
                () => Plugin.Instance?.Configuration.Hentai ?? new HentaiSettings(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<HanimeClient>()));
            serviceCollection.AddSingleton(services => new HentaiHavenClient(
                services.GetRequiredService<IHttpClientFactory>(),
                () => Plugin.Instance?.Configuration.Hentai ?? new HentaiSettings(),
                Path.Combine(services.GetRequiredService<IApplicationPaths>().DataPath, "adult-media", "hentaihaven.json"),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<HentaiHavenClient>()));
            serviceCollection.AddSingleton(services => new PornhubClient(
                services.GetRequiredService<IHttpClientFactory>(),
                () => Plugin.Instance?.Configuration.Pornhub ?? new PornhubSettings(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<PornhubClient>()));
            serviceCollection.AddSingleton<HentaiCatalog>();

            serviceCollection.AddSingleton<StreamLinks>();
            serviceCollection.AddSingleton<HanimeStreamResolver>();
            serviceCollection.AddSingleton<HentaiHavenStreamResolver>();
            serviceCollection.AddSingleton<PornhubStreamResolver>();

            serviceCollection.AddSingleton<HentaiChannelSource>();
            serviceCollection.AddSingleton<PornhubChannelSource>();
            serviceCollection.AddSingleton<IChannel>(services => new ProviderChannel(
                services.GetRequiredService<HentaiChannelSource>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<ProviderChannel>()));
            serviceCollection.AddSingleton<IChannel>(services => new ProviderChannel(
                services.GetRequiredService<PornhubChannelSource>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<ProviderChannel>()));

            serviceCollection.AddSingleton<LibraryLocator>();
            serviceCollection.AddSingleton<AccessSync>();
            serviceCollection.AddSingleton<LibrarySync>();
            serviceCollection.AddHostedService<LibraryService>();
            serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        }
    }
}
