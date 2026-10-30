using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.AppData;

/// <summary>Something an app asked for that cannot be done, with a reason a person can read.</summary>
public sealed class AppDataException(string message) : Exception(message);

// — Documents (design §16 idea 1) —

public sealed record Document(string Key, long Version, JsonElement? Value, bool Deleted, DateTime UpdatedAt, Guid? UpdatedBy);

public sealed record DocumentInfo(string Key, long Version, int Bytes, DateTime UpdatedAt);

/// <summary>What a write did: saved (the new version), or refused because the document had moved on (the current one).</summary>
public sealed record PutOutcome(bool Saved, Document? Document);

/// <summary>
/// Documents an app keeps on the server: one person's (settings, quiz scores, learned intros), or the household's
/// (quiz rounds built on one device for any device to play, chapter names made once). A write names the version it
/// read and is refused if the document has moved on since — the refusal carries the current document, the app merges
/// with its own rules and tries again. The server stores and arbitrates; it never merges.
/// </summary>
public static partial class Documents
{
    /// <summary>The owner of household documents.</summary>
    public const string Household = "*";

    public const int MaxValueBytes = 1_000_000;
    public const int MaxOwnerBytes = 20_000_000;
    public const int MaxKeys = 5_000;

    public static string Owner(Guid userId) => userId.ToString("N");

    public static void ValidateKey(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new AppDataException("A key is 1 to 128 letters, digits, '.', '_', ':' or '-', starting with a letter or digit.");
        }
    }

    /// <param name="ifVersion">The version the writer read: 0 when it believes there is no document yet.</param>
    public static PutOutcome Put(HistoryStore s, string owner, string key, string json, long ifVersion, Guid by, DateTimeOffset now)
    {
        ValidateKey(key);
        if (json.Length > MaxValueBytes)
        {
            throw new AppDataException($"A document may be at most {MaxValueBytes / 1000} KB.");
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new AppDataException("A document's value must be JSON.");
        }

        var current = Read(s, owner, key);
        var currentVersion = current?.Version ?? 0;
        if (currentVersion != ifVersion)
        {
            return new PutOutcome(false, current);
        }

        if (current is null)
        {
            var (count, bytes) = Usage(s, owner);
            if (count >= MaxKeys || bytes + json.Length > MaxOwnerBytes)
            {
                throw new AppDataException("There is no room for another document here.");
            }
        }

        var version = currentVersion + 1;
        s.Execute(
            """
            INSERT INTO documents (owner, key, version, value, deleted, updated_by, updated_at, updated_seq)
            VALUES ($owner, $key, $version, $value, 0, $by, $at, $seq)
            ON CONFLICT (owner, key) DO UPDATE SET version = excluded.version, value = excluded.value, deleted = 0,
                updated_by = excluded.updated_by, updated_at = excluded.updated_at, updated_seq = excluded.updated_seq;
            """,
            ("$owner", owner), ("$key", key), ("$version", version), ("$value", json), ("$by", by.ToString("N")),
            ("$at", now.ToUnixTimeMilliseconds()), ("$seq", s.CurrentSeq));
        return new PutOutcome(true, Read(s, owner, key));
    }

    /// <summary>Deletes a document, if still the version read. A marker stays, so other devices hear of it.</summary>
    public static PutOutcome Delete(HistoryStore s, string owner, string key, long ifVersion, Guid by, DateTimeOffset now)
    {
        var current = Read(s, owner, key);
        if (current is null || current.Deleted || current.Version != ifVersion)
        {
            return new PutOutcome(false, current);
        }

        s.Execute(
            """
            UPDATE documents SET version = version + 1, value = 'null', deleted = 1, updated_by = $by, updated_at = $at,
                updated_seq = $seq WHERE owner = $owner AND key = $key;
            """,
            ("$owner", owner), ("$key", key), ("$by", by.ToString("N")), ("$at", now.ToUnixTimeMilliseconds()), ("$seq", s.CurrentSeq));
        return new PutOutcome(true, Read(s, owner, key));
    }

    private static Document? Read(HistoryStore s, string owner, string key)
    {
        using var command = s.Command(Select + " WHERE owner = $owner AND key = $key;", ("$owner", owner), ("$key", key));
        using var r = command.ExecuteReader();
        return r.Read() ? FromRow(r) : null;
    }

    private static (long Count, long Bytes) Usage(HistoryStore s, string owner)
    {
        using var command = s.Command(
            "SELECT COUNT(*), COALESCE(SUM(LENGTH(value)), 0) FROM documents WHERE owner = $owner AND deleted = 0;", ("$owner", owner));
        using var r = command.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    internal const string Select = "SELECT key, version, value, deleted, updated_at, updated_by, updated_seq FROM documents";

    internal static Document FromRow(SqliteDataReader r)
    {
        var deleted = r.GetInt64(3) != 0;
        return new Document(
            r.GetString(0),
            r.GetInt64(1),
            deleted ? null : JsonDocument.Parse(r.GetString(2)).RootElement.Clone(),
            deleted,
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime,
            r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex KeyPattern();
}

// — Quiz scores and the household leaderboard (§16 idea 2) —

public sealed record QuizScoreInput
{
    /// <summary>The app's own id for the finished quiz: posting it twice is harmless.</summary>
    public required string ScoreId { get; init; }
    public Guid? ItemId { get; init; }
    public string? ItemName { get; init; }
    public Guid? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int Score { get; init; }
    public int OutOf { get; init; }
    public required DateTime CompletedAt { get; init; }
}

public sealed record LeaderboardRow(Guid UserId, string Name, bool IsYou, int Quizzes, int RightAnswers, int Questions, int Perfect,
    DateTime? LastPlayed);

public static class Quiz
{
    /// <summary>Records finished quizzes. Returns how many were new.</summary>
    public static int Record(HistoryStore s, Guid userId, IReadOnlyList<QuizScoreInput> scores)
    {
        var added = 0;
        foreach (var q in scores)
        {
            if (string.IsNullOrWhiteSpace(q.ScoreId) || q.ScoreId.Length > 100 || q.OutOf is < 1 or > 100 || q.Score < 0 || q.Score > q.OutOf)
            {
                throw new AppDataException($"Quiz '{q.ScoreId}' is not a plausible result.");
            }

            added += s.Execute(
                """
                INSERT OR IGNORE INTO quiz_scores (score_id, user_id, item_id, item_name, series_id, series_name, season, episode,
                    score, out_of, completed_at)
                VALUES ($id, $user, $item, $itemName, $series, $seriesName, $season, $episode, $score, $outOf, $at);
                """,
                ("$id", q.ScoreId), ("$user", userId.ToString("N")), ("$item", q.ItemId?.ToString("N")), ("$itemName", q.ItemName),
                ("$series", q.SeriesId?.ToString("N")), ("$seriesName", q.SeriesName), ("$season", q.Season), ("$episode", q.Episode),
                ("$score", q.Score), ("$outOf", q.OutOf),
                ("$at", new DateTimeOffset(DateTime.SpecifyKind(q.CompletedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds()));
        }

        return added;
    }

    public static void SetSharing(HistoryStore s, Guid userId, bool shares, DateTimeOffset now) => s.Execute(
        """
        INSERT INTO quiz_sharing (user_id, shares, updated_at) VALUES ($user, $shares, $at)
        ON CONFLICT (user_id) DO UPDATE SET shares = excluded.shares, updated_at = excluded.updated_at;
        """,
        ("$user", userId.ToString("N")), ("$shares", shares ? 1 : 0), ("$at", now.ToUnixTimeMilliseconds()));
}

// — Skip segments learned by an app (§16 idea 4) —

public sealed record SegmentInput
{
    public required Guid ItemId { get; init; }
    /// <summary><c>Intro</c>, <c>Outro</c>, <c>Recap</c>, <c>Preview</c> or <c>Commercial</c>.</summary>
    public required string Kind { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public string? Source { get; init; }
}

public sealed record Segment(Guid ItemId, string Kind, double StartSeconds, double EndSeconds, string? Source, DateTime UpdatedAt);

public static class Segments
{
    public static readonly IReadOnlyList<string> Kinds = ["Intro", "Outro", "Recap", "Preview", "Commercial"];

    public static int Put(HistoryStore s, IReadOnlyList<SegmentInput> segments, Func<Guid, long?> runtime, Guid by, DateTimeOffset now)
    {
        foreach (var seg in segments)
        {
            var kind = Kinds.FirstOrDefault(k => string.Equals(k, seg.Kind, StringComparison.OrdinalIgnoreCase))
                       ?? throw new AppDataException($"A segment's kind is one of {string.Join(", ", Kinds)}.");
            if (runtime(seg.ItemId) is not { } length)
            {
                throw new AppDataException($"Item {seg.ItemId:N} is not in this library.");
            }

            var lengthSeconds = TimeSpan.FromTicks(length).TotalSeconds;
            if (seg.StartSeconds < 0 || seg.EndSeconds <= seg.StartSeconds || seg.EndSeconds > lengthSeconds + 1)
            {
                throw new AppDataException($"A segment runs forwards, within the item's {lengthSeconds:0} seconds.");
            }

            s.Execute(
                """
                INSERT INTO segments (item_id, kind, start_ticks, end_ticks, source, updated_by, updated_at)
                VALUES ($item, $kind, $start, $end, $source, $by, $at)
                ON CONFLICT (item_id, kind) DO UPDATE SET start_ticks = excluded.start_ticks, end_ticks = excluded.end_ticks,
                    source = excluded.source, updated_by = excluded.updated_by, updated_at = excluded.updated_at;
                """,
                ("$item", seg.ItemId.ToString("N")), ("$kind", kind), ("$start", TimeSpan.FromSeconds(seg.StartSeconds).Ticks),
                ("$end", TimeSpan.FromSeconds(seg.EndSeconds).Ticks), ("$source", seg.Source), ("$by", by.ToString("N")),
                ("$at", now.ToUnixTimeMilliseconds()));
        }

        return segments.Count;
    }

    public static int Delete(HistoryStore s, Guid itemId, string? kind) => s.Execute(
        "DELETE FROM segments WHERE item_id = $item AND ($kind IS NULL OR kind = $kind);",
        ("$item", itemId.ToString("N")), ("$kind", kind));
}

/// <summary>The reads behind the app-data endpoints, each on its own read-only connection.</summary>
public sealed class AppDataQueries(HistoryDatabase database)
{
    public Document? Get(string owner, string key)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = Command(c, Documents.Select + " WHERE owner = $owner AND key = $key AND deleted = 0;", ("$owner", owner), ("$key", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Documents.FromRow(r) : null;
    }

    public IReadOnlyList<DocumentInfo> List(string owner, string? prefix)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = Command(c,
            "SELECT key, version, LENGTH(value), updated_at FROM documents WHERE owner = $owner AND deleted = 0 AND ($prefix IS NULL OR key LIKE $like ESCAPE '\\') ORDER BY key;",
            ("$owner", owner), ("$prefix", prefix),
            ("$like", prefix is null ? null : prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        using var r = cmd.ExecuteReader();
        var list = new List<DocumentInfo>();
        while (r.Read())
        {
            list.Add(new DocumentInfo(r.GetString(0), r.GetInt64(1), r.GetInt32(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).UtcDateTime));
        }

        return list;
    }

    /// <summary>Documents changed since a change number (deletions included, as markers), and the number for next time.</summary>
    public (IReadOnlyList<Document> Changed, long Next) Changes(string owner, long since, int limit)
    {
        using var c = database.Open(readOnly: true);
        var head = long.Parse((string)Scalar(c, "SELECT value FROM meta WHERE key = 'change_seq';")!, CultureInfo.InvariantCulture);
        using var cmd = Command(c, Documents.Select + " WHERE owner = $owner AND updated_seq > $since AND updated_seq <= $head ORDER BY updated_seq LIMIT $limit;",
            ("$owner", owner), ("$since", since), ("$head", head), ("$limit", limit + 1));
        using var r = cmd.ExecuteReader();
        var rows = new List<(Document Doc, long Seq)>();
        while (r.Read())
        {
            rows.Add((Documents.FromRow(r), r.GetInt64(6)));
        }

        var more = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        return (page.Select(x => x.Doc).ToList(), more ? page[^1].Seq : head);
    }

    public bool Shares(Guid userId)
    {
        using var c = database.Open(readOnly: true);
        return Scalar(c, "SELECT shares FROM quiz_sharing WHERE user_id = $u;", ("$u", userId.ToString("N"))) is long shares && shares != 0;
    }

    /// <summary>
    /// Quiz results, one row a person: the caller always, and everyone else who has chosen to share theirs. For one
    /// programme (a series or a film), or everything.
    /// </summary>
    public IReadOnlyList<LeaderboardRow> Leaderboard(Guid caller, Guid? seriesId, Guid? itemId)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = Command(c,
            """
            SELECT q.user_id, COALESCE(u.name, 'Someone'), COUNT(*), SUM(q.score), SUM(q.out_of),
                   SUM(CASE WHEN q.score = q.out_of THEN 1 ELSE 0 END), MAX(q.completed_at)
            FROM quiz_scores q
            LEFT JOIN users u ON u.user_id = q.user_id
            LEFT JOIN quiz_sharing s ON s.user_id = q.user_id
            WHERE (q.user_id = $caller OR s.shares = 1)
              AND ($series IS NULL OR q.series_id = $series)
              AND ($item IS NULL OR q.item_id = $item)
            GROUP BY q.user_id
            ORDER BY SUM(q.score) * 1.0 / SUM(q.out_of) DESC, COUNT(*) DESC;
            """,
            ("$caller", caller.ToString("N")), ("$series", seriesId?.ToString("N")), ("$item", itemId?.ToString("N")));
        using var r = cmd.ExecuteReader();
        var rows = new List<LeaderboardRow>();
        while (r.Read())
        {
            var user = Guid.Parse(r.GetString(0));
            rows.Add(new LeaderboardRow(user, r.GetString(1), user == caller, r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)).UtcDateTime));
        }

        return rows;
    }

    public IReadOnlyList<Segment> SegmentsFor(Guid itemId)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = Command(c, "SELECT item_id, kind, start_ticks, end_ticks, source, updated_at FROM segments WHERE item_id = $item ORDER BY start_ticks;",
            ("$item", itemId.ToString("N")));
        using var r = cmd.ExecuteReader();
        var list = new List<Segment>();
        while (r.Read())
        {
            list.Add(new Segment(Guid.Parse(r.GetString(0)), r.GetString(1), TimeSpan.FromTicks(r.GetInt64(2)).TotalSeconds,
                TimeSpan.FromTicks(r.GetInt64(3)).TotalSeconds, r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).UtcDateTime));
        }

        return list;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(c, sql, parameters);
        return cmd.ExecuteScalar();
    }
}
