using JellyMoldCompanion.Updates;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §13a, §19 decision 19: restarting Jellyfin, at a quiet hour, to finish the plugin's own update.</summary>
public class UpdateRestartTests
{
    private static readonly Version Running = new(10, 0, 7, 0);
    private static readonly Version Next = new(10, 0, 8, 0);

    // A server five hours behind UTC, with no daylight saving, so the expected instants are plain to read.
    private static readonly TimeZoneInfo Server = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");

    private static UpdateFacts Facts(Version? waiting = null, bool on = true, string at = "04:00", bool playbackReporting = false,
        Version? restartedFor = null, bool playing = false, bool task = false) =>
        new(Running, waiting ?? Next, on, UpdateRestart.ParseAt(at), playbackReporting, restartedFor, playing, task);

    /// <summary>A time on the server's clock, 19 November 2026 unless another day is given.</summary>
    private static DateTimeOffset At(int hour, int minute = 0, int day = 19) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-5));

    private static UpdatePlan Plan(UpdateFacts facts, DateTimeOffset now) => UpdateRestart.Plan(facts, now, Server);

    [Fact]
    public void NothingWaitingIsNothingToDo()
    {
        Assert.Equal(UpdatePlan.Nothing, Plan(Facts() with { Waiting = null }, At(4, 30)));
        Assert.Equal(UpdatePlan.Nothing, Plan(Facts(waiting: Running), At(4, 30)));
        Assert.Equal(UpdatePlan.Nothing, Plan(Facts(waiting: new Version(10, 0, 6, 0)), At(4, 30)));
    }

    [Fact]
    public void AnUpdateWaitingRestartsInTheQuietHoursWhenNothingIsGoingOn()
    {
        var plan = Plan(Facts(), At(4, 5));
        Assert.True(plan.Now);
        Assert.Equal(Next, plan.Waiting);
        Assert.Null(plan.Never);
        Assert.Equal(At(4), plan.Next);
    }

    [Fact]
    public void BeforeTheQuietHoursItWaitsForThem()
    {
        var plan = Plan(Facts(), At(1, 0));
        Assert.False(plan.Now);
        Assert.Equal(At(4), plan.Next);
    }

    [Fact]
    public void AfterTheQuietHoursItWaitsForTomorrows()
    {
        var plan = Plan(Facts(), At(7, 0));
        Assert.False(plan.Now);
        Assert.Equal(At(4, day: 20), plan.Next);

        Assert.True(Plan(Facts(), At(6, 59)).Now);
    }

    [Fact]
    public void SomeonePlayingOrATaskRunningHoldsItWithinTheQuietHours()
    {
        var playing = Plan(Facts(playing: true), At(4, 30));
        Assert.False(playing.Now);
        Assert.Equal(At(4), playing.Next);
        Assert.Null(playing.Never);

        Assert.False(Plan(Facts(task: true), At(4, 30)).Now);
    }

    [Fact]
    public void QuietHoursCanRunPastMidnight()
    {
        Assert.True(Plan(Facts(at: "23:30"), At(1, 0, day: 20)).Now);
        Assert.Equal(At(23, 30, day: 19), Plan(Facts(at: "23:30"), At(1, 0, day: 20)).Next);
        Assert.False(Plan(Facts(at: "23:30"), At(2, 30, day: 20)).Now);
        Assert.Equal(At(23, 30, day: 20), Plan(Facts(at: "23:30"), At(2, 30, day: 20)).Next);
    }

    [Fact]
    public void SwitchedOffItNeverRestarts()
    {
        var plan = Plan(Facts(on: false), At(4, 5));
        Assert.False(plan.Now);
        Assert.Equal(NoRestart.Off, plan.Never);
        Assert.Equal(Next, plan.Waiting);
        Assert.Null(plan.Next);
    }

    [Fact]
    public void WithPlaybackReportingItNeverRestarts()
    {
        // Jellyfin's own restart stops a 10.10.7 server with Playback Reporting installed, and nobody starts it again.
        var plan = Plan(Facts(playbackReporting: true), At(4, 5));
        Assert.False(plan.Now);
        Assert.Equal(NoRestart.PlaybackReporting, plan.Never);
    }

    [Fact]
    public void ItRestartsOnceForAVersion()
    {
        var again = Plan(Facts(restartedFor: Next), At(4, 5));
        Assert.False(again.Now);
        Assert.Equal(NoRestart.Tried, again.Never);

        // A restart for an earlier version says nothing about this one.
        Assert.True(Plan(Facts(restartedFor: new Version(10, 0, 7, 5)), At(4, 5)).Now);
    }

    [Theory]
    [InlineData("04:00", 4, 0)]
    [InlineData("4:30", 4, 30)]
    [InlineData(" 23:15 ", 23, 15)]
    [InlineData("", 4, 0)]
    [InlineData(null, 4, 0)]
    [InlineData("4am", 4, 0)]
    [InlineData("25:00", 4, 0)]
    public void TheTimeIsReadOnATwentyFourHourClock(string? setting, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), UpdateRestart.ParseAt(setting));
    }
}
