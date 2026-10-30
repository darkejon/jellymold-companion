using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;

namespace JellyMoldCompanion.Tests;

/// <summary>
/// The recorder end to end on a real database, one scenario per test, each written the way Jellyfin would report
/// it. The first is the design's own worked example (§5.6).
/// </summary>
public class WorkedExampleTests
{
    [Fact]
    public void TheDesignsWorkedExample()
    {
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);

        // 1. Tuesday 21:00. Plays from 0:00; pauses at 20:00 for five minutes; resumes; at 25:00 the app is killed.
        var t = Harness.Tuesday;
        h.Start(episode, t, 0);
        t = h.Play(episode, t, 0, 1200);
        h.Progress(episode, t, 1200, paused: true);
        t = t.Minutes(5);
        h.Progress(episode, t, 1200, paused: false);
        t = h.Play(episode, t, 1200, 1500);

        // Jellyfin's own one-second timer carries on for the vanished app, then its idle timer stops the play
        // seven minutes later — at the position its timer invented.
        for (var i = 1; i <= 30; i++)
        {
            h.Progress(episode, t.Seconds(i), 1500 + i, automated: true);
        }

        h.Stop(episode, t.Minutes(7), 1500 + (7 * 60));

        var first = Assert.Single(h.Sessions());
        Assert.Equal("idle", first.EndReason);
        Assert.Equal(1500, first.Actual, 1);
        Assert.Equal(300, first.Paused, 1);
        Assert.Equal(Harness.Ticks(1500), first.EndTicks);

        var a = Assert.Single(h.Viewings());
        Assert.Equal("in-progress", a.Status);
        Assert.Equal(1500, a.Credited, 1);

        // 2. Wednesday 20:30. Resumes at 24:30, plays to 41:00, stops.
        var wednesday = Harness.Tuesday.AddDays(1).Minutes(-30);
        h.Start(episode, wednesday, 1470);
        var end = h.Play(episode, wednesday, 1470, 2460);
        h.Stop(episode, end, 2460);

        a = Assert.Single(h.Viewings());
        Assert.Equal("completed", a.Status);
        Assert.Equal("position", a.CompletedBy);
        Assert.Equal(2490, a.Actual, 1); // thirty seconds watched twice
        Assert.Equal(2640, a.Credited, 1);
        Assert.Equal(990, h.Sessions()[1].Actual, 1);

        // It completed at the moment it crossed 39:36, not when the session ended.
        Assert.True(a.CompletedAt < end.ToUnixTimeMilliseconds());

        // 3. Twenty minutes later, back for the end credits from 41:00: joins the viewing it finishes.
        var credits = end.Minutes(20);
        h.Start(episode, credits, 2460);
        var creditsEnd = h.Play(episode, credits, 2460, 2640);
        h.Stop(episode, creditsEnd, 2640);

        a = Assert.Single(h.Viewings());
        Assert.Equal(2670, a.Actual, 1);
        Assert.Equal(3, h.Sessions().Count(s => s.ViewingId == a.Id));

        // 4. A month later, from the beginning: a second viewing.
        var later = Harness.Tuesday.AddDays(30);
        h.Start(episode, later, 0);
        h.Stop(episode, h.Play(episode, later, 0, 300), 300);

        var viewings = h.Viewings();
        Assert.Equal(2, viewings.Count);
        Assert.Equal("in-progress", viewings[1].Status);
        Assert.Equal(300, viewings[1].Actual, 1);
    }
}

public class AbandonedSessionTests
{
    [Fact]
    public void ServerInventedProgressIsNeverCounted()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Progress(film, Harness.Tuesday.Seconds(10), 10);
        for (var i = 1; i <= 600; i++)
        {
            h.Progress(film, Harness.Tuesday.Seconds(10 + i), 10 + i, automated: true);
        }

        var session = Assert.Single(h.Sessions());
        Assert.Equal(10, session.Actual, 1);
    }

    [Fact]
    public void AnIdleStopNearTheEndDoesNotCompleteAnything()
    {
        // Left at 80%; Jellyfin's invented position would be past 90% — which is how a nearly finished episode
        // gets ticked watched and leaves Continue Watching (§6.1).
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);
        h.Start(episode, Harness.Tuesday, 0);
        var t = h.Play(episode, Harness.Tuesday, 0, 2112);
        h.Stop(episode, t.Minutes(8), 2112 + 480);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("in-progress", viewing.Status);
        Assert.Equal(Harness.Ticks(2112), viewing.FurthestTicks);
    }

    [Fact]
    public void AClientStopAfterAPauseIsNotAnIdleStop()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        var t = h.Play(film, Harness.Tuesday, 0, 600);
        h.Progress(film, t, 600, paused: true);
        h.Stop(film, t.Seconds(5), 600, playSession: "play-1");

        Assert.Equal("stopped", Assert.Single(h.Sessions()).EndReason);
    }

    [Fact]
    public void AClientThatComesBackWithinHalfAnHourCarriesOnTheSameSession()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        var t = h.Play(film, Harness.Tuesday, 0, 600);

        // A long drop while a buffered stretch played on: Jellyfin idles the play, then the client reports again,
        // having played straight through.
        h.Stop(film, t.Minutes(7), 600 + 420);
        h.Progress(film, t.Minutes(12), 600 + 720);
        h.Stop(film, h.Play(film, t.Minutes(12), 1320, 1500), 1500);

        var session = Assert.Single(h.Sessions());
        Assert.Equal("stopped", session.EndReason);
        Assert.Equal(1500, session.Actual, 1);
        Assert.Contains("reopened", h.EventKinds());
    }

    [Fact]
    public void ASessionThatEndsWithNoStopClosesAtTheLastReport()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        var t = h.Play(film, Harness.Tuesday, 0, 300);
        h.Recorder.Apply(new JellyfinSessionEnded(t.Minutes(3), "phone"));

        var session = Assert.Single(h.Sessions());
        Assert.Equal("session-ended", session.EndReason);
        Assert.Equal(t.ToUnixTimeMilliseconds(), session.EndedAt);
    }

    [Fact]
    public void ANewItemWithNoStopForTheOldOneReplacesIt()
    {
        using var h = new Harness();
        var first = Harness.Episode(episode: 1, id: Guid.NewGuid());
        var second = Harness.Episode(episode: 2, id: Guid.NewGuid());
        h.Start(first, Harness.Tuesday, 0);
        var t = h.Play(first, Harness.Tuesday, 0, 120);
        h.Start(second, t.Seconds(2), 0);

        var sessions = h.Sessions();
        Assert.Equal("replaced", sessions[0].EndReason);
        Assert.Null(sessions[1].EndReason);
    }

    [Fact]
    public void AStopWithNoPlayBeforeItCreditsNothing()
    {
        using var h = new Harness();
        h.Stop(Harness.Film(), Harness.Tuesday, 3000);

        Assert.Empty(h.Sessions());
        Assert.Contains("orphan-stop", h.EventKinds());
    }

    [Fact]
    public void ARestartClosesOpenSessionsWhereTheyLastWere()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Play(film, Harness.Tuesday, 0, 300);

        // The server goes down; a new recorder starts on the same database.
        var after = new Recorder(h.Store, () => h.Rules, () => RecordingScope.Everything,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        after.Recover(Harness.Tuesday.AddHours(1));

        var session = Assert.Single(h.Sessions());
        Assert.Equal("restart", session.EndReason);
        Assert.Equal(300, session.Actual, 1);
    }

    [Fact]
    public void TheSweeperClosesWhatJellyfinNeverDid()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        var t = h.Play(film, Harness.Tuesday, 0, 300);
        h.Recorder.Apply(new SweepTick(t.Minutes(10)));
        Assert.Null(Assert.Single(h.Sessions()).EndReason);

        h.Recorder.Apply(new SweepTick(t.Minutes(16)));
        Assert.Equal("swept", Assert.Single(h.Sessions()).EndReason);
    }
}

public class ViewingRuleTests
{
    [Fact]
    public void AShortPlayIsKeptButCountsForNothing()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 40), 40);

        var session = Assert.Single(h.Sessions());
        Assert.False(session.Counted);
        Assert.Equal(40, session.Actual, 1);
        Assert.Empty(h.Viewings());
    }

    [Fact]
    public void AShortPlayJoinsAViewingAlreadyUnderWay()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 1800), 1800);

        var next = Harness.Tuesday.AddDays(1);
        h.Start(film, next, 1800);
        h.Stop(film, h.Play(film, next, 1800, 1830), 1830);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal(1830, viewing.Actual, 1);
        Assert.All(h.Sessions(), s => Assert.True(s.Counted));
    }

    [Fact]
    public void StartingAgainFromTheTopAbandonsTheFirstAttempt()
    {
        using var h = new Harness();
        var film = Harness.Film(minutes: 120);
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 2880), 2880); // 40%

        var week = Harness.Tuesday.AddDays(7);
        h.Start(film, week, 0);
        h.Stop(film, h.Play(film, week, 0, 600), 600);

        var viewings = h.Viewings();
        Assert.Equal(["abandoned", "in-progress"], viewings.Select(v => v.Status));
        Assert.Equal(2880, viewings[0].Credited, 1); // the time still counts as time, not as a watched film
    }

    [Fact]
    public void ABriefLookAtTheOpeningDoesNotAbandonAViewing()
    {
        using var h = new Harness();
        var film = Harness.Film(minutes: 120);
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 2880), 2880);

        var week = Harness.Tuesday.AddDays(7);
        h.Start(film, week, 0);
        h.Stop(film, h.Play(film, week, 0, 20), 20);

        Assert.Equal("in-progress", Assert.Single(h.Viewings()).Status);
    }

    [Fact]
    public void AViewingLeftForSixtyDaysIsAbandoned()
    {
        using var h = new Harness();
        var film = Harness.Film(minutes: 120);
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 2880), 2880);

        var later = Harness.Tuesday.AddDays(61);
        h.Start(film, later, 2880);
        h.Stop(film, h.Play(film, later, 2880, 3600), 3600);

        Assert.Equal(["abandoned", "in-progress"], h.Viewings().Select(v => v.Status));
    }

    [Fact]
    public void TheSameMediaUnderANewItemIdCarriesOn()
    {
        using var h = new Harness();
        var original = Harness.Film(id: Guid.NewGuid());
        var rerip = Harness.Film(id: Guid.NewGuid());
        h.Start(original, Harness.Tuesday, 0);
        h.Stop(original, h.Play(original, Harness.Tuesday, 0, 3600), 3600);

        var next = Harness.Tuesday.AddDays(1);
        h.Start(rerip, next, 3600);
        h.Stop(rerip, h.Play(rerip, next, 3600, 7000), 7000);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(rerip.ItemId.ToString("N"), viewing.ItemId);
    }

    [Fact]
    public void TwoPeopleOnOnePlayEachGetTheirOwn()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Recorder.Apply(h.Report(ReportKind.Start, film, Harness.Tuesday, 0, users: [Harness.Jon, Harness.Sam]));
        h.Recorder.Apply(h.Report(ReportKind.Progress, film, Harness.Tuesday.Minutes(10), 600, users: [Harness.Jon, Harness.Sam]));

        Assert.Single(h.Viewings(Harness.Jon));
        Assert.Single(h.Viewings(Harness.Sam));
    }

    [Fact]
    public void ExcludedPeopleAndKindsAreNotRecorded()
    {
        using var h = new Harness(scope: new RecordingScope
        {
            ExcludedUsers = new HashSet<Guid> { Harness.Sam.Id },
            Kinds = new HashSet<MediaKind> { MediaKind.Movie },
        });
        h.Recorder.Apply(h.Report(ReportKind.Start, Harness.Film(), Harness.Tuesday, 0, users: [Harness.Sam]));
        h.Start(Harness.Episode(), Harness.Tuesday, 0);

        Assert.Empty(h.Sessions(Harness.Sam));
        Assert.Empty(h.Sessions(Harness.Jon));
    }

    [Fact]
    public void ASeriesRemovedFromTheLibraryMarksEveryEpisode()
    {
        using var h = new Harness();
        var episode = Harness.Episode();
        h.Start(episode, Harness.Tuesday, 0);
        h.Recorder.Apply(new LibraryItemRemoved(Harness.Tuesday.AddDays(1), episode.SeriesId!.Value));

        Assert.Equal(1, h.Scalar("SELECT COUNT(*) FROM media WHERE removed_at IS NOT NULL"));
        Assert.Single(h.Sessions()); // nothing removed
    }
}

public class MarkTests
{
    [Fact]
    public void ATickWithoutPlayingCountsAsWatchedAndCreditsNothing()
    {
        using var h = new Harness();
        h.Marked(Harness.Film(), Harness.Tuesday, played: true);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("marked", viewing.Kind);
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(0, viewing.Credited);
    }

    [Fact]
    public void JellyMoldsOwnTickAtTheCreditsConfirmsThePlayAndCreditsWhatWasPlayed()
    {
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);
        h.Start(episode, Harness.Tuesday, 0);
        var t = h.Play(episode, Harness.Tuesday, 0, 2244); // 85%: the credits start
        h.Stop(episode, t, 2244);
        // Jellyfin's own judgement at the stop: 85% is short of its 90%, so not played.
        h.Marked(episode, t.Seconds(1), played: false, reason: SaveReason.PlaybackFinished, position: 2244);
        h.Marked(episode, t.Seconds(2), played: true);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal("mark", viewing.CompletedBy);
        Assert.Equal(2244, viewing.Credited, 1);
    }

    [Fact]
    public void ATickAfterJellyfinAlreadyTickedItIsNotADuplicate()
    {
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 44);
        h.Start(episode, Harness.Tuesday, 0);
        var t = h.Play(episode, Harness.Tuesday, 0, 2600);
        h.Stop(episode, t, 2600);
        h.Marked(episode, t.Seconds(1), played: true, reason: SaveReason.PlaybackFinished);
        h.Marked(episode, t.Seconds(2), played: true);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("position", viewing.CompletedBy);
    }

    [Fact]
    public void AnUntickWithinADayTakesBackAMarkedViewing()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Marked(film, Harness.Tuesday, played: true);
        h.Marked(film, Harness.Tuesday.Minutes(1), played: false);

        Assert.Equal("retracted", Assert.Single(h.Viewings()).Status);
    }

    [Fact]
    public void AnUntickLaterChangesNothing()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Marked(film, Harness.Tuesday, played: true);
        h.Marked(film, Harness.Tuesday.AddDays(3), played: false);

        Assert.Equal("completed", Assert.Single(h.Viewings()).Status);
        Assert.Contains("marked-unplayed", h.EventKinds());
    }

    [Fact]
    public void JellyfinClearingTheTickWhenPlayStartsIsNotAnUntick()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Marked(film, Harness.Tuesday, played: true);
        h.Marked(film, Harness.Tuesday.Minutes(1), played: false, reason: SaveReason.PlaybackStart);

        Assert.Equal("completed", Assert.Single(h.Viewings()).Status);
        Assert.Contains("played-flag-off", h.EventKinds());
    }

    [Fact]
    public void LeavingContinueWatchingIsOnTheRecord()
    {
        using var h = new Harness();
        var episode = Harness.Episode();
        h.Marked(episode, Harness.Tuesday, played: false, reason: SaveReason.PlaybackProgress, position: 1200);
        h.Marked(episode, Harness.Tuesday.Minutes(8), played: true, reason: SaveReason.PlaybackFinished);

        var events = h.EventKinds();
        Assert.Contains("played-flag-on", events);
        Assert.Contains("resumable-off", events);
    }
}

public class MusicAndBookTests
{
    [Fact]
    public void ASongOnRepeatCountsEveryTimeRound()
    {
        using var h = new Harness();
        var song = Harness.Song(seconds: 200);
        h.Start(song, Harness.Tuesday, 0);
        var t = h.Play(song, Harness.Tuesday, 0, 190);

        // Repeat one: the player jumps back to the start rather than stopping and starting again.
        h.Progress(song, t.Seconds(15), 5);
        h.Stop(song, h.Play(song, t.Seconds(15), 5, 200), 200);

        var viewings = h.Viewings();
        Assert.Equal(2, viewings.Count);
        Assert.All(viewings, v => Assert.Equal("completed", v.Status));
        Assert.Equal(200, viewings[0].Credited, 1);
    }

    [Fact]
    public void ASongSkippedEarlyIsNotAListen()
    {
        using var h = new Harness();
        var song = Harness.Song();
        h.Start(song, Harness.Tuesday, 0);
        h.Stop(song, h.Play(song, Harness.Tuesday, 0, 20), 20);

        Assert.Empty(h.Viewings());
    }

    [Fact]
    public void HalfASongIsAListenCreditedOnlyWhatWasHeard()
    {
        using var h = new Harness();
        var song = Harness.Song(seconds: 200);
        h.Start(song, Harness.Tuesday, 0);
        h.Stop(song, h.Play(song, Harness.Tuesday, 0, 120), 120);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(120, viewing.Credited, 1);
    }

    [Fact]
    public void AnAudiobookFinishesWithUnderFiveMinutesLeft()
    {
        using var h = new Harness();
        var book = Harness.Book(hours: 1);
        h.Start(book, Harness.Tuesday, 0);
        h.Stop(book, h.Play(book, Harness.Tuesday, 0, 3360, step: 60), 3360); // four minutes left

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(3600, viewing.Credited, 1);
    }
}
