using JellyMoldCompanion.Updates;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if JELLYFIN_12
using ActivityLog = Jellyfin.Database.Implementations.Entities.ActivityLog;
#else
using ActivityLog = Jellyfin.Data.Entities.ActivityLog;
#endif

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Finishes the plugin's own updates (design §13a, §19 decision 19). Jellyfin installs a new version by itself and
/// leaves it waiting for a restart it never makes; this restarts Jellyfin for it, once, in the quiet hours the settings
/// give (from 04:00 by default), while nobody is playing and no task is running (<see cref="UpdateRestart"/>). Never
/// with Playback Reporting installed: Jellyfin's own restart stops such a server instead of restarting it.
/// </summary>
public sealed class UpdateRestarter : IHostedService, IDisposable
{
    /// <summary>Playback Reporting's plugin id, the same on every server.</summary>
    public static readonly Guid PlaybackReportingId = Guid.Parse("5c534381-91a3-43cb-907a-35aa02eb9d2c");

    /// <summary>Not straight after the server starts: Jellyfin's own update check runs then, and apps reconnect.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(5);

    private readonly IPluginManager _plugins;
    private readonly ISessionManager _sessions;
    private readonly ITaskManager _tasks;
    private readonly IActivityManager _activity;
    private readonly IServiceProvider _services;
    private readonly ILogger<UpdateRestarter> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private bool _restarting;

    public UpdateRestarter(IPluginManager plugins, ISessionManager sessions, ITaskManager tasks, IActivityManager activity,
        IServiceProvider services, ILogger<UpdateRestarter> logger)
    {
        _plugins = plugins;
        _sessions = sessions;
        _tasks = tasks;
        _activity = activity;
        _services = services;
        _logger = logger;
    }

    public static Version Running => typeof(UpdateRestarter).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>What the plugin will do about an update waiting for a restart, as of now.</summary>
    public UpdatePlan Plan => UpdateRestart.Plan(Facts(), DateTimeOffset.UtcNow, TimeZoneInfo.Local);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(FirstLook, stopping).ConfigureAwait(false);
            while (!stopping.IsCancellationRequested && !_restarting)
            {
                try
                {
                    await LookAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "JellyMold Companion could not check for an update waiting for a restart");
                }

                await Task.Delay(LookEvery, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The server is stopping.
        }
    }

    private async Task LookAsync()
    {
        var plan = Plan;
        if (plan.Waiting is not { } waiting || !plan.Now || Plugin.Instance is not { } plugin)
        {
            return;
        }

        if (_services.GetService<ISystemManager>() is not { } system)
        {
            _logger.LogWarning("JellyMold Companion {Version} is waiting for Jellyfin to restart, and this server gave no way to restart it", Release(waiting));
            return;
        }

        // Written down first, so a restart that does not load the new version is never tried again for it.
        plugin.Configuration.RestartedForVersion = waiting.ToString();
        plugin.SaveConfiguration();

        var said = $"JellyMold Companion restarted Jellyfin to finish updating to {Release(waiting)}";
        _logger.LogInformation("{Said} (from {Running}; nobody was playing and no task was running)", said, Release(Running));
        try
        {
            await _activity.CreateAsync(new ActivityLog(said, "JellyMoldCompanionUpdateRestart", Guid.Empty)
            {
                ShortOverview = $"At {DateTime.Now:HH:mm}, in the quiet hours set on the plugin's settings page",
                LogSeverity = LogLevel.Information,
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "JellyMold Companion could not add its restart to the activity log");
        }

        _restarting = true;
        system.Restart();
    }

    private UpdateFacts Facts()
    {
        var settings = CompanionService.Settings;
        var running = Running;

        // Each version's status as Jellyfin's own API gives it (GetPluginInfo). Jellyfin 12 keeps "waiting for a restart"
        // apart from the status it saves (RestartRequired, in memory), so the saved status alone misses it there.
        var plugins = _plugins.Plugins.Select(p => (p.Id, p.Version, p.GetPluginInfo().Status)).ToList();
        var waiting = plugins
            .Where(p => p.Id == Plugin.PluginId && p.Status == PluginStatus.Restart && p.Version > running)
            .Select(p => p.Version)
            .DefaultIfEmpty()
            .Max();
        var playbackReporting = plugins.Any(p => p.Id == PlaybackReportingId
            && p.Status is not (PluginStatus.Deleted or PluginStatus.Disabled or PluginStatus.NotSupported));

        return new UpdateFacts(
            running,
            waiting,
            settings.RestartForUpdates,
            UpdateRestart.ParseAt(settings.RestartForUpdatesAt),
            playbackReporting,
            Version.TryParse(settings.RestartedForVersion, out var tried) ? tried : null,
            _sessions.Sessions.Any(s => s.NowPlayingItem is not null),
            _tasks.ScheduledTasks.Any(t => t.State != TaskState.Idle));
    }

    /// <summary>The release a plugin version carries: 10.0.8.0 and 12.0.8.0 are both 0.8.0 (design §13a).</summary>
    public static string Release(Version version) => $"{version.Minor}.{version.Build}.{Math.Max(version.Revision, 0)}";
}
