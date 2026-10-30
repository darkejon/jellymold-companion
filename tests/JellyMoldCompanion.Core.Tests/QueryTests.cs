using JellyMoldCompanion.Api;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;

namespace JellyMoldCompanion.Tests;

/// <summary>Watching reported afterwards (§6.3), and what the API reads back (§9).</summary>
public class ClientSessionTests
{
    private static ClientSessionInput Offline(string id, MediaSnapshot item, DateTimeOffset start, double from, double to) => new()
    {
        ClientSessionId = id,
        ItemId = item.ItemId,
        StartedAt = start.UtcDateTime,
        EndedAt = start.Seconds(to - from).UtcDateTime,
        StartPositionSeconds = from,
        EndPositionSeconds = to,
        ActualSeconds = to - from,
        DeviceName = "iPhone",
    };

    private static IReadOnlyList<ClientSessionOutcome> Submit(Harness h, DateTimeOffset now, params (ClientSessionInput, MediaSnapshot?)[] sessions)
    {
        var submission = new ClientSessionSubmission(now) { User = Harness.Jon, Sessions = sessions };
        h.Recorder.Apply(submission);
        return submission.Done.Task.Result;
    }

    [Fact]
    public void WatchingOnAPlaneCountsWhenItIsPostedLater()
    {
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);
        var outcome = Assert.Single(Submit(h, Harness.Tuesday.AddDays(1),
            (Offline("a", episode, Harness.Tuesday, 0, 2640), episode)));

        Assert.Equal("Accepted", outcome.Result);
        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(2640, viewing.Credited, 1);
    }

    [Fact]
    public void PostingTheSameSessionTwiceChangesNothing()
    {
        using var h = new Harness();
        var film = Harness.Film();
        Submit(h, Harness.Tuesday.AddDays(1), (Offline("a", film, Harness.Tuesday, 0, 1200), film));
        var second = Assert.Single(Submit(h, Harness.Tuesday.AddDays(1), (Offline("a", film, Harness.Tuesday, 0, 1200), film)));

        Assert.Equal("Duplicate", second.Result);
        Assert.Single(h.Sessions());
    }

    [Fact]
    public void ASessionTheServerAlsoHeardIsTheSameWatching()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 1200), 1200);

        var outcome = Assert.Single(Submit(h, Harness.Tuesday.AddHours(2), (Offline("a", film, Harness.Tuesday, 0, 1200), film)));
        Assert.Equal("Duplicate", outcome.Result);
    }

    [Fact]
    public void ImplausibleSessionsAreRefused()
    {
        using var h = new Harness();
        var film = Harness.Film(minutes: 100);
        var tooFast = Offline("fast", film, Harness.Tuesday, 0, 600) with { ActualSeconds = 99999 };
        var future = Offline("future", film, Harness.Tuesday.AddDays(3), 0, 600);
        var outcomes = Submit(h, Harness.Tuesday.AddHours(1), (tooFast, film), (future, film), (Offline("gone", film, Harness.Tuesday, 0, 60), null));

        Assert.Equal(["implausibleSeconds", "inTheFuture", "unknownItem"], outcomes.Select(o => o.Reason));
    }

    [Fact]
    public void TheReplayedTickAfterAnOfflineSessionConfirmsIt()
    {
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);
        var now = Harness.Tuesday.AddDays(1);
        Submit(h, now, (Offline("a", episode, Harness.Tuesday, 0, 2200), episode)); // 83%

        // The app then replays what it kept: a stop with no play before it, and the tick.
        h.Stop(episode, now.Seconds(1), 2200);
        h.Marked(episode, now.Seconds(2), played: true);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal("mark", viewing.CompletedBy);
        Assert.Equal(2200, viewing.Credited, 1);
    }
}

public class AlternateVersionTests
{
    [Fact]
    public void Jellyfin12TickingTheOtherVersionsIsNotASecondViewing()
    {
        using var h = new Harness();
        var theatrical = Harness.Film(minutes: 120, id: Guid.NewGuid());
        var extended = Harness.Film(minutes: 140, id: Guid.NewGuid());
        h.Start(theatrical, Harness.Tuesday, 0);
        var t = h.Play(theatrical, Harness.Tuesday, 0, 7000, step: 60);
        h.Stop(theatrical, t, 7000);

        // Jellyfin 12 propagates the played state to the film's other versions with reason TogglePlayed.
        h.Marked(extended, t.Seconds(1), played: true);

        Assert.Single(h.Viewings());
    }
}

public class SummaryTests
{
    private sealed class Library : ILibraryFacts
    {
        public int? EpisodesInSeries(Guid seriesId) => 2;

        public Guid? LastFileOfBook(Guid albumId) => null;
    }

    [Fact]
    public void TheStatsPageInOneRequest()
    {
        using var h = new Harness();
        var film = Harness.Film(minutes: 100);
        var e1 = Harness.Episode(minutes: 40, id: Guid.NewGuid(), episode: 1);
        var e2 = Harness.Episode(minutes: 40, id: Guid.NewGuid(), episode: 2);

        // A film watched twice, two episodes watched, and one ticked without playing.
        foreach (var day in new[] { 0, 1 })
        {
            var at = Harness.Tuesday.AddDays(day);
            h.Start(film, at, 0);
            h.Stop(film, h.Play(film, at, 0, 6000, step: 60), 6000);
        }

        var t = Harness.Tuesday.AddDays(2);
        h.Start(e1, t, 0);
        h.Stop(e1, h.Play(e1, t, 0, 2400, step: 60), 2400);
        h.Marked(e2, t.AddHours(2), played: true);

        var summary = new HistoryQueries(h.Database).Summary(Harness.Jon.Id, TimeZoneInfo.Utc, 5, new Library(), t.AddHours(3));

        Assert.Equal(1, summary.Films.Watched);
        Assert.Equal(2, summary.Films.Viewings);
        Assert.Equal(12000, summary.Films.CreditedSeconds, 1);
        Assert.Equal(2, summary.Episodes.Watched);
        Assert.Equal(2400, summary.Episodes.CreditedSeconds, 1); // the tick credits nothing
        Assert.Equal(1, summary.Series.Finished);
        Assert.Equal(3, summary.Streaks.LongestDays);
        Assert.Equal(3, summary.Streaks.CurrentDays);
        Assert.Equal("Deep Space Nine", summary.Series.MostWatched?.Name);
    }

    [Fact]
    public void AStreakSurvivesUntilTheEndOfTheNextDay()
    {
        var days = new SortedSet<DateOnly> { new(2026, 10, 1), new(2026, 10, 2), new(2026, 10, 5) };
        var streaks = HistoryQueries.ComputeStreaks(days, new DateOnly(2026, 10, 6));
        Assert.Equal(1, streaks.CurrentDays);
        Assert.Equal(2, streaks.LongestDays);
        Assert.Equal(new DateOnly(2026, 10, 1), streaks.LongestFrom);
    }

    [Fact]
    public void ALateEpisodeBelongsToTheEveningItStartedIn()
    {
        var oneAm = new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 27), HistoryQueries.ToLocalDay(oneAm, TimeZoneInfo.Utc));
    }
}

public class ChangeFeedTests
{
    [Fact]
    public void DeltasCarryOnFromTheLastToken()
    {
        using var h = new Harness();
        var queries = new HistoryQueries(h.Database);
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 600), 600);

        var first = queries.Changes(Harness.Jon.Id, null, 100);
        Assert.Single(first.Changed);

        var nothing = queries.Changes(Harness.Jon.Id, first.Next, 100);
        Assert.Empty(nothing.Changed);

        h.Marked(Harness.Episode(), Harness.Tuesday.AddDays(1), played: true);
        var next = queries.Changes(Harness.Jon.Id, nothing.Next, 100);
        Assert.Equal("Marked", Assert.Single(next.Changed).Kind);
    }

    [Fact]
    public void ARetractedTickArrivesAsARemoval()
    {
        using var h = new Harness();
        var queries = new HistoryQueries(h.Database);
        h.Marked(Harness.Film(), Harness.Tuesday, played: true);
        var token = queries.Changes(Harness.Jon.Id, null, 100).Next;
        h.Marked(Harness.Film(), Harness.Tuesday.Minutes(1), played: false);

        var feed = queries.Changes(Harness.Jon.Id, token, 100);
        Assert.Single(feed.Removed);
        Assert.Empty(feed.Changed);
    }

    [Fact]
    public void ATokenFromAnotherDatabaseStartsAgain()
    {
        using var h = new Harness();
        var feed = new HistoryQueries(h.Database).Changes(Harness.Jon.Id, "c29tZXRoaW5nLWVsc2U6NDI", 100);
        Assert.True(feed.Resync);
    }

    [Fact]
    public void PagesFollowOneAnother()
    {
        using var h = new Harness();
        var queries = new HistoryQueries(h.Database);
        for (var i = 0; i < 5; i++)
        {
            h.Marked(Harness.Film(id: Guid.NewGuid(), tmdb: i.ToString(System.Globalization.CultureInfo.InvariantCulture)), Harness.Tuesday.AddDays(i), played: true);
        }

        var page = queries.Changes(Harness.Jon.Id, null, 2);
        var seen = page.Changed.Count;
        while (page.More)
        {
            page = queries.Changes(Harness.Jon.Id, page.Next, 2);
            seen += page.Changed.Count;
        }

        Assert.Equal(5, seen);
    }
}
