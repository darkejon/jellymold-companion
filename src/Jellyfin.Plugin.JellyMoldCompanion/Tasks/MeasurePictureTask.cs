using System.Diagnostics;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Tasks;

/// <summary>
/// Black bars for the rest of the library (design §16 idea 11). New files are measured as they arrive and anything an app
/// asks for is measured at once; this works through everything else, newest first, for at most two hours a night, so a
/// large library is done over several nights. Files measured already are skipped unless they have been replaced.
/// </summary>
public sealed class MeasurePictureTask : IScheduledTask
{
    private static readonly TimeSpan Budget = TimeSpan.FromHours(2);

    private readonly ILibraryManager _library;
    private readonly PictureCropMeasurer _measurer;
    private readonly ILogger<MeasurePictureTask> _logger;

    public MeasurePictureTask(ILibraryManager library, PictureCropMeasurer measurer, ILogger<MeasurePictureTask> logger)
    {
        _library = library;
        _measurer = measurer;
        _logger = logger;
    }

    public string Name => "Measure black bars";

    public string Key => "JellyMoldCompanionPictureCrop";

    public string Description => "Measures the black bars built into the picture of films and episodes, so JellyMold opens them already cropped. Up to two hours a night, newest first. Does nothing if switched off in JellyMold Companion's settings.";

    public string Category => "JellyMold Companion";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!CompanionService.Settings.MeasurePictureCrop)
        {
            return;
        }

        var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = PictureCropMeasurer.Kinds,
                IsVirtualItem = false,
                Recursive = true,
            })
            .Where(PictureCropMeasurer.IsMeasurable)
            .OrderByDescending(i => i.DateCreated)
            .ToList();

        var clock = Stopwatch.StartNew();
        var measured = 0;
        var reached = 0;
        for (; reached < items.Count && clock.Elapsed < Budget; reached++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                measured += await _measurer.Measure(items[reached], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not measure the black bars of {Item}", items[reached].Name);
            }

            progress.Report(100.0 * (reached + 1) / items.Count);
        }

        _logger.LogInformation("JellyMold Companion measured the black bars of {Measured} files in {Minutes:0} minutes; {Left} items not reached tonight",
            measured, clock.Elapsed.TotalMinutes, items.Count - reached);
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
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks,
        },
    ];
}
