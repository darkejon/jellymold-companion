using System.Globalization;
using JellyMoldCompanion.Model;

namespace JellyMoldCompanion.Recording;

/// <summary>How a viewing came to be completed.</summary>
public enum CompletedBy
{
    /// <summary>Reached the completion point by playing (or seeking) there.</summary>
    Position,
    /// <summary>Ticked watched — by a person, or by an app confirming the end of a play.</summary>
    Mark,
    /// <summary>An import said so.</summary>
    Import,
}

/// <summary>Design §5.2 and §5.3: when something counts as watched, and what time it is credited.</summary>
public static class Completion
{
    /// <summary>
    /// Whether a viewing has reached its completion point.
    /// </summary>
    /// <param name="furthestTicks">The furthest position reached, for video and audiobooks.</param>
    /// <param name="heardSeconds">Seconds actually played, for music — Last.fm's rule is time heard, not position,
    /// so skipping to the middle of a song does not count.</param>
    public static bool IsComplete(MediaKind kind, long? runtimeTicks, long furthestTicks, double heardSeconds, CountingRules rules)
    {
        if (runtimeTicks is not > 0)
        {
            // No running time, no completion point: only a tick can complete it.
            return false;
        }

        var runtime = TimeSpan.FromTicks(runtimeTicks.Value);
        switch (kind)
        {
            case MediaKind.Audio:
                var needed = Math.Min(runtime.TotalSeconds * rules.MusicListenedFraction, rules.MusicListenedCap.TotalSeconds);
                return heardSeconds >= needed;

            case MediaKind.AudioBook:
                var remaining = runtime - TimeSpan.FromTicks(furthestTicks);
                return remaining.TotalMinutes < rules.MaxAudiobookResumeMinutes;

            default:
                return furthestTicks >= runtimeTicks.Value * (rules.MaxResumePercent / 100.0);
        }
    }

    /// <summary>
    /// Credited time (decided 2026-10-30): the full running time when the viewing reached its completion point by
    /// playing; otherwise only the time actually watched, never more than the running time. A tick counts as
    /// watched and adds no time. Music has two marks: it *counts* at half or four minutes, but is credited in full
    /// only once 90% has been heard.
    /// </summary>
    public static double Credited(
        MediaKind kind,
        long? runtimeTicks,
        double actualSeconds,
        CompletedBy? completedBy,
        CountingRules rules)
    {
        var runtime = runtimeTicks is > 0 ? TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds : (double?)null;
        var actual = runtime is double cap ? Math.Min(actualSeconds, cap) : actualSeconds;

        if (runtime is not double full)
        {
            return actual;
        }

        if (kind == MediaKind.Audio)
        {
            return actualSeconds >= full * rules.MusicCreditFraction ? full : actual;
        }

        return completedBy is CompletedBy.Position or CompletedBy.Import ? full : actual;
    }

    /// <summary>A short label for the rule a viewing was judged by, kept with it so a later change of setting does not rewrite the past.</summary>
    public static string RuleLabel(MediaKind kind, CountingRules rules) => kind switch
    {
        MediaKind.Audio => string.Create(
            CultureInfo.InvariantCulture,
            $"music:{rules.MusicListenedFraction:0.##}/{rules.MusicListenedCap.TotalSeconds:0}s"),
        MediaKind.AudioBook => string.Create(CultureInfo.InvariantCulture, $"audiobook:{rules.MaxAudiobookResumeMinutes:0.##}min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"pct:{rules.MaxResumePercent:0.##}"),
    };
}
