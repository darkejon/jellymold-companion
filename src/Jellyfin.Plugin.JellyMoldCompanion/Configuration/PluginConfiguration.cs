using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyMoldCompanion.Configuration;

/// <summary>
/// The settings page (design §11). Completion itself is not here: it follows the server's own resume settings
/// (decided 2026-10-30), read from Jellyfin's configuration each time.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    // — What is recorded —

    public bool RecordMovies { get; set; } = true;
    public bool RecordEpisodes { get; set; } = true;
    public bool RecordVideos { get; set; } = true;
    public bool RecordMusic { get; set; } = true;
    public bool RecordAudioBooks { get; set; } = true;

    /// <summary>People not recorded. Everyone is recorded unless listed here.</summary>
    public string[] ExcludedUserIds { get; set; } = [];

    // — Counting —

    public int MinimumVideoSeconds { get; set; } = 60;
    public int MinimumMusicSeconds { get; set; } = 30;
    public int MinimumAudioBookSeconds { get; set; } = 60;
    public int AbandonAfterDays { get; set; } = 60;

    // — Jellyfin's own data —

    /// <summary>
    /// After Jellyfin 10.10's idle timer stops a vanished app at an invented position, put the resume point back where the
    /// viewer was, and take back a watched tick that only the invented position earned (design §16 idea 10). The only
    /// setting that changes Jellyfin's own watch data, so off until chosen. Jellyfin 12 fixed this itself.
    /// </summary>
    public bool FixIdleStopGuess { get; set; }

    /// <summary>Extract new items' embedded subtitles ahead of time, so the first time one is chosen it appears at once (§16 idea 3).</summary>
    public bool PrepareSubtitles { get; set; }

    /// <summary>When subtitles were last prepared, so each run picks up from there.</summary>
    public DateTime? SubtitlesPreparedUntil { get; set; }

    // — Keeping —

    /// <summary>Progress samples are diagnostics; the history is kept regardless. 0 keeps samples for good.</summary>
    public int SampleRetentionDays { get; set; } = 30;

    public int DailyBackupsToKeep { get; set; } = 7;
    public int WeeklyBackupsToKeep { get; set; } = 4;
}
