using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using JellyMoldCompanion.Ratings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Tasks;

/// <summary>
/// Ratings for the rest of the library (design §16 idea 13). New titles are fetched as they arrive and anything an app asks
/// for at once; this works through everything not fetched yet, newest first, then refreshes each title a month after it
/// was last fetched. A hundred titles go in one request, so a library of a thousand films takes ten; the plugin never uses
/// more than half the key's daily allowance.
/// </summary>
public sealed class FetchRatingsTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly RatingsFetcher _fetcher;
    private readonly ILogger<FetchRatingsTask> _logger;

    public FetchRatingsTask(ILibraryManager library, RatingsFetcher fetcher, ILogger<FetchRatingsTask> logger)
    {
        _library = library;
        _fetcher = fetcher;
        _logger = logger;
    }

    public string Name => "Fetch ratings";

    public string Key => "JellyMoldCompanionRatings";

    public string Description => "Fetches films' and series' ratings (IMDb, Rotten Tomatoes, Letterboxd and more) from MDBList, so JellyMold can show the one each viewer prefers. Needs an MDBList API key in JellyMold Companion's settings; never uses more than half the key's daily allowance.";

    public string Category => "JellyMold Companion";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _fetcher.EnsureKeyChecked(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var fetches = _fetcher.Queries.Fetches();
        var now = DateTimeOffset.UtcNow;
        var due = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = RatingsFetcher.Kinds,
                IsVirtualItem = false,
                Recursive = true,
            })
            .Where(RatingsFetcher.IsRateable)
            .ToList();
        // A title Jellyfin has not identified (a home video, a file it could not match) has no id to ask MDBList by.
        var unidentified = due.Count(i => RatingsFetcher.Lookup(i) is null);
        due = due
            .Where(i => RatingsFetcher.Lookup(i) is not null)
            .Where(i => RatingRules.NeedsFetching(fetches.GetValueOrDefault(i.Id), now))
            // Never fetched first, newest first among them; then the stalest refresh.
            .OrderBy(i => fetches.ContainsKey(i.Id))
            .ThenByDescending(i => fetches.ContainsKey(i.Id) ? -fetches[i.Id].FetchedAt.ToUnixTimeSeconds() : i.DateCreated.Ticks)
            .ToList();

        var fetched = 0;
        var stopped = false;
        foreach (var chunk in due.Chunk(RatingRules.BatchSize * 5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (done, stop) = await _fetcher.Fetch(chunk, cancellationToken).ConfigureAwait(false);
            fetched += done;
            progress.Report(100.0 * Math.Min(due.Count, fetched) / Math.Max(1, due.Count));
            if (stop)
            {
                stopped = true;
                break;
            }
        }

        var today = _fetcher.Today;
        _logger.LogInformation(
            "JellyMold Companion fetched ratings for {Fetched} of {Due} titles due; requests made today: {Used}{Stopped:l}. {Unidentified} titles have no TMDb, TheTVDB or IMDb id to look them up by",
            fetched, due.Count, today.Used, stopped ? "; the rest carry on tomorrow" : string.Empty, unidentified);
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
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
        },
    ];
}
