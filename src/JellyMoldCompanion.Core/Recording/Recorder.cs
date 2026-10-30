using JellyMoldCompanion.Model;
using JellyMoldCompanion.Storage;
using Microsoft.Extensions.Logging;

namespace JellyMoldCompanion.Recording;

/// <summary>
/// Turns what Jellyfin says into sessions and viewings (design §4–§6). The one writer: <see cref="Apply"/> is
/// called from a single thread, one message at a time, in the order they arrived, and each message is one
/// transaction.
/// </summary>
public sealed class Recorder
{
    private readonly HistoryStore _store;
    private readonly Func<CountingRules> _rules;
    private readonly Func<RecordingScope> _scope;
    private readonly ILogger _logger;

    /// <summary>Plays in progress, by Jellyfin session and person.</summary>
    private readonly Dictionary<(string Jellyfin, Guid User), OpenSession> _open = [];

    /// <summary>Plays Jellyfin's idle timer stopped recently, kept so a client that comes back can carry on (§6.1).</summary>
    private readonly Dictionary<(string Jellyfin, Guid User), (OpenSession Session, DateTimeOffset ClosedAt)> _idleClosed = [];

    public Recorder(HistoryStore store, Func<CountingRules> rules, Func<RecordingScope> scope, ILogger logger)
    {
        _store = store;
        _rules = rules;
        _scope = scope;
        _logger = logger;
    }

    private CountingRules Rules => _rules();

    /// <summary>How many plays are open right now. For diagnostics.</summary>
    public int OpenSessionCount => _open.Count;

    /// <summary>
    /// At startup: sessions the database says are open were cut off by the server going down. Close them where they
    /// last were (§4).
    /// </summary>
    public void Recover(DateTimeOffset now)
    {
        _store.InTransaction(() =>
        {
            foreach (var session in _store.OpenSessions())
            {
                session.EndedAt = session.LastSeenAt;
                session.EndTicks = session.LastTicks;
                session.EndReason = "restart";
                _store.SaveSession(session);
                _store.InsertEvent(session.LastSeenAt, "restart", session.UserId, session.ItemId, session.SessionId,
                    session.LastTicks, new { recoveredAt = now });
            }

            _store.InsertEvent(now, "plugin-started");
        });
    }

    public void Apply(RecorderMessage message)
    {
        switch (message)
        {
            case PlaybackReport { IsAutomated: true }:
                // Jellyfin's own one-second guess. Not evidence of anything (§4).
                return;

            case PlaybackReport report when !_scope().Records(report.Item.Kind):
                return;

            case PlaybackReport report:
                _store.InTransaction(() =>
                {
                    foreach (var user in report.Users.Where(u => _scope().Records(u.Id)))
                    {
                        ApplyReport(report, user);
                    }
                });
                return;

            case JellyfinSessionEnded ended:
                if (_open.Keys.Any(k => k.Jellyfin == ended.JellyfinSessionId))
                {
                    _store.InTransaction(() => EndJellyfinSession(ended));
                }

                return;

            case UserDataSaved saved when _scope().Records(saved.Item.Kind) && _scope().Records(saved.User.Id):
                _store.InTransaction(() => ApplyUserData(saved));
                return;

            case UserDataSaved:
                return;

            case LibraryItemRemoved removed:
                _store.InTransaction(() =>
                {
                    var marked = _store.MarkRemoved(removed.ItemId, removed.At);
                    if (marked > 0)
                    {
                        _store.InsertEvent(removed.At, "item-removed", itemId: removed.ItemId, detail: new { items = marked });
                    }
                });
                return;

            case JellyfinUserDeleted deleted:
                _store.InTransaction(() =>
                {
                    _store.MarkUserDeleted(deleted.UserId, deleted.At);
                    _store.InsertEvent(deleted.At, "user-deleted", deleted.UserId);
                });
                return;

            case SweepTick tick:
                Sweep(tick.At);
                return;

            case TrimSamples trim:
                _store.InTransaction(() => _store.DeleteSamplesBefore(trim.Before));
                return;

            case ClientSessionSubmission submission:
                IReadOnlyList<Api.ClientSessionOutcome> outcomes = [];
                _store.InTransaction(() => outcomes = ApplyClientSessions(submission));
                submission.Done.TrySetResult(outcomes);
                return;
        }
    }

    // — Watching done offline (§6.3, §9.3) —

    private List<Api.ClientSessionOutcome> ApplyClientSessions(ClientSessionSubmission submission)
    {
        var outcomes = new List<Api.ClientSessionOutcome>();
        var user = submission.User;
        _store.UpsertUser(user, submission.At);

        foreach (var (input, item) in submission.Sessions)
        {
            Api.ClientSessionOutcome Reject(string reason) => new(input.ClientSessionId, "Rejected", reason, null);

            var kind = item?.Kind;
            var runtime = item?.RuntimeTicks;
            if (item is null && _store.MediaFacts(input.ItemId) is { } known)
            {
                (kind, runtime) = known;
            }

            if (kind is not { } mediaKind)
            {
                outcomes.Add(Reject("unknownItem"));
                continue;
            }

            if (!_scope().Records(mediaKind) || !_scope().Records(user.Id))
            {
                outcomes.Add(Reject("notRecorded"));
                continue;
            }

            var started = new DateTimeOffset(DateTime.SpecifyKind(input.StartedAt, DateTimeKind.Utc));
            var ended = new DateTimeOffset(DateTime.SpecifyKind(input.EndedAt, DateTimeKind.Utc));
            if (Validate(input, started, ended, runtime, submission.At) is { } problem)
            {
                outcomes.Add(Reject(problem));
                continue;
            }

            if (_store.ClientSessionExists(user.Id, input.ClientSessionId))
            {
                outcomes.Add(new(input.ClientSessionId, "Duplicate", "alreadyPosted", null));
                continue;
            }

            if (_store.OverlapsLiveSession(user.Id, input.ItemId, started, ended))
            {
                // Played while the server could hear it as well: the same watching, already recorded.
                outcomes.Add(new(input.ClientSessionId, "Duplicate", "overlapsLiveSession", null));
                continue;
            }

            if (item is not null)
            {
                _store.UpsertMedia(item, submission.At);
            }

            var start = TimeSpan.FromSeconds(input.StartPositionSeconds).Ticks;
            var end = TimeSpan.FromSeconds(input.EndPositionSeconds).Ticks;
            var furthest = Math.Max(start, TimeSpan.FromSeconds(input.FurthestPositionSeconds ?? input.EndPositionSeconds).Ticks);
            var record = new SessionRecord
            {
                UserId = user.Id,
                ItemId = input.ItemId,
                Kind = mediaKind,
                Source = Sources.Client,
                Client = input.Client,
                DeviceName = input.DeviceName,
                StartedAt = started,
                LastSeenAt = ended,
                ReceivedAt = submission.At,
                StartTicks = start,
                LastTicks = end,
                FurthestTicks = furthest,
                RuntimeTicks = runtime is > 0 ? runtime : null,
                ActualSeconds = input.ActualSeconds,
                WallSeconds = input.WallSeconds ?? input.ActualSeconds,
                PausedSeconds = input.PausedSeconds ?? 0,
                PlaybackRate = input.PlaybackRate,
                ClientSessionId = input.ClientSessionId,
            };

            var keys = item is not null ? MediaKeys.For(item) : [];
            var session = new OpenSession(record, keys);
            var decision = DecideViewing(user.Id, input.ItemId, keys, record.RuntimeTicks, start, started);
            if (decision.Join is { } viewing)
            {
                Attach(session, viewing);
            }
            else
            {
                session.PendingAbandon = decision.Abandon;
            }

            UpdateViewing(session, ended);
            record.EndedAt = ended;
            record.EndTicks = end;
            record.EndReason = "client";
            record.Counted = record.ViewingId is not null;
            _store.SaveSession(record);
            _store.InsertEvent(submission.At, "client-session", user.Id, input.ItemId, record.SessionId, end,
                new { input.ClientSessionId, startedAt = started, endedAt = ended, input.ActualSeconds });
            outcomes.Add(new(input.ClientSessionId, "Accepted", null, record.SessionId));
        }

        return outcomes;
    }

    /// <summary>The bounds in §9.3. A reason when something is wrong, null when it is plausible.</summary>
    private static string? Validate(Api.ClientSessionInput input, DateTimeOffset started, DateTimeOffset ended, long? runtime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input.ClientSessionId) || input.ClientSessionId.Length > 100)
        {
            return "badClientSessionId";
        }

        if (started > now.AddMinutes(5) || ended > now.AddMinutes(5))
        {
            return "inTheFuture";
        }

        if (started < now.AddDays(-365))
        {
            return "olderThanAYear";
        }

        if (ended < started || ended - started > TimeSpan.FromDays(1))
        {
            return "badTimes";
        }

        var elapsed = (ended - started).TotalSeconds;
        if (input.ActualSeconds < 0 || input.ActualSeconds > (elapsed * 3.5) + 60)
        {
            return "implausibleSeconds";
        }

        var limit = runtime is > 0 ? TimeSpan.FromTicks(runtime.Value).TotalSeconds + 60 : double.MaxValue;
        foreach (var position in new[] { input.StartPositionSeconds, input.EndPositionSeconds, input.FurthestPositionSeconds ?? 0 })
        {
            if (position < 0 || position > limit)
            {
                return "positionOutOfRange";
            }
        }

        return null;
    }

    // — Playback reports (§4) —

    private void ApplyReport(PlaybackReport report, RecordedUser user)
    {
        _store.UpsertUser(user, report.At);
        var key = (report.JellyfinSessionId, user.Id);
        var position = report.PositionTicks ?? 0;

        switch (report.Kind)
        {
            case ReportKind.Start:
                if (_open.TryGetValue(key, out var previous))
                {
                    // A new item on a session already playing: Jellyfin sends no stop for the old one.
                    Close(previous, previous.Record.LastSeenAt, previous.Record.LastTicks, "replaced");
                }

                _store.UpsertMedia(report.Item, report.At);
                OpenNew(report, user, position, "start");
                return;

            case ReportKind.Progress:
            {
                if (_open.TryGetValue(key, out var session) && session.Record.ItemId != report.Item.ItemId)
                {
                    Close(session, session.Record.LastSeenAt, session.Record.LastTicks, "replaced");
                    session = null;
                }

                if (session is null && !_open.ContainsKey(key))
                {
                    if (_idleClosed.TryGetValue(key, out var closed) && closed.Session.Record.ItemId == report.Item.ItemId &&
                        report.At - closed.ClosedAt <= Rules.ReopenWindow)
                    {
                        session = Reopen(key, closed.Session, report.At);
                    }
                    else
                    {
                        // A client that never sent a start, or came back too late to carry on.
                        _store.UpsertMedia(report.Item, report.At);
                        session = OpenNew(report, user, position, "start-implied");
                    }
                }

                session ??= _open[key];
                Advance(session, new Sample(report.At, position, report.IsPaused), report);
                return;
            }

            case ReportKind.Stop:
            {
                if (!_open.TryGetValue(key, out var session) || session.Record.ItemId != report.Item.ItemId)
                {
                    // A stop with no play before it — an app replaying a position it kept offline, mostly (§6.3).
                    _store.UpsertMedia(report.Item, report.At);
                    _store.InsertEvent(report.At, "orphan-stop", user.Id, report.Item.ItemId, positionTicks: position,
                        detail: new { client = report.Client, device = report.DeviceName });
                    return;
                }

                var silence = report.At - session.Record.LastSeenAt;
                if (report.PlaySessionId is null && silence > Rules.IdleStopSilence)
                {
                    // Jellyfin's idle timer, not the client. Its position is Jellyfin's invention — 5 to 10 minutes past
                    // where the viewer was — so the session ends at the last real report instead (§6.1).
                    Close(session, session.Record.LastSeenAt, session.Record.LastTicks, "idle",
                        new { reportedPositionTicks = report.PositionTicks, silentSeconds = (int)silence.TotalSeconds });
                    _idleClosed[key] = (session, report.At);
                    return;
                }

                Advance(session, new Sample(report.At, report.PositionTicks ?? session.Record.LastTicks, session.Record.LastPaused), report);
                if (_open.ContainsKey(key))
                {
                    Close(session, report.At, session.Record.LastTicks, "stopped");
                }

                return;
            }
        }
    }

    private OpenSession OpenNew(PlaybackReport report, RecordedUser user, long position, string eventKind)
    {
        var item = report.Item;
        var record = new SessionRecord
        {
            UserId = user.Id,
            ItemId = item.ItemId,
            Kind = item.Kind,
            JellyfinSessionId = report.JellyfinSessionId,
            PlaySessionId = report.PlaySessionId,
            Client = report.Client,
            ClientVersion = report.ClientVersion,
            DeviceName = report.DeviceName,
            DeviceId = report.DeviceId,
            PlayMethod = report.PlayMethod,
            MediaSourceId = report.MediaSourceId,
            StartedAt = report.At,
            LastSeenAt = report.At,
            ReceivedAt = report.At,
            StartTicks = position,
            LastTicks = position,
            FurthestTicks = position,
            LastPaused = report.IsPaused,
            RuntimeTicks = item.RuntimeTicks is > 0 ? item.RuntimeTicks : null,
        };

        var session = new OpenSession(record, MediaKeys.For(item));
        _open[(report.JellyfinSessionId, user.Id)] = session;

        var decision = DecideViewing(record.UserId, record.ItemId, session.Keys, record.RuntimeTicks, position, report.At);
        if (decision.Join is { } viewing)
        {
            Attach(session, viewing);
            viewing.LastActivityAt = report.At;
            _store.SaveViewing(viewing);
        }
        else
        {
            session.PendingAbandon = decision.Abandon;
        }

        _store.InsertEvent(report.At, eventKind, user.Id, item.ItemId, record.SessionId, position,
            new { client = report.Client, device = report.DeviceName, playSession = report.PlaySessionId });
        _store.SaveSession(record);
        return session;
    }

    private OpenSession Reopen((string, Guid) key, OpenSession session, DateTimeOffset at)
    {
        _idleClosed.Remove(key);
        var record = session.Record;
        record.EndedAt = null;
        record.EndTicks = null;
        record.EndReason = null;
        _open[key] = session;
        _store.InsertEvent(at, "reopened", record.UserId, record.ItemId, record.SessionId, record.LastTicks);
        return session;
    }

    /// <summary>One report from the client: the interval since the last one (§5.1), then the viewing (§5.2–§5.5).</summary>
    private void Advance(OpenSession session, Sample next, PlaybackReport? report)
    {
        var s = session.Record;
        var previous = new Sample(s.LastSeenAt, s.LastTicks, s.LastPaused);

        if (IsLoop(s, previous, next))
        {
            LoopAround(session, previous, next, report);
            return;
        }

        var outcome = IntervalRule.Measure(previous, next, Rules);
        s.ActualSeconds += outcome.ActualSeconds;
        s.WallSeconds += outcome.WallSeconds;
        s.PausedSeconds += outcome.PausedSeconds;

        if (outcome.IsSeek)
        {
            s.SeekCount++;
            _store.InsertEvent(next.At, "seek", s.UserId, s.ItemId, s.SessionId, next.PositionTicks,
                new { fromTicks = previous.PositionTicks });
        }

        if (next.IsPaused != previous.IsPaused)
        {
            _store.InsertEvent(next.At, next.IsPaused ? "pause" : "resume", s.UserId, s.ItemId, s.SessionId, next.PositionTicks);
        }

        if (next.At > s.LastSeenAt)
        {
            s.LastSeenAt = next.At;
        }

        s.LastTicks = next.PositionTicks;
        s.LastPaused = next.IsPaused;
        s.FurthestTicks = Math.Max(s.FurthestTicks, next.PositionTicks);
        if (report is not null)
        {
            s.PlayMethod = report.PlayMethod ?? s.PlayMethod;
            s.MediaSourceId = report.MediaSourceId ?? s.MediaSourceId;
            s.PlaySessionId ??= report.PlaySessionId;
        }

        _store.InsertSample(s.SessionId, next.At, next.PositionTicks, next.IsPaused);
        UpdateViewing(session, next.At);
        _store.SaveSession(s);
    }

    /// <summary>A jump from past 90% back to the first few seconds is a loop — repeat one — not a seek (§5.4).</summary>
    private bool IsLoop(SessionRecord s, Sample previous, Sample next) =>
        s.RuntimeTicks is long runtime && !previous.IsPaused && next.At > previous.At &&
        previous.PositionTicks >= runtime * Rules.LoopFromFraction &&
        next.PositionTicks <= runtime * Rules.LoopToFraction;

    private void LoopAround(OpenSession session, Sample previous, Sample next, PlaybackReport? report)
    {
        var s = session.Record;
        var runtime = s.RuntimeTicks!.Value;
        var elapsed = (next.At - previous.At).TotalSeconds;

        // The end of the first time round, then the start of the next, split from the time between the reports.
        var tail = Math.Min(TimeSpan.FromTicks(runtime - previous.PositionTicks).TotalSeconds, elapsed);
        s.ActualSeconds += tail;
        s.WallSeconds += tail;
        s.LastTicks = runtime;
        s.FurthestTicks = runtime;
        var endedAt = previous.At + TimeSpan.FromSeconds(tail);
        s.LastSeenAt = endedAt;
        Close(session, endedAt, runtime, "looped");

        var head = Math.Min(TimeSpan.FromTicks(next.PositionTicks).TotalSeconds, Math.Max(0, elapsed - tail));
        var startedAt = next.At - TimeSpan.FromSeconds(head);
        var restarted = OpenNew(
            report ?? throw new InvalidOperationException("A loop is only seen in a client report."),
            new RecordedUser(s.UserId, string.Empty), 0, "start-loop");
        restarted.Record.StartedAt = startedAt;
        restarted.Record.LastSeenAt = startedAt;
        Advance(restarted, next, report);
    }

    private void Close(OpenSession session, DateTimeOffset endedAt, long endTicks, string reason, object? detail = null)
    {
        var s = session.Record;
        s.EndedAt = endedAt;
        s.EndTicks = endTicks;
        s.EndReason = reason;
        UpdateViewing(session, endedAt);
        s.Counted = s.ViewingId is not null;
        _store.SaveSession(s);
        _store.InsertEvent(endedAt, reason == "stopped" ? "stop" : reason, s.UserId, s.ItemId, s.SessionId, endTicks, detail);

        foreach (var (key, value) in _open.Where(kv => ReferenceEquals(kv.Value, session)).ToList())
        {
            _open.Remove(key);
        }
    }

    private void EndJellyfinSession(JellyfinSessionEnded ended)
    {
        foreach (var (_, session) in _open.Where(kv => kv.Key.Jellyfin == ended.JellyfinSessionId).ToList())
        {
            Close(session, session.Record.LastSeenAt, session.Record.LastTicks, "session-ended");
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        var stale = _open.Values.Where(s => now - s.Record.LastSeenAt > Rules.SweepAfter).Distinct().ToList();
        foreach (var expired in _idleClosed.Where(kv => now - kv.Value.ClosedAt > Rules.ReopenWindow).Select(kv => kv.Key).ToList())
        {
            _idleClosed.Remove(expired);
        }

        if (stale.Count == 0)
        {
            return;
        }

        _store.InTransaction(() =>
        {
            foreach (var session in stale)
            {
                Close(session, session.Record.LastSeenAt, session.Record.LastTicks, "swept");
            }
        });
    }

    // — Viewings (§5.4) —

    private readonly record struct ViewingDecision(ViewingRecord? Join, ViewingRecord? Abandon);

    private ViewingDecision DecideViewing(Guid userId, Guid itemId, IReadOnlyList<string> keys, long? runtime, long start, DateTimeOffset at)
    {
        var latest = _store.LatestViewing(userId, itemId, keys);
        if (latest is null)
        {
            return new(null, null);
        }

        if (latest.IsCompleted)
        {
            // Watching the credits after a break, or a second session that ran on past the end, joins the viewing
            // it finishes. Anything else after a completed viewing is watching it again.
            var trailing = runtime is long r && start >= r * Rules.TrailingStartFraction &&
                           at - latest.LastActivityAt <= Rules.TrailingWindow;
            return trailing ? new(latest, null) : new(null, null);
        }

        if (at - latest.LastActivityAt > Rules.AbandonAfter)
        {
            return new(null, latest);
        }

        if (runtime is long length && start < length * Rules.RestartFraction &&
            start < latest.FurthestTicks - (length * Rules.RestartFraction))
        {
            // Started again from the top.
            return new(null, latest);
        }

        return new(latest, null);
    }

    private static void Attach(OpenSession session, ViewingRecord viewing)
    {
        session.Viewing = viewing;
        session.Record.ViewingId = viewing.ViewingId;
        session.Record.Counted = true;
        session.ViewingBaseActual = viewing.ActualSeconds;
        session.ViewingBaseWall = viewing.WallSeconds;
        session.ViewingBaseFurthest = viewing.FurthestTicks;
    }

    /// <summary>
    /// Brings the session's viewing up to date — creating it if the session has just earned one (§5.5: a short play
    /// that joins nothing is kept, uncounted) — and judges completion (§5.2) and credited time (§5.3).
    /// </summary>
    private void UpdateViewing(OpenSession session, DateTimeOffset at)
    {
        var s = session.Record;
        if (session.Viewing is null)
        {
            var earned = s.ActualSeconds >= Rules.MinimumFor(s.Kind).TotalSeconds ||
                         Completion.IsComplete(s.Kind, s.RuntimeTicks, s.FurthestTicks, s.ActualSeconds, Rules);
            if (!earned)
            {
                return;
            }

            CreateViewingFor(session);
        }

        var v = session.Viewing!;
        // The latest item: a viewing that carried on across a re-rip points at the copy that exists now (§7).
        v.ItemId = s.ItemId;
        v.ActualSeconds = session.ViewingBaseActual + s.ActualSeconds;
        v.WallSeconds = session.ViewingBaseWall + s.WallSeconds;
        v.FurthestTicks = Math.Max(session.ViewingBaseFurthest, s.FurthestTicks);
        v.RuntimeTicks ??= s.RuntimeTicks;
        if (at > v.LastActivityAt)
        {
            v.LastActivityAt = at;
        }

        if (!v.IsCompleted && Completion.IsComplete(v.MediaKind, v.RuntimeTicks, v.FurthestTicks, v.ActualSeconds, Rules))
        {
            v.Status = ViewingStatus.Completed;
            v.CompletedAt = at;
            v.CompletedBy = CompletedBy.Position;
            _store.InsertEvent(at, "completed", v.UserId, v.ItemId, s.SessionId, v.FurthestTicks);
        }

        v.CreditedSeconds = Completion.Credited(v.MediaKind, v.RuntimeTicks, v.ActualSeconds, v.CompletedBy, Rules);
        _store.SaveViewing(v);
    }

    private void CreateViewingFor(OpenSession session)
    {
        var s = session.Record;
        if (session.PendingAbandon is { } old)
        {
            old.Status = ViewingStatus.Abandoned;
            _store.SaveViewing(old);
            _store.InsertEvent(s.StartedAt, "abandoned", old.UserId, old.ItemId, detail: new { viewing = old.ViewingId });
            session.PendingAbandon = null;
        }

        var viewing = new ViewingRecord
        {
            UserId = s.UserId,
            ItemId = s.ItemId,
            MediaKind = s.Kind,
            Kind = ViewingKind.Played,
            Source = s.Source,
            StartedAt = s.StartedAt,
            LastActivityAt = s.StartedAt,
            RuntimeTicks = s.RuntimeTicks,
            Rule = Completion.RuleLabel(s.Kind, Rules),
        };
        Attach(session, viewing);
    }

    // — Jellyfin's own watch data (§6.1, §6.4) —

    private void ApplyUserData(UserDataSaved saved)
    {
        var user = saved.User;
        var item = saved.Item;
        var previous = _store.GetUserItemState(user.Id, item.ItemId);
        if (previous is null || saved.Reason is SaveReason.TogglePlayed or SaveReason.UpdateUserData)
        {
            // Jellyfin saves watch data on every progress report; the snapshot only needs writing when it is new,
            // or when a mark may create a viewing that points at it.
            _store.UpsertUser(user, saved.At);
            _store.UpsertMedia(item, saved.At);
        }

        var reason = saved.Reason.ToString();
        if (previous is { } before)
        {
            if (before.Played != saved.Played)
            {
                _store.InsertEvent(saved.At, saved.Played ? "played-flag-on" : "played-flag-off", user.Id, item.ItemId,
                    positionTicks: saved.PositionTicks, detail: new { reason });
            }

            var wasResumable = !before.Played && before.PositionTicks > 0;
            var isResumable = !saved.Played && saved.PositionTicks > 0;
            if (wasResumable != isResumable)
            {
                _store.InsertEvent(saved.At, isResumable ? "resumable-on" : "resumable-off", user.Id, item.ItemId,
                    positionTicks: saved.PositionTicks, detail: new { reason });
            }
        }

        _store.SetUserItemState(user.Id, item.ItemId, saved.Played, saved.PositionTicks, saved.At);

        // Only a tick or untick is a mark. Jellyfin clearing the flag when something starts playing is not (§6.4).
        if (saved.Reason is not (SaveReason.TogglePlayed or SaveReason.UpdateUserData))
        {
            return;
        }

        if (saved.Played && previous is not { Played: true })
        {
            Mark(user.Id, item, saved.At);
        }
        else if (!saved.Played && previous is not { Played: false })
        {
            Unmark(user.Id, item.ItemId, saved.At);
        }
    }

    private void Mark(Guid userId, MediaSnapshot item, DateTimeOffset at)
    {
        var rules = Rules;

        // 1. Confirming a play — JellyMold ticks an episode it finished, and so do other apps. Matched by media, not
        // only by item: on Jellyfin 12, finishing one version of a film ticks its other versions too, with the
        // same reason a person's tick carries.
        var keys = MediaKeys.For(item);
        var open = _open.Values.FirstOrDefault(s => s.Record.UserId == userId &&
                                                    (s.Record.ItemId == item.ItemId || s.Keys.Intersect(keys).Any()));
        if (open is not null)
        {
            if (open.Viewing is null)
            {
                CreateViewingFor(open);
                UpdateViewing(open, at);
            }

            CompleteByMark(open.Viewing!, at, open.Record.SessionId);
            _store.SaveSession(open.Record);
            return;
        }

        var recent = _store.LatestSessionFor(userId, item.ItemId, keys);
        if (recent is not null)
        {
            var heard = Max(recent.EndedAt ?? recent.LastSeenAt, recent.ReceivedAt);
            if (at - heard <= rules.MarkConfirmWindow)
            {
                var viewing = recent.ViewingId is { } id ? _store.GetViewing(id) : null;
                if (viewing is null)
                {
                    // A short play the tick now vouches for: it becomes the viewing it would not otherwise have earned.
                    viewing = new ViewingRecord
                    {
                        UserId = userId,
                        ItemId = item.ItemId,
                        MediaKind = item.Kind,
                        Kind = ViewingKind.Played,
                        Source = recent.Source,
                        StartedAt = recent.StartedAt,
                        LastActivityAt = recent.EndedAt ?? recent.LastSeenAt,
                        FurthestTicks = recent.FurthestTicks,
                        RuntimeTicks = recent.RuntimeTicks,
                        ActualSeconds = recent.ActualSeconds,
                        WallSeconds = recent.WallSeconds,
                        Rule = Completion.RuleLabel(item.Kind, rules),
                    };
                    recent.ViewingId = viewing.ViewingId;
                    recent.Counted = true;
                    _store.SaveSession(recent);
                }

                CompleteByMark(viewing, at, recent.SessionId);
                return;
            }
        }

        // 2. A duplicate of a completion just recorded.
        var latest = _store.LatestViewing(userId, item.ItemId, keys);
        if (latest is { IsCompleted: true, CompletedAt: { } completedAt } && at - completedAt <= rules.MarkConfirmWindow)
        {
            return;
        }

        // 3. Ticked watched without playing: counts as watched, credits no time (decided 2026-10-30).
        var marked = new ViewingRecord
        {
            UserId = userId,
            ItemId = item.ItemId,
            MediaKind = item.Kind,
            Kind = ViewingKind.Marked,
            Status = ViewingStatus.Completed,
            StartedAt = at,
            LastActivityAt = at,
            CompletedAt = at,
            CompletedBy = CompletedBy.Mark,
            RuntimeTicks = item.RuntimeTicks is > 0 ? item.RuntimeTicks : null,
            Rule = Completion.RuleLabel(item.Kind, rules),
        };
        _store.SaveViewing(marked);
        _store.InsertEvent(at, "marked-played", userId, item.ItemId, detail: new { viewing = marked.ViewingId });
    }

    private void CompleteByMark(ViewingRecord viewing, DateTimeOffset at, Guid sessionId)
    {
        if (!viewing.IsCompleted)
        {
            viewing.Status = ViewingStatus.Completed;
            viewing.CompletedAt = at;
            viewing.CompletedBy = CompletedBy.Mark;
            viewing.CreditedSeconds = Completion.Credited(viewing.MediaKind, viewing.RuntimeTicks, viewing.ActualSeconds,
                viewing.CompletedBy, Rules);
        }

        if (at > viewing.LastActivityAt)
        {
            viewing.LastActivityAt = at;
        }

        _store.SaveViewing(viewing);
        _store.InsertEvent(at, "mark-confirmed", viewing.UserId, viewing.ItemId, sessionId);
    }

    private void Unmark(Guid userId, Guid itemId, DateTimeOffset at)
    {
        var marked = _store.RecentMarkedViewing(userId, itemId, at - Rules.RetractWindow);
        if (marked is not null)
        {
            // A slip of the thumb should not be on the record for good.
            marked.Status = ViewingStatus.Retracted;
            _store.SaveViewing(marked);
            _store.InsertEvent(at, "mark-retracted", userId, itemId, detail: new { viewing = marked.ViewingId });
            return;
        }

        // Having watched something stays true. The untick is noted and changes nothing.
        _store.InsertEvent(at, "marked-unplayed", userId, itemId);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>A play in progress and what the recorder knows about it beyond its row.</summary>
    private sealed class OpenSession(SessionRecord record, IReadOnlyList<string> keys)
    {
        public SessionRecord Record { get; } = record;
        public IReadOnlyList<string> Keys { get; } = keys;
        public ViewingRecord? Viewing { get; set; }

        /// <summary>The viewing this session will abandon if it earns one of its own (§5.4: started again from the top).</summary>
        public ViewingRecord? PendingAbandon { get; set; }

        // What the viewing held from its other sessions when this one joined.
        public double ViewingBaseActual { get; set; }
        public double ViewingBaseWall { get; set; }
        public long ViewingBaseFurthest { get; set; }
    }
}

/// <summary>Who and what is recorded (§11): set on the settings page.</summary>
public sealed record RecordingScope
{
    public static RecordingScope Everything { get; } = new();

    public IReadOnlySet<MediaKind> Kinds { get; init; } = Enum.GetValues<MediaKind>().ToHashSet();

    public IReadOnlySet<Guid> ExcludedUsers { get; init; } = new HashSet<Guid>();

    public bool Records(MediaKind kind) => Kinds.Contains(kind);

    public bool Records(Guid userId) => !ExcludedUsers.Contains(userId);
}
