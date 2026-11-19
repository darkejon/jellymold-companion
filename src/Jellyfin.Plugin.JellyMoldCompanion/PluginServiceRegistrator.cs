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
        serviceCollection.AddSingleton<PictureCropMeasurer>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PictureCropMeasurer>());
        serviceCollection.AddSingleton<RatingsFetcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<RatingsFetcher>());
        serviceCollection.AddSingleton<Imports.ImportService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<Imports.ImportService>());

        // Restarting Jellyfin, in quiet hours, to finish the plugin's own update.
        serviceCollection.AddSingleton<UpdateRestarter>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<UpdateRestarter>());

        // JellyMold Stats in everyone's Jellyfin menu, while the setting is on: in front of Jellyfin's own request handling.
        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Web.StatsMenuLink>();

        // The HLS master with subtitles that keep time (§16 idea 14): Jellyfin's own master, asked for at the plugin's
        // address, handed to Jellyfin inside the same request and given back with the subtitle playlists swapped.
        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Api.HlsMasterPlaylist>();

        // One registration per event type: Jellyfin's container does not match a consumer of a base type to its
        // derived events, so a start is not a progress as far as it is concerned.
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<SessionEndedEventArgs>, PlaybackListener>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, PlaybackListener>();
    }
}
