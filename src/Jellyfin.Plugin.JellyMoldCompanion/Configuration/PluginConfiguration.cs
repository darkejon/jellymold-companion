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
    /// setting that changes Jellyfin's own watch data. On unless switched off (decided 2026-11-14; it started off). Jellyfin
    /// 12 fixed this itself, so there it does nothing either way.
    /// </summary>
    public bool FixIdleStopGuess { get; set; } = true;

    /// <summary>
    /// Extract new items' embedded subtitles ahead of time, so the first time one is chosen it appears at once (§16 idea 3).
    /// On unless switched off (decided 2026-11-14; it started off).
    /// </summary>
    public bool PrepareSubtitles { get; set; } = true;

    /// <summary>When subtitles were last prepared, so each run picks up from there.</summary>
    public DateTime? SubtitlesPreparedUntil { get; set; }

    /// <summary>
    /// Measure the black bars built into each file's picture, so JellyMold opens a widescreen film already cropped (§16
    /// idea 11). It only reads the files, a keyframe at a time and at low priority, so it is on until switched off.
    /// </summary>
    public bool MeasurePictureCrop { get; set; } = true;

    // — Ratings —

    /// <summary>
    /// The server owner's own MDBList API key (design §16 idea 13), free from mdblist.com. Ratings are fetched with it, and
    /// offered to apps, only once MDBList has accepted it. Never logged, and never in any answer but this page's own.
    /// </summary>
    public string? MdbListApiKey { get; set; }

    /// <summary>Fetch ratings while there is a key. On by default: with no key it does nothing.</summary>
    public bool FetchRatings { get; set; } = true;

    // — Stats on the web —

    /// <summary>
    /// JellyMold Stats in everyone's Jellyfin menu, opening the stats page (design §11). On unless an admin takes it away
    /// from the settings page (decided 2026-11-16): it is how people who are not admins find their stats.
    /// </summary>
    public bool StatsInEveryonesMenu { get; set; } = true;

    // — Updates —

    /// <summary>
    /// Restart Jellyfin, in quiet hours, when a new version of this plugin is waiting for a restart (design §13a, §19
    /// decision 19): Jellyfin installs updates by itself but never restarts for them. On unless switched off (decided
    /// 2026-11-19); never with Playback Reporting installed, whose presence stops Jellyfin's own restart.
    /// </summary>
    public bool RestartForUpdates { get; set; } = true;

    /// <summary>When the quiet hours start, on the server's clock, <c>HH:mm</c>. They last three hours.</summary>
    public string RestartForUpdatesAt { get; set; } = "04:00";

    /// <summary>The version the plugin last restarted Jellyfin for, so a restart that did not load it is never repeated.</summary>
    public string? RestartedForVersion { get; set; }

    // — Keeping —

    /// <summary>Progress samples are diagnostics; the history is kept regardless. 0 keeps samples for good.</summary>
    public int SampleRetentionDays { get; set; } = 30;

    public int DailyBackupsToKeep { get; set; } = 7;
    public int WeeklyBackupsToKeep { get; set; } = 4;
}
