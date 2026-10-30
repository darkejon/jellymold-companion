namespace JellyMoldCompanion.Recording;

/// <summary>One report from the client: when, where, and whether it was paused.</summary>
public readonly record struct Sample(DateTimeOffset At, long PositionTicks, bool IsPaused);

/// <summary>What the time between two reports adds to a session.</summary>
public readonly record struct IntervalOutcome(double ActualSeconds, double WallSeconds, double PausedSeconds, bool IsSeek)
{
    public static IntervalOutcome None => default;
}

/// <summary>
/// Design §5.1, the whole of it: what the interval between two consecutive client reports adds.
///
/// <list type="bullet">
/// <item>Paused at the first report: nothing, and the time is kept as paused.</item>
/// <item>Normal play — the position moved forward no faster than the fastest any client plays: the position's
/// advance is actual time, the clock's advance is wall time. At 2× for ten minutes that is twenty minutes of film
/// and ten of evening.</item>
/// <item>Anything else — a seek either way, a skip, a jump: only the playing before it, capped.</item>
/// </list>
///
/// Jellyfin reports no playback speed, so the rule is written not to need one.
/// </summary>
public static class IntervalRule
{
    public static IntervalOutcome Measure(Sample from, Sample to, CountingRules rules)
    {
        var wall = (to.At - from.At).TotalSeconds;
        if (wall <= 0)
        {
            // Two reports in the same instant, or out of order: nothing has elapsed to count.
            return IntervalOutcome.None;
        }

        if (from.IsPaused)
        {
            return new IntervalOutcome(0, 0, wall, IsSeek: false);
        }

        var moved = TimeSpan.FromTicks(to.PositionTicks - from.PositionTicks).TotalSeconds;
        var ceiling = (wall * rules.FastestRate) + rules.NormalPlaySlack.TotalSeconds;
        if (moved >= 0 && moved <= ceiling)
        {
            return new IntervalOutcome(moved, wall, 0, IsSeek: false);
        }

        var credit = Math.Min(wall, rules.SeekCreditCap.TotalSeconds);
        return new IntervalOutcome(credit, credit, 0, IsSeek: true);
    }
}
