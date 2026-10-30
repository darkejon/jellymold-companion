using Jellyfin.Plugin.JellyMoldCompanion.Configuration;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using JellyMoldCompanion.Api;
using JellyMoldCompanion.Recording;
using JellyMoldCompanion.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion;

/// <summary>
/// The plugin's running parts, started and stopped with the server: the history database, the recorder's queue,
/// and the queries the API reads through.
/// </summary>
public sealed class CompanionService : IHostedService
{
    private readonly ILogger<CompanionService> _logger;

    public CompanionService(IApplicationPaths paths, JellyfinHost host, ILogger<CompanionService> logger)
    {
        _logger = logger;
        Host = host;

        // Under Jellyfin's data path, so a backup of that folder includes it — and not in the plugin's own folder,
        // which is replaced when the plugin updates (design §10.1).
        Database = new HistoryDatabase(Path.Combine(paths.DataPath, "jellymold"), logger);
        Queries = new HistoryQueries(Database);
        Recording = new RecordingService(Database, () => host.Rules(Settings), () => JellyfinHost.Scope(Settings), logger);
    }

    public JellyfinHost Host { get; }

    public HistoryDatabase Database { get; }

    public HistoryQueries Queries { get; }

    public RecordingService Recording { get; }

    /// <summary>Set when the history could not be opened at all. Shown on the dashboard.</summary>
    public string? StartupError { get; private set; }

    public static PluginConfiguration Settings => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Recording.Start();
            _logger.LogInformation("JellyMold Companion is recording to {Path}", Database.FilePath);
        }
        catch (Exception e)
        {
            // Never take the server down with us: say what happened and record nothing.
            StartupError = e.Message;
            _logger.LogError(e, "JellyMold Companion could not open its history database; nothing will be recorded");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await Recording.DisposeAsync().ConfigureAwait(false);

    public bool Enqueue(RecorderMessage message) => Recording.Enqueue(message);
}
