using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyMoldCompanion;

/// <summary>Tells Jellyfin's container about the plugin's parts. Found by Jellyfin, created with no arguments.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellyfinHost>();
        serviceCollection.AddSingleton<CompanionService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<CompanionService>());
        serviceCollection.AddSingleton<IdleGuessCorrector>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<IdleGuessCorrector>());
        serviceCollection.AddHostedService<LibraryListener>();
        serviceCollection.AddSingleton<Segments.SegmentRefresher>();
#if JELLYFIN_12
        serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider, Segments.LearnedSegmentProvider>();
#else
        serviceCollection.AddSingleton<MediaBrowser.Controller.IMediaSegmentProvider, Segments.LearnedSegmentProvider>();
#endif
        serviceCollection.AddSingleton<Imports.ImportService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Imports.ImportService>());

        // One registration per event type: Jellyfin's container does not match a consumer of a base type to its
        // derived events, so a start is not a progress as far as it is concerned.
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<SessionEndedEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, PlaybackListener>();
    }
}
