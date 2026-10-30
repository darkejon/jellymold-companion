using System.Globalization;
using System.Text.Json;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.Storage;

/// <summary>
/// The recorder's side of the database: every write, and the few reads the counting rules need. Used from the
/// recorder's single writer thread only (design §2), on one long-lived connection.
///
/// Every write happens inside <see cref="InTransaction"/>, which takes the next number from the change counter
/// and stamps it on whatever it touches — the change feed (§9.4) is "everything stamped after the number you had".
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;
    private long _seq;

    public HistoryStore(HistoryDatabase database)
    {
        _connection = database.Open();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>The change number of the transaction in progress.</summary>
    public long CurrentSeq => _transaction is null
        ? throw new InvalidOperationException("Writes happen inside InTransaction.")
        : _seq;

    /// <param name="commit">False runs the work and rolls it back — an import's preview (§8.1), which does exactly
    /// what its commit would and keeps nothing.</param>
    public void InTransaction(Action work, bool commit = true)
    {
        _transaction = _connection.BeginTransaction();
        try
        {
            _seq = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key = 'change_seq';"), CultureInfo.InvariantCulture) + 1;
            Execute("UPDATE meta SET value = $seq WHERE key = 'change_seq';", ("$seq", _seq.ToString(CultureInfo.InvariantCulture)));
            work();
            if (commit)
            {
                _transaction.Commit();
            }
            else
            {
                _transaction.Rollback();
            }
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    // — People —

    /// <summary>Adds a person the first time they are seen (their recording starts then: the cutover), and keeps their name current.</summary>
    public void UpsertUser(RecordedUser user, DateTimeOffset at) => Execute(
        """
        INSERT INTO users (user_id, name, first_seen_at, recording_since) VALUES ($id, $name, $at, $at)
        ON CONFLICT (user_id) DO UPDATE SET name = excluded.name;
        """,
        ("$id", Id(user.Id)), ("$name", user.Name), ("$at", Ms(at)));

    public void MarkUserDeleted(Guid userId, DateTimeOffset at) => Execute(
        "UPDATE users SET deleted_at = $at WHERE user_id = $id AND deleted_at IS NULL;",
        ("$id", Id(userId)), ("$at", Ms(at)));

    /// <summary>When live recording began for this person: the cutover (§8). Null for someone never seen.</summary>
    public DateTimeOffset? RecordingSince(Guid userId) => Scalar(
        "SELECT recording_since FROM users WHERE user_id = $id;", ("$id", Id(userId))) is long ms ? FromMs(ms) : null;

    // — What items were —

    private const string MediaInsert = """
        INSERT {0} INTO media (item_id, kind, name, original_title, production_year, premiere_date, runtime_ticks,
            series_id, series_name, season_id, season_name, season_number, episode_number, episode_number_end,
            album_id, album_name, artists, album_artists, track_number, disc_number,
            provider_ids, series_provider_ids, library_id, library_name, first_seen_at, updated_at, removed_at, origin)
        VALUES ($item, $kind, $name, $original, $year, $premiere, $runtime,
            $series, $seriesName, $season, $seasonName, $seasonNumber, $episode, $episodeEnd,
            $album, $albumName, $artists, $albumArtists, $track, $disc,
            $providers, $seriesProviders, $library, $libraryName, $at, $at, NULL, $origin)
        """;

    /// <summary>
    /// Something an import names that this library does not have (§8.1) — kept with what the source knew, and never
    /// written over a richer record already there.
    /// </summary>
    public void InsertMediaIfMissing(MediaSnapshot item, DateTimeOffset at, string origin)
    {
        Execute(string.Format(CultureInfo.InvariantCulture, MediaInsert, "OR IGNORE") + ";", MediaParameters(item, at, origin));
        InsertKeys(item);
    }

    public void UpsertMedia(MediaSnapshot item, DateTimeOffset at)
    {
        Execute(
            string.Format(CultureInfo.InvariantCulture, MediaInsert, string.Empty) + """

            ON CONFLICT (item_id) DO UPDATE SET
                kind = excluded.kind, name = excluded.name, original_title = excluded.original_title,
                production_year = excluded.production_year, premiere_date = excluded.premiere_date,
                runtime_ticks = COALESCE(excluded.runtime_ticks, media.runtime_ticks),
                series_id = excluded.series_id, series_name = excluded.series_name, season_id = excluded.season_id,
                season_name = excluded.season_name, season_number = excluded.season_number,
                episode_number = excluded.episode_number, episode_number_end = excluded.episode_number_end,
                album_id = excluded.album_id, album_name = excluded.album_name, artists = excluded.artists,
                album_artists = excluded.album_artists, track_number = excluded.track_number,
                disc_number = excluded.disc_number, provider_ids = excluded.provider_ids,
                series_provider_ids = excluded.series_provider_ids,
                library_id = COALESCE(excluded.library_id, media.library_id),
                library_name = COALESCE(excluded.library_name, media.library_name),
                updated_at = excluded.updated_at, removed_at = NULL, origin = NULL;
            """,
            MediaParameters(item, at, origin: null));
        InsertKeys(item);
    }

    private void InsertKeys(MediaSnapshot item)
    {
        foreach (var key in MediaKeys.For(item))
        {
            Execute("INSERT OR IGNORE INTO media_keys (item_id, key) VALUES ($item, $key);",
                ("$item", Id(item.ItemId)), ("$key", key));
        }
    }

    private static (string, object?)[] MediaParameters(MediaSnapshot item, DateTimeOffset at, string? origin) =>
    [
        ("$item", Id(item.ItemId)), ("$kind", StoredNames.Of(item.Kind)), ("$name", item.Name),
        ("$original", item.OriginalTitle), ("$year", item.ProductionYear),
        ("$premiere", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ("$runtime", item.RuntimeTicks is > 0 ? item.RuntimeTicks : null),
        ("$series", Id(item.SeriesId)), ("$seriesName", item.SeriesName), ("$season", Id(item.SeasonId)),
        ("$seasonName", item.SeasonName), ("$seasonNumber", item.SeasonNumber), ("$episode", item.EpisodeNumber),
        ("$episodeEnd", item.EpisodeNumberEnd), ("$album", Id(item.AlbumId)), ("$albumName", item.AlbumName),
        ("$artists", Json(item.Artists)), ("$albumArtists", Json(item.AlbumArtists)),
        ("$track", item.TrackNumber), ("$disc", item.DiscNumber),
        ("$providers", Json(item.ProviderIds)), ("$seriesProviders", Json(item.SeriesProviderIds)),
        ("$library", Id(item.LibraryId)), ("$libraryName", item.LibraryName), ("$at", Ms(at)), ("$origin", origin),
    ];

    /// <summary>
    /// *Deleted from the library on…* — for the item and, because Jellyfin sends one removal for a whole series,
    /// season or album, for everything inside it. Nothing is deleted.
    /// </summary>
    public int MarkRemoved(Guid itemId, DateTimeOffset at) => Execute(
        """
        UPDATE media SET removed_at = $at
        WHERE removed_at IS NULL AND (item_id = $id OR series_id = $id OR season_id = $id OR album_id = $id);
        """,
        ("$id", Id(itemId)), ("$at", Ms(at)));

    public (MediaKind Kind, long? RuntimeTicks)? MediaFacts(Guid itemId)
    {
        using var command = Command("SELECT kind, runtime_ticks FROM media WHERE item_id = $id;", ("$id", Id(itemId)));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (StoredNames.MediaKindFrom(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetInt64(1))
            : null;
    }

    // — Sessions —

    public void SaveSession(SessionRecord s) => Execute(
        """
        INSERT INTO sessions (session_id, user_id, item_id, viewing_id, source, jf_session_id, play_session_id,
            client, client_version, device_name, device_id, play_method, media_source_id,
            started_at, ended_at, last_seen_at, received_at, start_ticks, end_ticks, last_ticks, furthest_ticks,
            last_paused, runtime_ticks, actual_seconds, wall_seconds, paused_seconds, seek_count, playback_rate,
            end_reason, counted, client_session_id, updated_seq)
        VALUES ($id, $user, $item, $viewing, $source, $jf, $play, $client, $clientVersion, $device, $deviceId,
            $method, $mediaSource, $started, $ended, $lastSeen, $received, $startTicks, $endTicks, $lastTicks,
            $furthest, $paused, $runtime, $actual, $wall, $pausedSeconds, $seeks, $rate, $reason, $counted,
            $clientSession, $seq)
        ON CONFLICT (session_id) DO UPDATE SET
            viewing_id = excluded.viewing_id, play_session_id = excluded.play_session_id,
            play_method = excluded.play_method, media_source_id = excluded.media_source_id,
            started_at = excluded.started_at, ended_at = excluded.ended_at, last_seen_at = excluded.last_seen_at,
            start_ticks = excluded.start_ticks, end_ticks = excluded.end_ticks, last_ticks = excluded.last_ticks,
            furthest_ticks = excluded.furthest_ticks, last_paused = excluded.last_paused,
            actual_seconds = excluded.actual_seconds, wall_seconds = excluded.wall_seconds,
            paused_seconds = excluded.paused_seconds, seek_count = excluded.seek_count,
            end_reason = excluded.end_reason, counted = excluded.counted, updated_seq = excluded.updated_seq;
        """,
        ("$id", Id(s.SessionId)), ("$user", Id(s.UserId)), ("$item", Id(s.ItemId)), ("$viewing", Id(s.ViewingId)),
        ("$source", s.Source), ("$jf", s.JellyfinSessionId), ("$play", s.PlaySessionId), ("$client", s.Client),
        ("$clientVersion", s.ClientVersion), ("$device", s.DeviceName), ("$deviceId", s.DeviceId),
        ("$method", s.PlayMethod), ("$mediaSource", s.MediaSourceId), ("$started", Ms(s.StartedAt)),
        ("$ended", Ms(s.EndedAt)), ("$lastSeen", Ms(s.LastSeenAt)), ("$received", Ms(s.ReceivedAt)),
        ("$startTicks", s.StartTicks), ("$endTicks", s.EndTicks), ("$lastTicks", s.LastTicks),
        ("$furthest", s.FurthestTicks), ("$paused", s.LastPaused ? 1 : 0), ("$runtime", s.RuntimeTicks),
        ("$actual", s.ActualSeconds), ("$wall", s.WallSeconds), ("$pausedSeconds", s.PausedSeconds),
        ("$seeks", s.SeekCount), ("$rate", s.PlaybackRate), ("$reason", s.EndReason), ("$counted", s.Counted ? 1 : 0),
        ("$clientSession", s.ClientSessionId), ("$seq", CurrentSeq));

    /// <summary>Sessions left open — by a server that went down mid-play, say.</summary>
    public IReadOnlyList<SessionRecord> OpenSessions() => ReadSessions(
        SessionSelect + " WHERE s.ended_at IS NULL;");

    /// <summary>
    /// The latest session of this item for this person, judged by when it ended or was received — the one a tick
    /// arriving now might be confirming (§6.4).
    /// </summary>
    public SessionRecord? LatestSessionFor(Guid userId, Guid itemId, IReadOnlyList<string> keys)
    {
        var (keyClause, parameters) = KeyMatch("s.item_id", keys, ("$user", Id(userId)), ("$item", Id(itemId)));
        return ReadSessions(
            SessionSelect + $"""
                 WHERE s.user_id = $user AND (s.item_id = $item{keyClause})
                 ORDER BY MAX(COALESCE(s.ended_at, s.last_seen_at), s.received_at) DESC LIMIT 1;
                """,
            parameters).FirstOrDefault();
    }

    /// <summary>
    /// <c> OR column IN (items sharing one of these keys)</c>, with its parameters — "the same media", for a re-rip
    /// or another version of the same film.
    /// </summary>
    private static (string Clause, (string, object?)[] Parameters) KeyMatch(
        string column, IReadOnlyList<string> keys, params (string, object?)[] parameters)
    {
        var all = parameters.ToList();
        if (keys.Count == 0)
        {
            return (string.Empty, all.ToArray());
        }

        var names = keys.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        all.AddRange(keys.Select((k, i) => (names[i], (object?)k)));
        return ($" OR {column} IN (SELECT item_id FROM media_keys WHERE key IN ({string.Join(", ", names)}))", all.ToArray());
    }

    public IReadOnlyList<SessionRecord> SessionsOfViewing(Guid viewingId) => ReadSessions(
        SessionSelect + " WHERE s.viewing_id = $viewing ORDER BY s.started_at;", ("$viewing", Id(viewingId)));

    /// <summary>Whether a live session of this item for this person overlaps the given time by more than half of it (§6.3).</summary>
    public bool OverlapsLiveSession(Guid userId, Guid itemId, DateTimeOffset start, DateTimeOffset end)
    {
        var length = (end - start).TotalMilliseconds;
        if (length <= 0)
        {
            return false;
        }

        var overlap = Convert.ToDouble(Scalar(
            """
            SELECT COALESCE(MAX(MIN(COALESCE(ended_at, last_seen_at), $end) - MAX(started_at, $start)), 0)
            FROM sessions WHERE user_id = $user AND item_id = $item AND source = 'live'
              AND started_at < $end AND COALESCE(ended_at, last_seen_at) > $start;
            """,
            ("$user", Id(userId)), ("$item", Id(itemId)), ("$start", Ms(start)), ("$end", Ms(end))), CultureInfo.InvariantCulture);
        return overlap > length / 2;
    }

    public bool ClientSessionExists(Guid userId, string clientSessionId) =>
        Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sessions WHERE user_id = $user AND client_session_id = $id;",
            ("$user", Id(userId)), ("$id", clientSessionId)), CultureInfo.InvariantCulture) > 0;

    private const string SessionSelect = """
        SELECT s.session_id, s.user_id, s.item_id, s.viewing_id, s.source, s.jf_session_id, s.play_session_id,
               s.client, s.client_version, s.device_name, s.device_id, s.play_method, s.media_source_id,
               s.started_at, s.ended_at, s.last_seen_at, s.received_at, s.start_ticks, s.end_ticks, s.last_ticks,
               s.furthest_ticks, s.last_paused, s.runtime_ticks, s.actual_seconds, s.wall_seconds, s.paused_seconds,
               s.seek_count, s.playback_rate, s.end_reason, s.counted, s.client_session_id, m.kind
        FROM sessions s JOIN media m ON m.item_id = s.item_id
        """;

    private List<SessionRecord> ReadSessions(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var r = command.ExecuteReader();
        var list = new List<SessionRecord>();
        while (r.Read())
        {
            list.Add(new SessionRecord
            {
                SessionId = Guid.Parse(r.GetString(0)),
                UserId = Guid.Parse(r.GetString(1)),
                ItemId = Guid.Parse(r.GetString(2)),
                ViewingId = r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
                Source = r.GetString(4),
                JellyfinSessionId = NullableString(r, 5),
                PlaySessionId = NullableString(r, 6),
                Client = NullableString(r, 7),
                ClientVersion = NullableString(r, 8),
                DeviceName = NullableString(r, 9),
                DeviceId = NullableString(r, 10),
                PlayMethod = NullableString(r, 11),
                MediaSourceId = NullableString(r, 12),
                StartedAt = FromMs(r.GetInt64(13)),
                EndedAt = r.IsDBNull(14) ? null : FromMs(r.GetInt64(14)),
                LastSeenAt = FromMs(r.GetInt64(15)),
                ReceivedAt = FromMs(r.GetInt64(16)),
                StartTicks = r.GetInt64(17),
                EndTicks = r.IsDBNull(18) ? null : r.GetInt64(18),
                LastTicks = r.GetInt64(19),
                FurthestTicks = r.GetInt64(20),
                LastPaused = r.GetInt64(21) != 0,
                RuntimeTicks = r.IsDBNull(22) ? null : r.GetInt64(22),
                ActualSeconds = r.GetDouble(23),
                WallSeconds = r.GetDouble(24),
                PausedSeconds = r.GetDouble(25),
                SeekCount = r.GetInt32(26),
                PlaybackRate = r.IsDBNull(27) ? null : r.GetDouble(27),
                EndReason = NullableString(r, 28),
                Counted = r.GetInt64(29) != 0,
                ClientSessionId = NullableString(r, 30),
                Kind = StoredNames.MediaKindFrom(r.GetString(31)),
            });
        }

        return list;
    }

    // — Viewings —

    public void SaveViewing(ViewingRecord v) => Execute(
        """
        INSERT INTO viewings (viewing_id, user_id, item_id, kind, source, import_id, source_key, status, started_at,
            last_activity_at, completed_at, completed_by, furthest_ticks, runtime_ticks, actual_seconds, wall_seconds,
            credited_seconds, estimated, play_count_noted, superseded_by, rule, updated_seq)
        VALUES ($id, $user, $item, $kind, $source, $import, $sourceKey, $status, $started, $activity, $completed,
            $by, $furthest, $runtime, $actual, $wall, $credited, $estimated, $playCount, $superseded, $rule, $seq)
        ON CONFLICT (viewing_id) DO UPDATE SET
            item_id = excluded.item_id, status = excluded.status, started_at = excluded.started_at,
            last_activity_at = excluded.last_activity_at, completed_at = excluded.completed_at,
            completed_by = excluded.completed_by, furthest_ticks = excluded.furthest_ticks,
            runtime_ticks = excluded.runtime_ticks, actual_seconds = excluded.actual_seconds,
            wall_seconds = excluded.wall_seconds, credited_seconds = excluded.credited_seconds,
            superseded_by = excluded.superseded_by, rule = excluded.rule, import_id = excluded.import_id,
            updated_seq = excluded.updated_seq;
        """,
        ("$id", Id(v.ViewingId)), ("$user", Id(v.UserId)), ("$item", Id(v.ItemId)), ("$kind", StoredNames.Of(v.Kind)),
        ("$source", v.Source), ("$import", Id(v.ImportId)), ("$sourceKey", v.SourceKey),
        ("$status", StoredNames.Of(v.Status)), ("$started", Ms(v.StartedAt)), ("$activity", Ms(v.LastActivityAt)),
        ("$completed", Ms(v.CompletedAt)), ("$by", v.CompletedBy is { } by ? StoredNames.Of(by) : null),
        ("$furthest", v.FurthestTicks), ("$runtime", v.RuntimeTicks), ("$actual", v.ActualSeconds),
        ("$wall", v.WallSeconds), ("$credited", v.CreditedSeconds), ("$estimated", v.Estimated ? 1 : 0),
        ("$playCount", v.PlayCountNoted), ("$superseded", Id(v.SupersededBy)), ("$rule", v.Rule), ("$seq", CurrentSeq));

    public ViewingRecord? GetViewing(Guid viewingId) => ReadViewings(
        ViewingSelect + " WHERE v.viewing_id = $id;", ("$id", Id(viewingId))).FirstOrDefault();

    /// <summary>
    /// This person's latest live-or-completed viewing of the same media — the same item, or any item sharing a
    /// media key with it (§5.4, §7). Abandoned, retracted, undone and superseded viewings are history, not
    /// something a new play can join.
    /// </summary>
    public ViewingRecord? LatestViewing(Guid userId, Guid itemId, IReadOnlyList<string> keys)
    {
        var (keyClause, parameters) = KeyMatch("v.item_id", keys, ("$user", Id(userId)), ("$item", Id(itemId)));
        return ReadViewings(
            ViewingSelect + $"""
                 WHERE v.user_id = $user AND v.status IN ('in-progress', 'completed') AND v.superseded_by IS NULL
                   AND (v.item_id = $item{keyClause})
                 ORDER BY v.last_activity_at DESC LIMIT 1;
                """,
            parameters).FirstOrDefault();
    }

    /// <summary>A marked viewing of this item made since the given moment — the one an untick might be taking back (§6.4).</summary>
    public ViewingRecord? RecentMarkedViewing(Guid userId, Guid itemId, DateTimeOffset since) => ReadViewings(
        ViewingSelect + """
             WHERE v.user_id = $user AND v.item_id = $item AND v.kind = 'marked' AND v.status = 'completed'
               AND v.started_at >= $since
             ORDER BY v.started_at DESC LIMIT 1;
            """,
        ("$user", Id(userId)), ("$item", Id(itemId)), ("$since", Ms(since))).FirstOrDefault();

    // — Imports (§8) —

    /// <summary>The record an import made for this source key, in any status — undone ones included.</summary>
    public ViewingRecord? ViewingBySourceKey(string source, Guid userId, string sourceKey) => ReadViewings(
        ViewingSelect + " WHERE v.source = $source AND v.user_id = $user AND v.source_key = $key;",
        ("$source", source), ("$user", Id(userId)), ("$key", sourceKey)).FirstOrDefault();

    /// <summary>
    /// The counted viewing of the same media nearest the given moment, within the window — the one an imported record
    /// might be a second account of (§8.1).
    /// </summary>
    public ViewingRecord? NearbyViewing(Guid userId, Guid itemId, IReadOnlyList<string> keys, DateTimeOffset when,
        TimeSpan window, Guid excluding)
    {
        var (keyClause, parameters) = KeyMatch("v.item_id", keys, ("$user", Id(userId)), ("$item", Id(itemId)),
            ("$when", Ms(when)), ("$window", (long)window.TotalMilliseconds), ("$excluding", Id(excluding)));
        return ReadViewings(
            ViewingSelect + $"""
                 WHERE v.user_id = $user AND v.status IN ('in-progress', 'completed', 'abandoned')
                   AND v.superseded_by IS NULL AND v.viewing_id <> $excluding
                   AND (v.item_id = $item{keyClause})
                   AND ABS(COALESCE(v.completed_at, v.last_activity_at) - $when) <= $window
                 ORDER BY ABS(COALESCE(v.completed_at, v.last_activity_at) - $when) LIMIT 1;
                """,
            parameters).FirstOrDefault();
    }

    public IReadOnlyList<ViewingRecord> ViewingsOfImport(Guid importId) => ReadViewings(
        ViewingSelect + " WHERE v.import_id = $import;", ("$import", Id(importId)));

    public IReadOnlyList<ViewingRecord> ViewingsSupersededBy(IReadOnlyList<Guid> viewingIds)
    {
        if (viewingIds.Count == 0)
        {
            return [];
        }

        var names = viewingIds.Select((_, i) => "$v" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        return ReadViewings(
            ViewingSelect + $" WHERE v.superseded_by IN ({string.Join(", ", names)});",
            viewingIds.Select((id, i) => (names[i], (object?)Id(id))).ToArray());
    }

    /// <summary>Whether a viewing's sessions count — they stop when it is superseded or its import undone.</summary>
    public void SetSessionsCounted(Guid viewingId, bool counted) => Execute(
        "UPDATE sessions SET counted = $counted, updated_seq = $seq WHERE viewing_id = $viewing;",
        ("$counted", counted ? 1 : 0), ("$seq", CurrentSeq), ("$viewing", Id(viewingId)));

    public sealed record ImportRow(Guid ImportId, string Source, Guid? UserId, DateTimeOffset CreatedAt,
        DateTimeOffset? CommittedAt, DateTimeOffset? UndoneAt, string? Detail, string? Candidates);

    public void SaveImport(Guid importId, string source, Guid? userId, DateTimeOffset at, string candidates, string detail) => Execute(
        """
        INSERT INTO imports (import_id, source, user_id, created_at, detail, candidates)
        VALUES ($id, $source, $user, $at, $detail, $candidates);
        """,
        ("$id", Id(importId)), ("$source", source), ("$user", Id(userId)), ("$at", Ms(at)), ("$detail", detail),
        ("$candidates", candidates));

    public ImportRow? GetImport(Guid importId)
    {
        using var command = Command(
            "SELECT import_id, source, user_id, created_at, committed_at, undone_at, detail, candidates FROM imports WHERE import_id = $id;",
            ("$id", Id(importId)));
        using var r = command.ExecuteReader();
        return r.Read()
            ? new ImportRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)),
                FromMs(r.GetInt64(3)), r.IsDBNull(4) ? null : FromMs(r.GetInt64(4)), r.IsDBNull(5) ? null : FromMs(r.GetInt64(5)),
                NullableString(r, 6), NullableString(r, 7))
            : null;
    }

    /// <summary>Whether this person's install-time baseline from Jellyfin's own watch state has been taken (§8.2).</summary>
    public bool HasCommittedImport(string source, Guid userId) => Convert.ToInt64(Scalar(
        "SELECT COUNT(*) FROM imports WHERE source = $source AND user_id = $user AND committed_at IS NOT NULL;",
        ("$source", source), ("$user", Id(userId))), CultureInfo.InvariantCulture) > 0;

    public void SetImportCommitted(Guid importId, DateTimeOffset at, string detail) => Execute(
        "UPDATE imports SET committed_at = $at, detail = $detail WHERE import_id = $id;",
        ("$id", Id(importId)), ("$at", Ms(at)), ("$detail", detail));

    public void SetImportUndone(Guid importId, DateTimeOffset at) => Execute(
        "UPDATE imports SET undone_at = $at WHERE import_id = $id;", ("$id", Id(importId)), ("$at", Ms(at)));

    private const string ViewingSelect = """
        SELECT v.viewing_id, v.user_id, v.item_id, v.kind, v.source, v.import_id, v.source_key, v.status,
               v.started_at, v.last_activity_at, v.completed_at, v.completed_by, v.furthest_ticks, v.runtime_ticks,
               v.actual_seconds, v.wall_seconds, v.credited_seconds, v.estimated, v.play_count_noted,
               v.superseded_by, v.rule, m.kind
        FROM viewings v JOIN media m ON m.item_id = v.item_id
        """;

    private List<ViewingRecord> ReadViewings(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var r = command.ExecuteReader();
        var list = new List<ViewingRecord>();
        while (r.Read())
        {
            list.Add(new ViewingRecord
            {
                ViewingId = Guid.Parse(r.GetString(0)),
                UserId = Guid.Parse(r.GetString(1)),
                ItemId = Guid.Parse(r.GetString(2)),
                Kind = StoredNames.ViewingKindFrom(r.GetString(3)),
                Source = r.GetString(4),
                ImportId = r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
                SourceKey = NullableString(r, 6),
                Status = StoredNames.StatusFrom(r.GetString(7)),
                StartedAt = FromMs(r.GetInt64(8)),
                LastActivityAt = FromMs(r.GetInt64(9)),
                CompletedAt = r.IsDBNull(10) ? null : FromMs(r.GetInt64(10)),
                CompletedBy = StoredNames.CompletedByFrom(NullableString(r, 11)),
                FurthestTicks = r.GetInt64(12),
                RuntimeTicks = r.IsDBNull(13) ? null : r.GetInt64(13),
                ActualSeconds = r.GetDouble(14),
                WallSeconds = r.GetDouble(15),
                CreditedSeconds = r.GetDouble(16),
                Estimated = r.GetInt64(17) != 0,
                PlayCountNoted = r.IsDBNull(18) ? null : r.GetInt32(18),
                SupersededBy = r.IsDBNull(19) ? null : Guid.Parse(r.GetString(19)),
                Rule = NullableString(r, 20),
                MediaKind = StoredNames.MediaKindFrom(r.GetString(21)),
            });
        }

        return list;
    }

    // — Jellyfin's own flags, as last seen (§6.1) —

    public (bool Played, long PositionTicks)? GetUserItemState(Guid userId, Guid itemId)
    {
        using var command = Command(
            "SELECT played, position_ticks FROM user_item_state WHERE user_id = $user AND item_id = $item;",
            ("$user", Id(userId)), ("$item", Id(itemId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0) != 0, reader.GetInt64(1)) : null;
    }

    public void SetUserItemState(Guid userId, Guid itemId, bool played, long positionTicks, DateTimeOffset at) => Execute(
        """
        INSERT INTO user_item_state (user_id, item_id, played, position_ticks, updated_at)
        VALUES ($user, $item, $played, $position, $at)
        ON CONFLICT (user_id, item_id) DO UPDATE SET
            played = excluded.played, position_ticks = excluded.position_ticks, updated_at = excluded.updated_at;
        """,
        ("$user", Id(userId)), ("$item", Id(itemId)), ("$played", played ? 1 : 0), ("$position", positionTicks),
        ("$at", Ms(at)));

    // — The raw log —

    public void InsertEvent(
        DateTimeOffset at, string kind, Guid? userId = null, Guid? itemId = null, Guid? sessionId = null,
        long? positionTicks = null, object? detail = null) => Execute(
        """
        INSERT INTO events (at, user_id, item_id, session_id, kind, position_ticks, detail)
        VALUES ($at, $user, $item, $session, $kind, $position, $detail);
        """,
        ("$at", Ms(at)), ("$user", Id(userId)), ("$item", Id(itemId)), ("$session", Id(sessionId)), ("$kind", kind),
        ("$position", positionTicks), ("$detail", detail is null ? null : JsonSerializer.Serialize(detail)));

    public void InsertSample(Guid sessionId, DateTimeOffset at, long positionTicks, bool paused) => Execute(
        "INSERT INTO progress_samples (session_id, at, position_ticks, paused) VALUES ($s, $at, $p, $paused);",
        ("$s", Id(sessionId)), ("$at", Ms(at)), ("$p", positionTicks), ("$paused", paused ? 1 : 0));

    /// <summary>Progress samples are diagnostics, kept 30 days by default (§3) — the one table the plugin trims.</summary>
    public int DeleteSamplesBefore(DateTimeOffset cutoff) => Execute(
        "DELETE FROM progress_samples WHERE at < $cutoff;", ("$cutoff", Ms(cutoff)));

    // — Plumbing —

    internal static string? Id(Guid? id) => id?.ToString("N");

    internal static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    internal static long? Ms(DateTimeOffset? at) => at?.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    internal int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    internal object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }
}
