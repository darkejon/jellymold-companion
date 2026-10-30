using JellyMoldCompanion.Recording;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Tasks;

/// <summary>
/// Nightly, under Dashboard → Scheduled Tasks (design §10.3): a consistent copy of the history, the old copies
/// pruned to seven daily and four weekly, and progress samples past their retention dropped.
/// </summary>
public sealed class BackupTask : IScheduledTask
{
    private readonly CompanionService _companion;
    private readonly ILogger<BackupTask> _logger;

    public BackupTask(CompanionService companion, ILogger<BackupTask> logger)
    {
        _companion = companion;
        _logger = logger;
    }

    public string Name => "Back up watch history";

    public string Key => "JellyMoldCompanionBackup";

    public string Description => "Copies JellyMold Companion's history database, keeps a week of daily copies and a month of weekly ones, and drops progress samples past their retention.";

    public string Category => "JellyMold Companion";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = CompanionService.Settings;
        var database = _companion.Database;
        if (database.IsReadOnly || _companion.StartupError is not null)
        {
            _logger.LogWarning("JellyMold Companion's history is not open for writing; nothing backed up");
            return Task.CompletedTask;
        }

        database.Backup();
        progress.Report(60);
        database.PruneBackups(Math.Max(1, settings.DailyBackupsToKeep), Math.Max(0, settings.WeeklyBackupsToKeep));
        progress.Report(80);

        if (settings.SampleRetentionDays > 0)
        {
            var now = DateTimeOffset.UtcNow;
            _companion.Enqueue(new TrimSamples(now, now.AddDays(-settings.SampleRetentionDays)));
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
#if JELLYFIN_12
            Type = TaskTriggerInfoType.DailyTrigger,
#else
            Type = TaskTriggerInfo.TriggerDaily,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        },
    ];
}
