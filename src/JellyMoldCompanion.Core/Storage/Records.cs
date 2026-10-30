using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;

namespace JellyMoldCompanion.Storage;

public enum ViewingKind
{
    Played,
    Marked,
    Imported,
}

public enum ViewingStatus
{
    InProgress,
    Completed,
    Abandoned,
    /// <summary>A mistaken tick undone at once (§6.4). Kept, not counted.</summary>
    Retracted,
    /// <summary>Its import was undone (§8.1). Kept, not counted.</summary>
    Undone,
}

/// <summary>One continuous play (design §3). Mutable: the recorder updates it as reports arrive.</summary>
public sealed class SessionRecord
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required Guid ItemId { get; init; }
    public required MediaKind Kind { get; init; }
    public Guid? ViewingId { get; set; }
    public string Source { get; init; } = Sources.Live;

    public string? JellyfinSessionId { get; init; }
    public string? PlaySessionId { get; set; }
    public string? Client { get; init; }
    public string? ClientVersion { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceId { get; init; }
    public string? PlayMethod { get; set; }
    public string? MediaSourceId { get; set; }

    public required DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public required DateTimeOffset LastSeenAt { get; set; }
    public required DateTimeOffset ReceivedAt { get; init; }

    public required long StartTicks { get; set; }
    public long? EndTicks { get; set; }
    public required long LastTicks { get; set; }
    public required long FurthestTicks { get; set; }
    public bool LastPaused { get; set; }
    public long? RuntimeTicks { get; init; }

    public double ActualSeconds { get; set; }
    public double WallSeconds { get; set; }
    public double PausedSeconds { get; set; }
    public int SeekCount { get; set; }
    public double? PlaybackRate { get; init; }
    public string? EndReason { get; set; }
    public bool Counted { get; set; }
    public string? ClientSessionId { get; init; }
}

/// <summary>"I watched it" — one or more sessions of an item, until completed or abandoned (design §3, §5.4).</summary>
public sealed class ViewingRecord
{
    public Guid ViewingId { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required Guid ItemId { get; set; }
    public required MediaKind MediaKind { get; init; }
    public required ViewingKind Kind { get; init; }
    public string Source { get; init; } = Sources.Live;
    public Guid? ImportId { get; init; }
    public string? SourceKey { get; init; }
    public ViewingStatus Status { get; set; } = ViewingStatus.InProgress;
    public required DateTimeOffset StartedAt { get; set; }
    public required DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public CompletedBy? CompletedBy { get; set; }
    public long FurthestTicks { get; set; }
    public long? RuntimeTicks { get; set; }
    public double ActualSeconds { get; set; }
    public double WallSeconds { get; set; }
    public double CreditedSeconds { get; set; }
    public bool Estimated { get; init; }
    public int? PlayCountNoted { get; init; }
    public Guid? SupersededBy { get; set; }
    public string? Rule { get; set; }

    public bool IsCompleted => Status == ViewingStatus.Completed;
}

/// <summary>Where a record came from (§3). Strings, because imports will add more without a schema change.</summary>
public static class Sources
{
    public const string Live = "live";
    public const string Client = "client";
    public const string Jellyfin = "jellyfin";
    public const string PlaybackReporting = "playback-reporting";
}

/// <summary>The words the database stores for the enums. Changing one is a migration.</summary>
public static class StoredNames
{
    public static string Of(ViewingKind kind) => kind switch
    {
        ViewingKind.Played => "played",
        ViewingKind.Marked => "marked",
        ViewingKind.Imported => "imported",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(ViewingStatus status) => status switch
    {
        ViewingStatus.InProgress => "in-progress",
        ViewingStatus.Completed => "completed",
        ViewingStatus.Abandoned => "abandoned",
        ViewingStatus.Retracted => "retracted",
        ViewingStatus.Undone => "undone",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string Of(CompletedBy by) => by switch
    {
        CompletedBy.Position => "position",
        CompletedBy.Mark => "mark",
        CompletedBy.Import => "import",
        _ => throw new ArgumentOutOfRangeException(nameof(by)),
    };

    public static string Of(MediaKind kind) => kind.ToString();

    public static ViewingKind ViewingKindFrom(string text) => text switch
    {
        "played" => ViewingKind.Played,
        "marked" => ViewingKind.Marked,
        "imported" => ViewingKind.Imported,
        _ => throw new FormatException($"Unknown viewing kind '{text}'"),
    };

    public static ViewingStatus StatusFrom(string text) => text switch
    {
        "in-progress" => ViewingStatus.InProgress,
        "completed" => ViewingStatus.Completed,
        "abandoned" => ViewingStatus.Abandoned,
        "retracted" => ViewingStatus.Retracted,
        "undone" => ViewingStatus.Undone,
        _ => throw new FormatException($"Unknown viewing status '{text}'"),
    };

    public static CompletedBy? CompletedByFrom(string? text) => text switch
    {
        null => null,
        "position" => CompletedBy.Position,
        "mark" => CompletedBy.Mark,
        "import" => CompletedBy.Import,
        _ => throw new FormatException($"Unknown completion '{text}'"),
    };

    public static MediaKind MediaKindFrom(string text) => Enum.Parse<MediaKind>(text);
}
