using JellyMoldCompanion.Model;

namespace JellyMoldCompanion.Recording;

/// <summary>
/// Something Jellyfin said, turned into a plain value by the adapter and dropped on the recorder's queue
/// (design §2). The recorder applies them one at a time, in the order they arrived.
/// </summary>
public abstract record RecorderMessage(DateTimeOffset At);

public enum ReportKind
{
    Start,
    Progress,
    Stop,
}

public sealed record RecordedUser(Guid Id, string Name);

/// <summary>A start, progress or stop report for one play.</summary>
public sealed record PlaybackReport(DateTimeOffset At) : RecorderMessage(At)
{
    public required ReportKind Kind { get; init; }

    /// <summary>Jellyfin's session: one app on one device. Plays one thing at a time.</summary>
    public required string JellyfinSessionId { get; init; }

    /// <summary>The play's own id, when the client sends one. Jellyfin's idle stops never carry one.</summary>
    public string? PlaySessionId { get; init; }

    /// <summary>The session's user and any others added to it. Each gets a session of their own.</summary>
    public required IReadOnlyList<RecordedUser> Users { get; init; }

    public required MediaSnapshot Item { get; init; }

    public long? PositionTicks { get; init; }
    public bool IsPaused { get; init; }

    /// <summary>
    /// True for the progress Jellyfin invents itself: a one-second timer that adds a second a second to the last
    /// position the client gave, whatever the speed, and keeps going after the client has vanished (design §4).
    /// Never counted, never proof of life.
    /// </summary>
    public bool IsAutomated { get; init; }

    public string? Client { get; init; }
    public string? ClientVersion { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceId { get; init; }
    public string? PlayMethod { get; init; }
    public string? MediaSourceId { get; init; }
}

/// <summary>Jellyfin removed a session — the app's connection closed, a sign-out, a shutdown — with no stop for what it was playing.</summary>
public sealed record JellyfinSessionEnded(DateTimeOffset At, string JellyfinSessionId) : RecorderMessage(At);

/// <summary>Why Jellyfin saved a person's watch data for an item. Mirrors Jellyfin's own reasons.</summary>
public enum SaveReason
{
    Other,
    PlaybackStart,
    PlaybackProgress,
    PlaybackFinished,
    /// <summary>A tick or untick — by a person, or by an app such as JellyMold marking an episode it finished.</summary>
    TogglePlayed,
    UpdateUserRating,
    Import,
    UpdateUserData,
}

public sealed record UserDataSaved(DateTimeOffset At) : RecorderMessage(At)
{
    public required RecordedUser User { get; init; }
    public required MediaSnapshot Item { get; init; }
    public required SaveReason Reason { get; init; }
    public required bool Played { get; init; }
    public required long PositionTicks { get; init; }
}

/// <summary>
/// An item left the library. Jellyfin sends one of these for a series, season or album and nothing for what was
/// inside it, so the recorder marks the children too.
/// </summary>
public sealed record LibraryItemRemoved(DateTimeOffset At, Guid ItemId) : RecorderMessage(At);

public sealed record JellyfinUserDeleted(DateTimeOffset At, Guid UserId) : RecorderMessage(At);

/// <summary>
/// Told when Jellyfin's idle timer has stopped a play the client abandoned (§6.1): where the viewer really was, so the
/// plugin can put Jellyfin's resume point back if asked to (§16 idea 10).
/// </summary>
public sealed record IdleStop(Guid UserId, Guid ItemId, MediaKind Kind, long LastPositionTicks, long FurthestTicks, long? RuntimeTicks);

/// <summary>Sent by the recorder's own timer: close what nothing else will (design §4, the sweeper).</summary>
public sealed record SweepTick(DateTimeOffset At) : RecorderMessage(At);

/// <summary>A message someone is waiting on an answer to: told when the recorder could not apply it.</summary>
public interface IReplyingMessage
{
    void Fail(Exception error);
}

/// <summary>Check an import's candidates against what is recorded, keep them, and report what a commit would do (§8.1).</summary>
public sealed record ImportPreviewRequest(DateTimeOffset At) : RecorderMessage(At), IReplyingMessage
{
    public required string Source { get; init; }

    /// <summary>The person it is for; null for a server-wide import (Playback Reporting).</summary>
    public Guid? UserId { get; init; }

    /// <summary>Their name, so an import with nothing in it still says whose it was.</summary>
    public string? UserName { get; init; }

    public required IReadOnlyList<Import.ImportCandidate> Candidates { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public TaskCompletionSource<Import.ImportReport> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Fail(Exception error) => Done.TrySetException(error);
}

public sealed record ImportCommitRequest(DateTimeOffset At, Guid ImportId) : RecorderMessage(At), IReplyingMessage
{
    public TaskCompletionSource<Import.ImportReport> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Fail(Exception error) => Done.TrySetException(error);
}

public sealed record ImportUndoRequest(DateTimeOffset At, Guid ImportId) : RecorderMessage(At), IReplyingMessage
{
    public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Fail(Exception error) => Done.TrySetException(error);
}

/// <summary>
/// Any other write — the app's documents, quiz scores, segments — done on the recorder's thread so there is still one
/// writer (§2), inside a transaction that stamps the change counter. The answer comes back through <see cref="Done"/>.
/// </summary>
public sealed record WriteRequest(DateTimeOffset At, Func<Storage.HistoryStore, object?> Work) : RecorderMessage(At), IReplyingMessage
{
    public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Fail(Exception error) => Done.TrySetException(error);
}

/// <summary>Drop progress samples older than <see cref="Before"/> — diagnostics, kept 30 days by default (§3).</summary>
public sealed record TrimSamples(DateTimeOffset At, DateTimeOffset Before) : RecorderMessage(At);

/// <summary>
/// Watching an app did offline, posted afterwards (§6.3, §9.3). Goes through the recorder's queue like everything
/// else, so it is ordered with live reports; the answer comes back through <see cref="Done"/>.
/// </summary>
public sealed record ClientSessionSubmission(DateTimeOffset At) : RecorderMessage(At), IReplyingMessage
{
    public void Fail(Exception error) => Done.TrySetException(error);

    public required RecordedUser User { get; init; }

    /// <summary>Each session, with the item as the library has it now — null when it has gone from the library.</summary>
    public required IReadOnlyList<(Api.ClientSessionInput Input, MediaSnapshot? Item)> Sessions { get; init; }

    public TaskCompletionSource<IReadOnlyList<Api.ClientSessionOutcome>> Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
