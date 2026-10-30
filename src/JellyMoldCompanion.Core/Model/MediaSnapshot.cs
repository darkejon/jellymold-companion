namespace JellyMoldCompanion.Model;

/// <summary>The kinds of item the plugin records (design §6.6). Anything else never reaches the recorder.</summary>
public enum MediaKind
{
    Movie,
    Episode,
    /// <summary>A video that is neither a film nor an episode — home videos, mostly.</summary>
    Video,
    /// <summary>A music track.</summary>
    Audio,
    AudioBook,
}

/// <summary>
/// What an item *was*, kept with every record so the record outlives the file (design §7). Built by the Jellyfin
/// adapter from a library item; nothing here is a Jellyfin type.
/// </summary>
public sealed record MediaSnapshot
{
    public required Guid ItemId { get; init; }
    public required MediaKind Kind { get; init; }
    public required string Name { get; init; }
    public string? OriginalTitle { get; init; }
    public int? ProductionYear { get; init; }
    public DateTime? PremiereDate { get; init; }
    public long? RuntimeTicks { get; init; }

    // Episodes
    public Guid? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public Guid? SeasonId { get; init; }
    public string? SeasonName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? EpisodeNumberEnd { get; init; }

    // Music, and audiobooks, which Jellyfin describes the same way: the book as the album, the author as the artist.
    public Guid? AlbumId { get; init; }
    public string? AlbumName { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }

    /// <summary>Exactly as Jellyfin holds them: <c>{"Tmdb": "603", "Imdb": "tt0133093"}</c>.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    public Guid? LibraryId { get; init; }
    public string? LibraryName { get; init; }

    public TimeSpan? Runtime => RuntimeTicks is > 0 ? TimeSpan.FromTicks(RuntimeTicks.Value) : null;
}
