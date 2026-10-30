namespace JellyMoldCompanion.Api;

// The shapes the HTTP API returns (design §9). Jellyfin serialises these in its own PascalCase style, which the
// app's decoders already expect. Times are seconds; dates are UTC.

/// <summary>What an item was — enough for the app to show it and to merge copies across servers.</summary>
public sealed record ItemRef
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? OriginalTitle { get; init; }
    public int? ProductionYear { get; init; }
    public double? RuntimeSeconds { get; init; }
    public Guid? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public Guid? SeasonId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? EpisodeNumberEnd { get; init; }
    public Guid? AlbumId { get; init; }
    public string? AlbumName { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();
    public bool InLibrary { get; init; }
    public DateTime? RemovedAt { get; init; }
}

/// <summary>One entry in the history: a viewing, or for music and audiobooks a listen.</summary>
public sealed record HistoryEntry
{
    public required Guid Id { get; init; }
    public required ItemRef Item { get; init; }
    /// <summary><c>Played</c>, <c>Marked</c> or <c>Imported</c>.</summary>
    public required string Kind { get; init; }
    public required string Source { get; init; }
    /// <summary><c>InProgress</c>, <c>Completed</c>, <c>Abandoned</c> — or, in the change feed only, <c>Retracted</c> and <c>Undone</c>.</summary>
    public required string Status { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime LastActivityAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? CompletedBy { get; init; }
    public double FurthestSeconds { get; init; }
    public double ActualSeconds { get; init; }
    public double WallSeconds { get; init; }
    public double CreditedSeconds { get; init; }
    public bool Estimated { get; init; }
    /// <summary>1 for the first time this person watched it, 2 for the second…</summary>
    public int Ordinal { get; init; }
    public int Sessions { get; init; }
}

public sealed record SessionEntry
{
    public required Guid Id { get; init; }
    public required Guid ItemId { get; init; }
    public Guid? EntryId { get; init; }
    public required string Source { get; init; }
    public string? Client { get; init; }
    public string? DeviceName { get; init; }
    public string? PlayMethod { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public double StartPositionSeconds { get; init; }
    public double? EndPositionSeconds { get; init; }
    public double FurthestSeconds { get; init; }
    public double ActualSeconds { get; init; }
    public double WallSeconds { get; init; }
    public double PausedSeconds { get; init; }
    public int Seeks { get; init; }
    public string? EndReason { get; init; }
    public bool Counted { get; init; }
}

public sealed record EventEntry(DateTime At, string Kind, Guid? ItemId, Guid? SessionId, double? PositionSeconds, string? Detail);

public sealed record Page<T>(IReadOnlyList<T> Items, int TotalRecordCount, int StartIndex);

public sealed record ChangeFeed
{
    public required IReadOnlyList<HistoryEntry> Changed { get; init; }
    /// <summary>Entries that no longer count — a tick taken back, an import undone. Drop them from a cached copy.</summary>
    public required IReadOnlyList<Guid> Removed { get; init; }
    /// <summary>The token for next time.</summary>
    public required string Next { get; init; }
    /// <summary>True when there is more to fetch with <see cref="Next"/> straight away.</summary>
    public bool More { get; init; }
    /// <summary>
    /// True when the token given was from another database (restored from a backup, say): this answer starts from
    /// the beginning, and a cached copy should be replaced rather than updated.
    /// </summary>
    public bool Resync { get; init; }
}

// — The stats page in one request (§9.2) —

public sealed record Summary
{
    public required DateTime GeneratedAt { get; init; }
    public required string AsOf { get; init; }
    public required FilmsSummary Films { get; init; }
    public required EpisodesSummary Episodes { get; init; }
    public required SeriesSummary Series { get; init; }
    public required TimeTotals Total { get; init; }
    public required Streaks Streaks { get; init; }
    public required IReadOnlyList<FilmRow> TopFilms { get; init; }
    public required IReadOnlyList<SeriesRow> TopSeries { get; init; }
    public required IReadOnlyList<HistoryEntry> Recent { get; init; }
    public required IReadOnlyList<YearRow> ByYear { get; init; }
    public required MusicSummary Music { get; init; }
    public required AudioBookSummary AudioBooks { get; init; }
}

public sealed record TimeTotals(double ActualSeconds, double CreditedSeconds, double WallSeconds);

public sealed record FilmsSummary
{
    /// <summary>Films watched to the end, each once however often.</summary>
    public int Watched { get; init; }
    /// <summary>Completed viewings, rewatches included.</summary>
    public int Viewings { get; init; }
    public int UnderWay { get; init; }
    public double ActualSeconds { get; init; }
    public double CreditedSeconds { get; init; }
    public double? AverageRuntimeSeconds { get; init; }
    public FilmRow? Longest { get; init; }
    public required IReadOnlyList<FilmRow> Recent { get; init; }
}

public sealed record EpisodesSummary
{
    public int Watched { get; init; }
    public int Viewings { get; init; }
    public double ActualSeconds { get; init; }
    public double CreditedSeconds { get; init; }
    public double? AverageRuntimeSeconds { get; init; }
}

public sealed record SeriesSummary
{
    public int Started { get; init; }
    public int Finished { get; init; }
    public SeriesRow? MostWatched { get; init; }
    public required IReadOnlyList<SeriesRow> Recent { get; init; }
}

public sealed record FilmRow
{
    public required ItemRef Item { get; init; }
    public bool Finished { get; init; }
    /// <summary>Completed viewings.</summary>
    public int Viewings { get; init; }
    public DateTime? LastWatched { get; init; }
    public double? RuntimeSeconds { get; init; }
    /// <summary>How far in, for a film under way. Null for one watched to the end.</summary>
    public double? PositionSeconds { get; init; }
    public double ActualSeconds { get; init; }
    public double CreditedSeconds { get; init; }
}

public sealed record SeriesRow
{
    public Guid? SeriesId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
    public int EpisodesWatched { get; init; }
    /// <summary>Episodes in the library now. Null when the series has gone from it.</summary>
    public int? EpisodesInLibrary { get; init; }
    public bool Finished { get; init; }
    public bool InLibrary { get; init; }
    public DateTime? LastWatched { get; init; }
    public double ActualSeconds { get; init; }
    public double CreditedSeconds { get; init; }
}

public sealed record Streaks(int CurrentDays, int LongestDays, DateOnly? LongestFrom, DateOnly? LongestTo);

public sealed record YearRow(int Year, int Viewings, double ActualSeconds, double CreditedSeconds);

public sealed record MusicSummary
{
    public int Listens { get; init; }
    public double ActualSeconds { get; init; }
    public double CreditedSeconds { get; init; }
    public required IReadOnlyList<NamedCount> TopArtists { get; init; }
    public required IReadOnlyList<NamedCount> TopAlbums { get; init; }
    public required IReadOnlyList<TrackCount> TopTracks { get; init; }
}

public sealed record NamedCount(string Name, int Listens);

public sealed record TrackCount(ItemRef Item, int Listens);

public sealed record AudioBookSummary(int Finished, int UnderWay, double ActualSeconds);

// — Watching done offline (§9.3) —

public sealed record ClientSessionInput
{
    /// <summary>The app's own id for the session: posting it twice is harmless.</summary>
    public required string ClientSessionId { get; init; }
    public required Guid ItemId { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndedAt { get; init; }
    public double StartPositionSeconds { get; init; }
    public double EndPositionSeconds { get; init; }
    public double? FurthestPositionSeconds { get; init; }
    public double ActualSeconds { get; init; }
    public double? WallSeconds { get; init; }
    public double? PausedSeconds { get; init; }
    public double? PlaybackRate { get; init; }
    public string? DeviceName { get; init; }
    public string? Client { get; init; }
}

public sealed record ClientSessionOutcome(string ClientSessionId, string Result, string? Reason, Guid? SessionId);
