using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.JellyMoldCompanion.Tasks;

/// <summary>
/// Subtitles ready before they are needed (design §16 idea 3). The first request for any embedded subtitle of a file makes
/// Jellyfin extract all of that file's subtitle tracks in one pass and keep them for good — slow on a NAS with a large
/// file, which is how a chosen track came to look unchosen (2026-10-29). Nothing does it ahead of time; this task does,
/// for items added since its last run, through Jellyfin's own extraction, so there is no second copy. Off until chosen:
/// Jellyfin's pass takes picture-based tracks too, which can be tens of megabytes each.
/// </summary>
public sealed class PrepareSubtitlesTask : IScheduledTask
{
    /// <summary>Items a night, so a big library added at once is worked through over several nights rather than in one.</summary>
    private const int PerRun = 200;

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ISubtitleEncoder _subtitles;
    private readonly ILogger<PrepareSubtitlesTask> _logger;

    public PrepareSubtitlesTask(ILibraryManager library, IMediaSourceManager mediaSources, ISubtitleEncoder subtitles,
        ILogger<PrepareSubtitlesTask> logger)
    {
        _library = library;
        _mediaSources = mediaSources;
        _subtitles = subtitles;
        _logger = logger;
    }

    public string Name => "Prepare subtitles";

    public string Key => "JellyMoldCompanionSubtitles";

    public string Description => "Extracts the embedded subtitles of newly added films and episodes ahead of time, so they appear at once the first time they are chosen. Does nothing unless switched on in JellyMold Companion's settings.";

    public string Category => "JellyMold Companion";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var settings = CompanionService.Settings;
        if (plugin is null || !settings.PrepareSubtitles)
        {
            return;
        }

        var since = settings.SubtitlesPreparedUntil ?? DateTime.UtcNow.AddDays(-7);
        var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
                IsVirtualItem = false,
                Recursive = true,
                MinDateCreated = since,
            })
            .OrderBy(i => i.DateCreated)
            .Take(PerRun)
            .ToList();

        var prepared = 0;
        var reached = since;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            try
            {
                if (await Prepare(item, cancellationToken).ConfigureAwait(false))
                {
                    prepared++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not prepare the subtitles of {Item}", item.Name);
            }

            reached = item.DateCreated;
            progress.Report(100.0 * (i + 1) / items.Count);
        }

        // Carry on from the last item reached; with nothing left, from now.
        settings.SubtitlesPreparedUntil = items.Count < PerRun ? DateTime.UtcNow : reached;
        plugin.SaveConfiguration();
        _logger.LogInformation("JellyMold Companion prepared the subtitles of {Prepared} of {Items} new items", prepared, items.Count);
    }

    /// <summary>Starts Jellyfin's extraction for each of the item's versions with embedded text subtitles. Whether there were any.</summary>
    private async Task<bool> Prepare(BaseItem item, CancellationToken cancellationToken)
    {
        var any = false;
        foreach (var source in _mediaSources.GetStaticMediaSources(item, false))
        {
            var embedded = source.MediaStreams
                .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.IsTextSubtitleStream)
                .ToList();
            if (embedded.Count == 0)
            {
                continue;
            }

            any = true;
#if JELLYFIN_12
            await _subtitles.ExtractAllExtractableSubtitles(source, cancellationToken).ConfigureAwait(false);
#else
            // 10.10 has no direct call: asking for any one embedded track extracts them all in one pass.
            await using var stream = await _subtitles.GetSubtitles(item, source.Id, embedded[0].Index, "srt", 0, 0, false, cancellationToken)
                .ConfigureAwait(false);
#endif
        }

        return any;
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
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        },
    ];
}
