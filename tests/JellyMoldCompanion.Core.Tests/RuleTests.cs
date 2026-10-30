using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §5.1–§5.3 as plain functions.</summary>
public class IntervalRuleTests
{
    private static readonly CountingRules Rules = new();
    private static readonly DateTimeOffset T0 = Harness.Tuesday;

    private static Sample At(double seconds, double position, bool paused = false) =>
        new(T0.Seconds(seconds), Harness.Ticks(position), paused);

    [Fact]
    public void NormalPlayCountsThePositionsAdvance()
    {
        var o = IntervalRule.Measure(At(0, 100), At(10, 110), Rules);
        Assert.Equal(10, o.ActualSeconds, 3);
        Assert.Equal(10, o.WallSeconds, 3);
        Assert.False(o.IsSeek);
    }

    [Fact]
    public void PausedCountsNothingAndKeepsThePause()
    {
        var o = IntervalRule.Measure(At(0, 100, paused: true), At(300, 100), Rules);
        Assert.Equal(0, o.ActualSeconds);
        Assert.Equal(300, o.PausedSeconds, 3);
    }

    [Fact]
    public void DoubleSpeedIsTwiceTheFilmInTheSameEvening()
    {
        var o = IntervalRule.Measure(At(0, 0), At(600, 1200), Rules);
        Assert.Equal(1200, o.ActualSeconds, 3);
        Assert.Equal(600, o.WallSeconds, 3);
    }

    [Fact]
    public void AForwardSeekCountsOnlyThePlayingBeforeIt()
    {
        var o = IntervalRule.Measure(At(0, 100), At(3, 1000), Rules);
        Assert.True(o.IsSeek);
        Assert.Equal(3, o.ActualSeconds, 3);
    }

    [Fact]
    public void ABackwardSeekCountsOnlyThePlayingBeforeIt()
    {
        var o = IntervalRule.Measure(At(0, 1000), At(4, 700), Rules);
        Assert.True(o.IsSeek);
        Assert.Equal(4, o.ActualSeconds, 3);
    }

    [Fact]
    public void ASeekReportedLateIsCappedAtFifteenSeconds()
    {
        var o = IntervalRule.Measure(At(0, 100), At(120, 5000), Rules);
        Assert.Equal(15, o.ActualSeconds, 3);
    }

    [Fact]
    public void BufferingAddsNothingBecauseThePositionDidNotMove()
    {
        var o = IntervalRule.Measure(At(0, 100), At(30, 100), Rules);
        Assert.Equal(0, o.ActualSeconds);
        Assert.False(o.IsSeek);
    }

    [Fact]
    public void ReportsOutOfOrderCountNothing()
    {
        var o = IntervalRule.Measure(At(10, 100), At(5, 110), Rules);
        Assert.Equal(IntervalOutcome.None, o);
    }
}

public class CompletionTests
{
    private static readonly CountingRules Rules = new();

    [Theory]
    [InlineData(2375, false)]
    [InlineData(2376, true)] // 90% of 44 minutes is 39:36
    public void VideoIsWatchedAtNinetyPercent(double furthest, bool complete) =>
        Assert.Equal(complete, Completion.IsComplete(MediaKind.Episode, Harness.Ticks(2640), Harness.Ticks(furthest), 0, Rules));

    [Fact]
    public void AnAudiobookIsFinishedWithUnderFiveMinutesLeft()
    {
        var twelveHours = Harness.Ticks(12 * 3600);
        Assert.False(Completion.IsComplete(MediaKind.AudioBook, twelveHours, Harness.Ticks((12 * 3600) - 400), 0, Rules));
        Assert.True(Completion.IsComplete(MediaKind.AudioBook, twelveHours, Harness.Ticks((12 * 3600) - 240), 0, Rules));
        // 90% of a twelve-hour book would leave seventy minutes unheard, which is why books have their own rule.
        Assert.False(Completion.IsComplete(MediaKind.AudioBook, twelveHours, Harness.Ticks(12 * 3600 * 0.9), 0, Rules));
    }

    [Fact]
    public void ASongCountsAtHalfOfItHeard()
    {
        Assert.False(Completion.IsComplete(MediaKind.Audio, Harness.Ticks(200), Harness.Ticks(200), 99, Rules));
        Assert.True(Completion.IsComplete(MediaKind.Audio, Harness.Ticks(200), 0, 100, Rules));
    }

    [Fact]
    public void ALongSongCountsAtFourMinutesHeard() =>
        Assert.True(Completion.IsComplete(MediaKind.Audio, Harness.Ticks(900), 0, 240, Rules));

    [Fact]
    public void SkippingToTheMiddleOfASongIsNotListeningToIt() =>
        Assert.False(Completion.IsComplete(MediaKind.Audio, Harness.Ticks(200), Harness.Ticks(150), 10, Rules));

    [Fact]
    public void NoRunningTimeMeansOnlyATickCanComplete() =>
        Assert.False(Completion.IsComplete(MediaKind.Movie, null, Harness.Ticks(99999), 99999, Rules));

    [Fact]
    public void ReachingTheEndCreditsTheFullRunningTime() =>
        Assert.Equal(2640, Completion.Credited(MediaKind.Episode, Harness.Ticks(2640), 2400, CompletedBy.Position, Rules));

    [Fact]
    public void ATickCreditsOnlyWhatWasWatched()
    {
        Assert.Equal(2244, Completion.Credited(MediaKind.Episode, Harness.Ticks(2640), 2244, CompletedBy.Mark, Rules));
        Assert.Equal(0, Completion.Credited(MediaKind.Movie, Harness.Ticks(7200), 0, CompletedBy.Mark, Rules));
    }

    [Fact]
    public void UnfinishedNeverCreditsMoreThanTheRunningTime() =>
        Assert.Equal(2640, Completion.Credited(MediaKind.Episode, Harness.Ticks(2640), 5000, null, Rules));

    [Fact]
    public void ASongIsCreditedInFullOnlyAtNinetyPercentHeard()
    {
        Assert.Equal(150, Completion.Credited(MediaKind.Audio, Harness.Ticks(200), 150, CompletedBy.Position, Rules));
        Assert.Equal(200, Completion.Credited(MediaKind.Audio, Harness.Ticks(200), 180, CompletedBy.Position, Rules));
    }
}

public class MediaKeysTests
{
    [Fact]
    public void AFilmAnswersToEachProviderId()
    {
        var film = Harness.Film() with
        {
            ProviderIds = new Dictionary<string, string> { ["TMDB"] = "603", ["Imdb"] = "tt0133093", ["Empty"] = "" },
        };
        Assert.Equal(["tmdb:603", "imdb:tt0133093"], MediaKeys.For(film));
    }

    [Fact]
    public void AnEpisodeIsItsSeriesAndNumbers() =>
        Assert.Contains("tvdb-series:73244|s03e07", MediaKeys.For(Harness.Episode()));

    [Fact]
    public void AnEpisodeWithoutNumbersHasNoSeriesKey() =>
        Assert.Empty(MediaKeys.For(Harness.Episode() with { EpisodeNumber = null }));

    [Fact]
    public void ATrackWithoutMusicBrainzIsArtistAlbumAndTitle() =>
        Assert.Equal(["t:an artist|an album|a song"], MediaKeys.For(Harness.Song()));
}
