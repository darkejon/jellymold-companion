using JellyMoldCompanion.Model;

namespace JellyMoldCompanion.Recording;

/// <summary>
/// Every number the counting rules use (design §5, §6), with the design's defaults. The resume settings come from
/// the server (decided 2026-10-30: completion follows Jellyfin's own settings); the rest are the plugin's.
/// </summary>
public sealed record CountingRules
{
    // — Completion (§5.2) —

    /// <summary>Jellyfin's <c>MaxResumePct</c>: video is watched once this share of it is reached.</summary>
    public double MaxResumePercent { get; init; } = 90;

    /// <summary>Jellyfin's <c>MaxAudiobookResume</c>: a book is finished when fewer minutes than this remain.</summary>
    public double MaxAudiobookResumeMinutes { get; init; } = 5;

    /// <summary>Last.fm's rule (decided 2026-10-30): a song counts once half of it, or four minutes, has been heard.</summary>
    public double MusicListenedFraction { get; init; } = 0.5;

    public TimeSpan MusicListenedCap { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>A song is credited its full running time only once this share of it has been heard (§5.3).</summary>
    public double MusicCreditFraction { get; init; } = 0.9;

    // — Actual time (§5.1) —

    /// <summary>A little above the fastest any client plays. Jellyfin reports no speed, so normal play has to allow for it.</summary>
    public double FastestRate { get; init; } = 3.5;

    public TimeSpan NormalPlaySlack { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A seek counts only the playing before it, and never more than this.</summary>
    public TimeSpan SeekCreditCap { get; init; } = TimeSpan.FromSeconds(15);

    // — Short plays (§5.5) —

    public TimeSpan MinimumVideo { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MinimumMusic { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinimumAudioBook { get; init; } = TimeSpan.FromSeconds(60);

    // — Sessions that end badly (§4, §6.1) —

    /// <summary>A stop this long after the client's last report is Jellyfin's idle timer, not the client.</summary>
    public TimeSpan IdleStopSilence { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Reports resuming within this long after an idle stop reopen the session rather than begin another.</summary>
    public TimeSpan ReopenWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The sweeper closes anything with no real report for this long.</summary>
    public TimeSpan SweepAfter { get; init; } = TimeSpan.FromMinutes(15);

    // — Viewings (§5.4) —

    public double TrailingStartFraction { get; init; } = 0.85;
    public TimeSpan TrailingWindow { get; init; } = TimeSpan.FromHours(6);
    public double RestartFraction { get; init; } = 0.10;
    public TimeSpan AbandonAfter { get; init; } = TimeSpan.FromDays(60);

    /// <summary>A jump from past this share…</summary>
    public double LoopFromFraction { get; init; } = 0.9;

    /// <summary>…to before this one is a loop (repeat one), not a seek: a new play.</summary>
    public double LoopToFraction { get; init; } = 0.05;

    // — Marks (§6.4) —

    public TimeSpan MarkConfirmWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RetractWindow { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan MinimumFor(MediaKind kind) => kind switch
    {
        MediaKind.Audio => MinimumMusic,
        MediaKind.AudioBook => MinimumAudioBook,
        _ => MinimumVideo,
    };
}
