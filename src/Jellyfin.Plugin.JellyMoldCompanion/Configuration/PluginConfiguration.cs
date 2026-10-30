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

    // — Keeping —

    /// <summary>Progress samples are diagnostics; the history is kept regardless. 0 keeps samples for good.</summary>
    public int SampleRetentionDays { get; set; } = 30;

    public int DailyBackupsToKeep { get; set; } = 7;
    public int WeeklyBackupsToKeep { get; set; } = 4;
}
