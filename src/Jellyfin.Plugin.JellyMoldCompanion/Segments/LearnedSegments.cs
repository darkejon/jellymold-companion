using JellyMoldCompanion.AppData;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
#if JELLYFIN_12
using MediaBrowser.Controller.MediaSegments;
using MediaSegmentType = Jellyfin.Database.Implementations.Enums.MediaSegmentType;
#else
using MediaBrowser.Controller;
using MediaSegmentType = Jellyfin.Data.Enums.MediaSegmentType;
#endif

namespace Jellyfin.Plugin.JellyMoldCompanion.Segments;

/// <summary>
/// Publishes the skip segments apps have learned to Jellyfin (design §16 idea 4), so the web client and TV apps show
/// the Skip button too. Jellyfin asks its providers from a scheduled task, and only for items with no segments yet from
/// any provider — so these fill the gaps a chapter-name provider leaves rather than correcting it.
/// </summary>
public sealed class LearnedSegmentProvider : IMediaSegmentProvider
{
    private readonly CompanionService _companion;

    public LearnedSegmentProvider(CompanionService companion) => _companion = companion;

    public string Name => "JellyMold Companion";

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaSegmentDto> segments = new AppDataQueries(_companion.Database).SegmentsFor(request.ItemId)
            .Select(s => new MediaSegmentDto
            {
                ItemId = request.ItemId,
                Type = Enum.Parse<MediaSegmentType>(s.Kind),
                StartTicks = TimeSpan.FromSeconds(s.StartSeconds).Ticks,
                EndTicks = TimeSpan.FromSeconds(s.EndSeconds).Ticks,
            })
            .ToList();
        return Task.FromResult(segments);
    }

    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);

#if JELLYFIN_12
    /// <summary>Nothing extracted to clean up: the segments live in the plugin's own database.</summary>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;
#endif
}

/// <summary>Asks Jellyfin to fetch an item's segments from its providers now, rather than at the next 12-hourly run.</summary>
public sealed class SegmentRefresher
{
    private readonly IMediaSegmentManager _segments;
    private readonly ILibraryManager _library;
    private readonly ILogger<SegmentRefresher> _logger;

    public SegmentRefresher(IMediaSegmentManager segments, ILibraryManager library, ILogger<SegmentRefresher> logger)
    {
        _segments = segments;
        _library = library;
        _logger = logger;
    }

    /// <summary>How many of the items now have segments in Jellyfin — ours, or another provider's that were there first.</summary>
    /// <param name="overwrite">Have Jellyfin drop what it holds and ask every provider again — after a segment is deleted
    /// here, so Jellyfin's copy goes too.</param>
    public async Task<int> Publish(IReadOnlyList<Guid> itemIds, bool overwrite = false)
    {
        var published = 0;
        foreach (var id in itemIds)
        {
            if (_library.GetItemById(id) is not { } item)
            {
                continue;
            }

            try
            {
#if JELLYFIN_12
                await _segments.RunSegmentPluginProviders(item, _library.GetLibraryOptions(item), overwrite, CancellationToken.None).ConfigureAwait(false);
#else
                await _segments.RunSegmentPluginProviders(item, overwrite, CancellationToken.None).ConfigureAwait(false);
#endif
                published++;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Jellyfin could not refresh the segments of {Item}; its own task will pick them up", item.Name);
            }
        }

        return published;
    }
}
