using System.Globalization;

namespace JellyMoldCompanion.Updates;

/// <summary>What the plugin knows when it decides whether to restart Jellyfin to finish its own update.</summary>
/// <param name="Running">This plugin's version, as loaded.</param>
/// <param name="Waiting">A newer version of this plugin, installed and waiting for Jellyfin to restart; or none.</param>
/// <param name="On">The setting.</param>
/// <param name="At">The setting: when, on the server's clock.</param>
/// <param name="PlaybackReporting">Playback Reporting is installed, so Jellyfin's own restart stops the server.</param>
/// <param name="RestartedFor">The version the plugin last restarted Jellyfin for.</param>
/// <param name="SomeonePlaying">Anyone is playing anything, paused included.</param>
/// <param name="TaskRunning">One of Jellyfin's scheduled tasks is running (a library scan, a backup…).</param>
public sealed record UpdateFacts(
    Version Running,
    Version? Waiting,
    bool On,
    TimeOnly At,
    bool PlaybackReporting,
    Version? RestartedFor,
    bool SomeonePlaying,
    bool TaskRunning);

/// <summary>Why the plugin will not restart Jellyfin for an update that is waiting.</summary>
public enum NoRestart
{
    /// <summary>Switched off on the settings page.</summary>
    Off,

    /// <summary>Playback Reporting is installed: Jellyfin's own restart would stop the server, not restart it.</summary>
    PlaybackReporting,

    /// <summary>The plugin restarted Jellyfin for this version once, and it is still waiting: not again.</summary>
    Tried,
}

/// <summary>What the plugin will do about an update waiting for a restart.</summary>
/// <param name="Waiting">The version waiting, or none (and nothing to do).</param>
/// <param name="Never">Why the plugin will not restart Jellyfin for it; none if it will.</param>
/// <param name="Next">When it will: the start of the quiet hours it is in, or of the next.</param>
/// <param name="Now">Restart now: in the quiet hours, nobody playing, no task running.</param>
public sealed record UpdatePlan(Version? Waiting, NoRestart? Never, DateTimeOffset? Next, bool Now)
{
    public static readonly UpdatePlan Nothing = new(null, null, null, false);
}

/// <summary>
/// Finishing an update (design §13a, §19 decision 19). Jellyfin installs a new version of a plugin by itself and leaves
/// it waiting for a restart it never makes, so a server nobody restarts runs the old version for months. The plugin
/// restarts Jellyfin for its own update, once, in quiet hours on the server's clock (from 04:00 by default, for three
/// hours), and only while nobody is playing and no task is running; otherwise it tries the next night.
/// </summary>
public static class UpdateRestart
{
    /// <summary>How long after the set time a restart may still happen, waiting for playback or a task to end.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    public static readonly TimeOnly DefaultAt = new(4, 0);

    /// <summary>The setting's time, <c>HH:mm</c> on a 24-hour clock; anything else is 04:00.</summary>
    public static TimeOnly ParseAt(string? at) =>
        TimeOnly.TryParseExact(at?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : DefaultAt;

    public static UpdatePlan Plan(UpdateFacts facts, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (facts.Waiting is not { } waiting || waiting <= facts.Running)
        {
            return UpdatePlan.Nothing;
        }

        NoRestart? never = !facts.On ? NoRestart.Off
            : facts.PlaybackReporting ? NoRestart.PlaybackReporting
            : facts.RestartedFor == waiting ? NoRestart.Tried
            : null;
        if (never is not null)
        {
            return new UpdatePlan(waiting, never, null, false);
        }

        var (start, inside) = QuietHours(facts.At, now, zone);
        return new UpdatePlan(waiting, null, start, inside && !facts.SomeonePlaying && !facts.TaskRunning);
    }

    /// <summary>The start of the quiet hours <paramref name="now"/> is in, or else of the next; and whether it is in them.</summary>
    public static (DateTimeOffset Start, bool Inside) QuietHours(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        foreach (var day in new[] { -1, 0, 1 })
        {
            var start = local.Date.AddDays(day) + at.ToTimeSpan();
            if (local < start + Window)
            {
                // Yesterday's hours, if they run past midnight and have not ended; today's, if they have not ended; tomorrow's.
                return (new DateTimeOffset(start, zone.GetUtcOffset(start)), local >= start);
            }
        }

        throw new InvalidOperationException("Unreachable: tomorrow's quiet hours always lie ahead.");
    }
}
