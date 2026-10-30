using System.Globalization;
using System.Text;
using System.Text.Json;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.Api;

/// <summary>Facts only Jellyfin's library can give, asked of the adapter (§2).</summary>
public interface ILibraryFacts
{
    /// <summary>Episodes of the series in the library now. Null when the series has gone.</summary>
    int? EpisodesInSeries(Guid seriesId);

    /// <summary>The last file of an audiobook split into several, whose completion finishes the book. Null when unknown.</summary>
    Guid? LastFileOfBook(Guid albumId);
}

/// <summary>
/// Everything the API reads (design §9). Each call opens its own read-only connection, so reads never wait on the
/// recorder (WAL) and never write.
/// </summary>
public sealed class HistoryQueries
{
    /// <summary>Statuses that count: watched, under way, or given up on (whose time still counts).</summary>
    private const string Counted = "('in-progress', 'completed', 'abandoned')";

    private readonly HistoryDatabase _database;

    public HistoryQueries(HistoryDatabase database) => _database = database;

    // — People —

    public sealed record UserInfo(Guid UserId, string Name, DateTimeOffset? RecordingSince, DateTimeOffset? DeletedAt);

    public UserInfo? User(Guid userId)
    {
        using var c = _database.Open(readOnly: true);
        using var cmd = Command(c, "SELECT user_id, name, recording_since, deleted_at FROM users WHERE user_id = $u;", ("$u", Id(userId)));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new UserInfo(Guid.Parse(r.GetString(0)), r.GetString(1), NullableTime(r, 2), NullableTime(r, 3))
            : null;
    }

    public IReadOnlyList<UserInfo> Users()
    {
        using var c = _database.Open(readOnly: true);
        using var cmd = Command(c, "SELECT user_id, name, recording_since, deleted_at FROM users ORDER BY name;");
        using var r = cmd.ExecuteReader();
        var list = new List<UserInfo>();
        while (r.Read())
        {
            list.Add(new UserInfo(Guid.Parse(r.GetString(0)), r.GetString(1), NullableTime(r, 2), NullableTime(r, 3)));
        }

        return list;
    }

    // — History —

    public Page<HistoryEntry> History(Guid userId, DateTimeOffset? from, DateTimeOffset? to, IReadOnlyCollection<MediaKind>? kinds,
        int startIndex, int limit)
    {
        using var c = _database.Open(readOnly: true);
        var where = new StringBuilder($"v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL");
        var parameters = new List<(string, object?)> { ("$u", Id(userId)) };
        if (from is { } f)
        {
            where.Append(" AND v.last_activity_at >= $from");
            parameters.Add(("$from", f.ToUnixTimeMilliseconds()));
        }

        if (to is { } t)
        {
            where.Append(" AND v.started_at < $to");
            parameters.Add(("$to", t.ToUnixTimeMilliseconds()));
        }

        if (kinds is { Count: > 0 })
        {
            where.Append(" AND m.kind IN (" + string.Join(", ", kinds.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture))) + ")");
            parameters.AddRange(kinds.Select((k, i) => ("$k" + i.ToString(CultureInfo.InvariantCulture), (object?)StoredNames.Of(k))));
        }

        var total = Convert.ToInt32(ScalarOf(c, $"SELECT COUNT(*) FROM viewings v JOIN media m ON m.item_id = v.item_id WHERE {where};",
            parameters.ToArray()), CultureInfo.InvariantCulture);
        parameters.Add(("$limit", limit));
        parameters.Add(("$start", startIndex));
        var items = ReadEntries(c, $"{EntrySelect} WHERE {where} ORDER BY v.last_activity_at DESC LIMIT $limit OFFSET $start;",
            parameters.ToArray());
        return new Page<HistoryEntry>(items, total, startIndex);
    }

    /// <summary>One item's viewings, with their sessions. Works for items gone from the library (§7).</summary>
    public (IReadOnlyList<HistoryEntry> Entries, IReadOnlyList<SessionEntry> Sessions) ItemHistory(Guid userId, Guid itemId)
    {
        using var c = _database.Open(readOnly: true);
        var entries = ReadEntries(c, $"""
            {EntrySelect}
            WHERE v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL
              AND (v.item_id = $i OR v.item_id IN (SELECT k2.item_id FROM media_keys k1 JOIN media_keys k2 ON k2.key = k1.key WHERE k1.item_id = $i))
            ORDER BY v.started_at;
            """, ("$u", Id(userId)), ("$i", Id(itemId)));
        var sessions = ReadSessions(c, $"{SessionSelect} WHERE s.user_id = $u AND s.item_id = $i ORDER BY s.started_at;",
            ("$u", Id(userId)), ("$i", Id(itemId)));
        return (entries, sessions);
    }

    public Page<SessionEntry> Sessions(Guid userId, Guid? itemId, DateTimeOffset? from, DateTimeOffset? to, int startIndex, int limit)
    {
        using var c = _database.Open(readOnly: true);
        var where = new StringBuilder("s.user_id = $u");
        var parameters = new List<(string, object?)> { ("$u", Id(userId)) };
        if (itemId is { } item)
        {
            where.Append(" AND s.item_id = $i");
            parameters.Add(("$i", Id(item)));
        }

        if (from is { } f)
        {
            where.Append(" AND s.started_at >= $from");
            parameters.Add(("$from", f.ToUnixTimeMilliseconds()));
        }

        if (to is { } t)
        {
            where.Append(" AND s.started_at < $to");
            parameters.Add(("$to", t.ToUnixTimeMilliseconds()));
        }

        var total = Convert.ToInt32(ScalarOf(c, $"SELECT COUNT(*) FROM sessions s WHERE {where};", parameters.ToArray()), CultureInfo.InvariantCulture);
        parameters.Add(("$limit", limit));
        parameters.Add(("$start", startIndex));
        var items = ReadSessions(c, $"{SessionSelect} WHERE {where} ORDER BY s.started_at DESC LIMIT $limit OFFSET $start;", parameters.ToArray());
        return new Page<SessionEntry>(items, total, startIndex);
    }

    /// <summary>A session and its raw events: the diagnostics (§6.1, §16 idea 7). Null when it is not this person's.</summary>
    public (SessionEntry Session, IReadOnlyList<EventEntry> Events)? Session(Guid userId, Guid sessionId)
    {
        using var c = _database.Open(readOnly: true);
        var session = ReadSessions(c, $"{SessionSelect} WHERE s.user_id = $u AND s.session_id = $s;", ("$u", Id(userId)), ("$s", Id(sessionId)))
            .FirstOrDefault();
        if (session is null)
        {
            return null;
        }

        var events = new List<EventEntry>();
        using var cmd = Command(c,
            """
            SELECT at, kind, item_id, session_id, position_ticks, detail FROM events
            WHERE user_id = $u AND (session_id = $s OR (item_id = $i AND at BETWEEN $from AND $to))
            ORDER BY at, event_id;
            """,
            ("$u", Id(userId)), ("$s", Id(sessionId)), ("$i", Id(session.ItemId)),
            ("$from", new DateTimeOffset(session.StartedAt).ToUnixTimeMilliseconds() - 60_000),
            ("$to", new DateTimeOffset(session.EndedAt ?? session.StartedAt).ToUnixTimeMilliseconds() + 900_000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            events.Add(new EventEntry(Time(r.GetInt64(0)), r.GetString(1), NullableGuid(r, 2), NullableGuid(r, 3),
                r.IsDBNull(4) ? null : Seconds(r.GetInt64(4)), r.IsDBNull(5) ? null : r.GetString(5)));
        }

        return (session, events);
    }

    // — The change feed (§9.4) —

    public ChangeFeed Changes(Guid userId, string? since, int limit)
    {
        using var c = _database.Open(readOnly: true);
        var databaseId = (string)ScalarOf(c, "SELECT value FROM meta WHERE key = 'db_id';")!;
        var head = long.Parse((string)ScalarOf(c, "SELECT value FROM meta WHERE key = 'change_seq';")!, CultureInfo.InvariantCulture);

        var resync = false;
        long from = 0;
        if (!string.IsNullOrEmpty(since))
        {
            if (TryParseToken(since, out var tokenDb, out var tokenSeq) && tokenDb == databaseId && tokenSeq <= head)
            {
                from = tokenSeq;
            }
            else
            {
                resync = true;
            }
        }

        var rows = ReadEntries(c, $"""
            {EntrySelectWithSeq}
            WHERE v.user_id = $u AND v.updated_seq > $from AND v.updated_seq <= $head
            ORDER BY v.updated_seq LIMIT $limit;
            """, [("$u", Id(userId)), ("$from", from), ("$head", head), ("$limit", limit + 1)], out var seqs);

        var more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
            seqs.RemoveAt(seqs.Count - 1);
        }

        var next = more ? seqs[^1] : head;
        var counted = new HashSet<string> { "InProgress", "Completed", "Abandoned" };
        return new ChangeFeed
        {
            Changed = rows.Where(e => counted.Contains(e.Status)).ToList(),
            Removed = rows.Where(e => !counted.Contains(e.Status)).Select(e => e.Id).ToList(),
            Next = Token(databaseId, next),
            More = more,
            Resync = resync,
        };
    }

    public string CurrentToken()
    {
        using var c = _database.Open(readOnly: true);
        var databaseId = (string)ScalarOf(c, "SELECT value FROM meta WHERE key = 'db_id';")!;
        var head = long.Parse((string)ScalarOf(c, "SELECT value FROM meta WHERE key = 'change_seq';")!, CultureInfo.InvariantCulture);
        return Token(databaseId, head);
    }

    private static string Token(string databaseId, long seq) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(databaseId + ":" + seq.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryParseToken(string token, out string databaseId, out long seq)
    {
        databaseId = string.Empty;
        seq = 0;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out seq))
            {
                return false;
            }

            databaseId = parts[0];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // — The stats page in one request (§9.2) —

    public Summary Summary(Guid userId, TimeZoneInfo zone, int recent, ILibraryFacts facts, DateTimeOffset now)
    {
        using var c = _database.Open(readOnly: true);
        var all = LoadForSummary(c, userId);
        var video = all.Where(v => v.Kind is MediaKind.Movie or MediaKind.Episode or MediaKind.Video).ToList();

        var films = FilmRows(all.Where(v => v.Kind == MediaKind.Movie)).ToList();
        var series = SeriesRows(all.Where(v => v.Kind == MediaKind.Episode), facts).ToList();
        var episodeGroups = all.Where(v => v.Kind == MediaKind.Episode).GroupBy(v => v.GroupKey).ToList();
        var finishedEpisodes = episodeGroups.Where(g => g.Any(v => v.Completed)).ToList();
        var finishedFilms = films.Where(f => f.Finished).ToList();

        var sessionDays = SessionDays(c, userId, zone);
        var entries = ReadEntries(c, $"""
            {EntrySelect}
            WHERE v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL AND m.kind IN ('Movie', 'Episode', 'Video')
            ORDER BY v.last_activity_at DESC LIMIT 10;
            """, ("$u", Id(userId)));

        var music = all.Where(v => v.Kind == MediaKind.Audio).ToList();
        var books = all.Where(v => v.Kind == MediaKind.AudioBook).ToList();

        return new Summary
        {
            GeneratedAt = now.UtcDateTime,
            AsOf = CurrentToken(),
            Films = new FilmsSummary
            {
                Watched = finishedFilms.Count,
                Viewings = films.Sum(f => f.Viewings),
                UnderWay = films.Count(f => !f.Finished),
                ActualSeconds = films.Sum(f => f.ActualSeconds),
                CreditedSeconds = films.Sum(f => f.CreditedSeconds),
                AverageRuntimeSeconds = Average(finishedFilms.Select(f => f.RuntimeSeconds)),
                Longest = finishedFilms.Where(f => f.RuntimeSeconds is not null).MaxBy(f => f.RuntimeSeconds),
                Recent = films.OrderByDescending(f => f.LastWatched).Take(recent).ToList(),
            },
            Episodes = new EpisodesSummary
            {
                Watched = finishedEpisodes.Count,
                Viewings = episodeGroups.Sum(g => g.Count(v => v.Completed)),
                ActualSeconds = episodeGroups.Sum(g => g.Sum(v => v.Actual)),
                CreditedSeconds = episodeGroups.Sum(g => g.Sum(v => v.Credited)),
                AverageRuntimeSeconds = Average(finishedEpisodes.Select(g => g.First().Item.RuntimeSeconds)),
            },
            Series = new SeriesSummary
            {
                Started = series.Count,
                Finished = series.Count(s => s.Finished),
                MostWatched = series.Where(s => s.CreditedSeconds > 0).MaxBy(s => s.CreditedSeconds),
                Recent = series.OrderByDescending(s => s.LastWatched).Take(recent).ToList(),
            },
            Total = new TimeTotals(video.Sum(v => v.Actual), video.Sum(v => v.Credited), video.Sum(v => v.Wall)),
            Streaks = ComputeStreaks(sessionDays, ToLocalDay(now, zone)),
            TopFilms = films.Where(f => f.Viewings > 0).OrderByDescending(f => f.Viewings).ThenByDescending(f => f.LastWatched).Take(10).ToList(),
            TopSeries = series.OrderByDescending(s => s.CreditedSeconds).Take(10).ToList(),
            Recent = entries,
            ByYear = Years(c, userId, zone, video),
            Music = new MusicSummary
            {
                Listens = music.Count(v => v.Completed),
                ActualSeconds = music.Sum(v => v.Actual),
                CreditedSeconds = music.Sum(v => v.Credited),
                TopArtists = music.Where(v => v.Completed)
                    .GroupBy(v => v.Item.Artists.FirstOrDefault() ?? v.Item.AlbumArtists.FirstOrDefault() ?? "Unknown artist")
                    .Select(g => new NamedCount(g.Key, g.Count())).OrderByDescending(x => x.Listens).Take(10).ToList(),
                TopAlbums = music.Where(v => v.Completed && v.Item.AlbumName is not null)
                    .GroupBy(v => v.Item.AlbumName!)
                    .Select(g => new NamedCount(g.Key, g.Count())).OrderByDescending(x => x.Listens).Take(10).ToList(),
                TopTracks = music.Where(v => v.Completed).GroupBy(v => v.GroupKey)
                    .Select(g => new TrackCount(g.OrderByDescending(v => v.LastActivity).First().Item, g.Count()))
                    .OrderByDescending(x => x.Listens).Take(10).ToList(),
            },
            AudioBooks = BookSummary(books, facts),
        };
    }

    public Page<FilmRow> Films(Guid userId, string order, int startIndex, int limit)
    {
        using var c = _database.Open(readOnly: true);
        var films = FilmRows(LoadForSummary(c, userId).Where(v => v.Kind == MediaKind.Movie));
        var ordered = order == "title"
            ? films.OrderBy(f => f.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            : films.OrderByDescending(f => f.LastWatched);
        var list = ordered.ToList();
        return new Page<FilmRow>(list.Skip(startIndex).Take(limit).ToList(), list.Count, startIndex);
    }

    public Page<SeriesRow> Series(Guid userId, string order, int startIndex, int limit, ILibraryFacts facts)
    {
        using var c = _database.Open(readOnly: true);
        var series = SeriesRows(LoadForSummary(c, userId).Where(v => v.Kind == MediaKind.Episode), facts).ToList();
        IEnumerable<SeriesRow> ordered = order switch
        {
            "title" => series.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
            "mostWatched" => series.OrderByDescending(s => s.EpisodesWatched).ThenByDescending(s => s.LastWatched),
            "mostComplete" => series.OrderByDescending(s => s.Finished)
                .ThenByDescending(s => s.Finished ? s.EpisodesWatched : 0)
                .ThenByDescending(s => s.EpisodesInLibrary is > 0 ? (double)s.EpisodesWatched / s.EpisodesInLibrary.Value : -1)
                .ThenByDescending(s => s.LastWatched),
            _ => series.OrderByDescending(s => s.LastWatched),
        };
        var list = ordered.ToList();
        return new Page<SeriesRow>(list.Skip(startIndex).Take(limit).ToList(), list.Count, startIndex);
    }

    private sealed record SummaryRow(
        Guid ViewingId, MediaKind Kind, ItemRef Item, string GroupKey, string? SeriesKey, bool Completed, bool InProgress,
        DateTimeOffset LastActivity, DateTimeOffset? CompletedAt, double Actual, double Credited, double Wall, double FurthestSeconds);

    private static List<SummaryRow> LoadForSummary(SqliteConnection c, Guid userId)
    {
        using var cmd = Command(c, $"""
            SELECT v.viewing_id, v.status, v.last_activity_at, v.completed_at, v.actual_seconds, v.credited_seconds,
                   v.wall_seconds, v.furthest_ticks, {MediaColumns},
                   (SELECT MIN(key) FROM media_keys k WHERE k.item_id = m.item_id) AS group_key
            FROM viewings v JOIN media m ON m.item_id = v.item_id
            WHERE v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL;
            """, ("$u", Id(userId)));
        using var r = cmd.ExecuteReader();
        var rows = new List<SummaryRow>();
        while (r.Read())
        {
            var item = ReadItem(r, 8);
            var status = r.GetString(1);
            var groupKey = r.IsDBNull(r.FieldCount - 1) ? "id:" + item.Id.ToString("N") : r.GetString(r.FieldCount - 1);
            rows.Add(new SummaryRow(
                Guid.Parse(r.GetString(0)), Enum.Parse<MediaKind>(item.Type), item, groupKey, SeriesKey(item),
                status == "completed", status == "in-progress", FromMs(r.GetInt64(2)),
                r.IsDBNull(3) ? null : FromMs(r.GetInt64(3)), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                Seconds(r.GetInt64(7))));
        }

        return rows;
    }

    /// <summary>One series across a re-added library: by its first provider id, else its id, else its name.</summary>
    private static string? SeriesKey(ItemRef item)
    {
        foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
        {
            var match = item.SeriesProviderIds.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Value))
            {
                return provider.ToLowerInvariant() + ":" + match.Value;
            }
        }

        return item.SeriesId is { } id ? "id:" + id.ToString("N") : item.SeriesName is { } name ? "n:" + name.ToLowerInvariant() : null;
    }

    private static IEnumerable<FilmRow> FilmRows(IEnumerable<SummaryRow> viewings) =>
        viewings.GroupBy(v => v.GroupKey).Select(g =>
        {
            var latest = g.OrderByDescending(v => v.LastActivity).First();
            var finished = g.Any(v => v.Completed);
            return new FilmRow
            {
                Item = latest.Item,
                Finished = finished,
                Viewings = g.Count(v => v.Completed),
                LastWatched = g.Max(v => v.LastActivity).UtcDateTime,
                RuntimeSeconds = g.Select(v => v.Item.RuntimeSeconds).FirstOrDefault(x => x is > 0),
                PositionSeconds = finished ? null : g.Where(v => v.InProgress).Select(v => (double?)v.FurthestSeconds).Max(),
                ActualSeconds = g.Sum(v => v.Actual),
                CreditedSeconds = g.Sum(v => v.Credited),
            };
        });

    private static IEnumerable<SeriesRow> SeriesRows(IEnumerable<SummaryRow> episodes, ILibraryFacts facts) =>
        episodes.Where(v => v.SeriesKey is not null).GroupBy(v => v.SeriesKey!).Select(g =>
        {
            var latest = g.OrderByDescending(v => v.LastActivity).First();
            var watched = g.Where(v => v.Completed).Select(v => v.GroupKey).Distinct().Count();
            var seriesId = latest.Item.SeriesId;
            var inLibrary = seriesId is { } id ? facts.EpisodesInSeries(id) : null;
            return new SeriesRow
            {
                SeriesId = seriesId,
                Name = latest.Item.SeriesName ?? "Untitled",
                ProviderIds = latest.Item.SeriesProviderIds,
                EpisodesWatched = watched,
                EpisodesInLibrary = inLibrary is { } n ? Math.Max(n, watched) : null,
                Finished = inLibrary is > 0 && watched >= inLibrary,
                InLibrary = inLibrary is not null,
                LastWatched = g.Max(v => v.LastActivity).UtcDateTime,
                ActualSeconds = g.Sum(v => v.Actual),
                CreditedSeconds = g.Sum(v => v.Credited),
            };
        });

    private static AudioBookSummary BookSummary(List<SummaryRow> books, ILibraryFacts facts)
    {
        var finished = 0;
        var underWay = 0;
        foreach (var book in books.GroupBy(v => v.Item.AlbumId?.ToString("N") ?? v.GroupKey))
        {
            var albumId = book.First().Item.AlbumId;
            var lastFile = albumId is { } id ? facts.LastFileOfBook(id) : null;
            // A book in one file is finished when that file is; one in a file per chapter, when its last file is.
            var done = lastFile is { } last
                ? book.Any(v => v.Completed && v.Item.Id == last)
                : book.Any(v => v.Completed);
            if (done)
            {
                finished++;
            }
            else if (book.Any(v => v.InProgress || v.Completed))
            {
                underWay++;
            }
        }

        return new AudioBookSummary(finished, underWay, books.Sum(v => v.Actual));
    }

    /// <summary>The local days with something counted, from sessions. A day runs 04:00 to 04:00, so a 1 a.m. episode belongs to the evening it started in.</summary>
    private static SortedSet<DateOnly> SessionDays(SqliteConnection c, Guid userId, TimeZoneInfo zone)
    {
        using var cmd = Command(c, """
            SELECT s.started_at FROM sessions s JOIN media m ON m.item_id = s.item_id
            WHERE s.user_id = $u AND s.counted = 1 AND m.kind IN ('Movie', 'Episode', 'Video');
            """, ("$u", Id(userId)));
        using var r = cmd.ExecuteReader();
        var days = new SortedSet<DateOnly>();
        while (r.Read())
        {
            days.Add(ToLocalDay(FromMs(r.GetInt64(0)), zone));
        }

        return days;
    }

    internal static DateOnly ToLocalDay(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).AddHours(-4).DateTime);

    internal static Streaks ComputeStreaks(SortedSet<DateOnly> days, DateOnly today)
    {
        if (days.Count == 0)
        {
            return new Streaks(0, 0, null, null);
        }

        int longest = 0, run = 0;
        DateOnly? runStart = null, bestFrom = null, bestTo = null, previous = null;
        foreach (var day in days)
        {
            run = previous is { } p && day.DayNumber - p.DayNumber == 1 ? run + 1 : 1;
            if (run == 1)
            {
                runStart = day;
            }

            if (run > longest)
            {
                longest = run;
                bestFrom = runStart;
                bestTo = day;
            }

            previous = day;
        }

        // Still alive if something was watched today, or yesterday and nothing yet today.
        var current = 0;
        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        while (days.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        return new Streaks(current, longest, bestFrom, bestTo);
    }

    private static List<YearRow> Years(SqliteConnection c, Guid userId, TimeZoneInfo zone, List<SummaryRow> video)
    {
        var viewings = video.Where(v => v.CompletedAt is not null)
            .GroupBy(v => TimeZoneInfo.ConvertTime(v.CompletedAt!.Value, zone).Year)
            .ToDictionary(g => g.Key, g => g.Count());
        var credited = video
            .GroupBy(v => TimeZoneInfo.ConvertTime(v.CompletedAt ?? v.LastActivity, zone).Year)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Credited));

        var actual = new Dictionary<int, double>();
        using (var cmd = Command(c, """
            SELECT s.started_at, s.actual_seconds FROM sessions s JOIN media m ON m.item_id = s.item_id
            WHERE s.user_id = $u AND s.counted = 1 AND m.kind IN ('Movie', 'Episode', 'Video');
            """, ("$u", Id(userId))))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var year = TimeZoneInfo.ConvertTime(FromMs(r.GetInt64(0)), zone).Year;
                actual[year] = actual.GetValueOrDefault(year) + r.GetDouble(1);
            }
        }

        return viewings.Keys.Union(credited.Keys).Union(actual.Keys).OrderByDescending(y => y)
            .Select(y => new YearRow(y, viewings.GetValueOrDefault(y), actual.GetValueOrDefault(y), credited.GetValueOrDefault(y)))
            .ToList();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var known = values.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return known.Count == 0 ? null : known.Average();
    }

    // — Export (§9.2) —

    public void ExportJson(Stream output, IReadOnlyList<Guid> userIds)
    {
        using var c = _database.Open(readOnly: true);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("ExportedAt", DateTime.UtcNow);
        writer.WriteString("Format", "JellyMold Companion history, version 1");
        writer.WriteStartArray("People");
        foreach (var userId in userIds)
        {
            var user = User(userId);
            writer.WriteStartObject();
            writer.WriteString("UserId", userId.ToString("N"));
            writer.WriteString("Name", user?.Name);
            writer.WritePropertyName("History");
            JsonSerializer.Serialize(writer, ReadEntries(c, $"{EntrySelect} WHERE v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL ORDER BY v.started_at;", ("$u", Id(userId))));
            writer.WritePropertyName("Sessions");
            JsonSerializer.Serialize(writer, ReadSessions(c, $"{SessionSelect} WHERE s.user_id = $u ORDER BY s.started_at;", ("$u", Id(userId))));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public void ExportCsv(TextWriter output, IReadOnlyList<Guid> userIds)
    {
        using var c = _database.Open(readOnly: true);
        output.WriteLine("user,started_at,completed_at,status,kind,source,type,title,series,season,episode,album,artist,year,runtime_seconds,actual_seconds,credited_seconds,tmdb,imdb,tvdb,in_library");
        foreach (var userId in userIds)
        {
            var name = User(userId)?.Name ?? userId.ToString("N");
            foreach (var e in ReadEntries(c, $"{EntrySelect} WHERE v.user_id = $u AND v.status IN {Counted} AND v.superseded_by IS NULL ORDER BY v.started_at;", ("$u", Id(userId))))
            {
                var ids = e.Item.ProviderIds;
                string Provider(string key) => ids.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
                output.WriteLine(string.Join(',', new[]
                {
                    name, e.StartedAt.ToString("o", CultureInfo.InvariantCulture), e.CompletedAt?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
                    e.Status, e.Kind, e.Source, e.Item.Type, e.Item.Name, e.Item.SeriesName ?? string.Empty,
                    e.Item.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    e.Item.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    e.Item.AlbumName ?? string.Empty, e.Item.Artists.FirstOrDefault() ?? string.Empty,
                    e.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    e.Item.RuntimeSeconds?.ToString("0", CultureInfo.InvariantCulture) ?? string.Empty,
                    e.ActualSeconds.ToString("0", CultureInfo.InvariantCulture), e.CreditedSeconds.ToString("0", CultureInfo.InvariantCulture),
                    Provider("Tmdb"), Provider("Imdb"), Provider("Tvdb"), e.Item.InLibrary ? "yes" : "no",
                }.Select(Csv)));
            }
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    // — Imports (§8) —

    public sealed record ImportInfo(Guid Id, string Source, Guid? UserId, string? UserName, DateTime CreatedAt,
        DateTime? CommittedAt, DateTime? UndoneAt, Import.ImportReport? Report);

    /// <summary>Imports, newest first: one person's, or — with no person — every import, server-wide ones included.</summary>
    public IReadOnlyList<ImportInfo> Imports(Guid? userId)
    {
        using var c = _database.Open(readOnly: true);
        using var cmd = Command(c, $"""
            SELECT i.import_id, i.source, i.user_id, u.name, i.created_at, i.committed_at, i.undone_at, i.detail
            FROM imports i LEFT JOIN users u ON u.user_id = i.user_id
            {(userId is null ? string.Empty : "WHERE i.user_id = $u")}
            ORDER BY i.created_at DESC LIMIT 200;
            """, ("$u", Id(userId)));
        using var r = cmd.ExecuteReader();
        var list = new List<ImportInfo>();
        while (r.Read())
        {
            list.Add(new ImportInfo(Guid.Parse(r.GetString(0)), r.GetString(1), NullableGuid(r, 2), NullableString(r, 3),
                Time(r.GetInt64(4)), r.IsDBNull(5) ? null : Time(r.GetInt64(5)), r.IsDBNull(6) ? null : Time(r.GetInt64(6)),
                r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Import.ImportReport>(r.GetString(7))));
        }

        return list;
    }

    public ImportInfo? ImportById(Guid importId) => Imports(null).FirstOrDefault(i => i.Id == importId);

    /// <summary>Whether this person already has a committed import from this source — for the baseline, whether it has been taken (§8.2).</summary>
    public bool HasCommittedImport(string source, Guid userId)
    {
        using var c = _database.Open(readOnly: true);
        return Convert.ToInt64(ScalarOf(c,
            "SELECT COUNT(*) FROM imports WHERE source = $s AND user_id = $u AND committed_at IS NOT NULL;",
            ("$s", source), ("$u", Id(userId))), CultureInfo.InvariantCulture) > 0;
    }

    // — Database health, for the admin page (§11) —

    public sealed record DatabaseStatus(long Viewings, long Sessions, long Events, long Samples, long People, long Items, long Bytes);

    public DatabaseStatus Status()
    {
        using var c = _database.Open(readOnly: true);
        long Count(string table) => Convert.ToInt64(ScalarOf(c, $"SELECT COUNT(*) FROM {table};"), CultureInfo.InvariantCulture);
        var bytes = new[] { "", "-wal" }.Select(s => _database.FilePath + s).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        return new DatabaseStatus(Count("viewings"), Count("sessions"), Count("events"), Count("progress_samples"), Count("users"), Count("media"), bytes);
    }

    // — Reading rows —

    private const string MediaColumns = """
        m.item_id, m.kind, m.name, m.original_title, m.production_year, m.runtime_ticks, m.series_id, m.series_name,
        m.season_id, m.season_number, m.episode_number, m.episode_number_end, m.album_id, m.album_name, m.artists,
        m.album_artists, m.provider_ids, m.series_provider_ids, m.removed_at, m.origin
        """;

    private const int MediaColumnCount = 20;

    // A superseded entry reads as such (the change feed passes it on as a removal); everywhere else filters it out.
    private const string EntryColumns = """
        v.viewing_id, v.kind, v.source, CASE WHEN v.superseded_by IS NOT NULL THEN 'superseded' ELSE v.status END, v.started_at, v.last_activity_at, v.completed_at, v.completed_by,
        v.furthest_ticks, v.actual_seconds, v.wall_seconds, v.credited_seconds, v.estimated,
        (SELECT COUNT(*) FROM viewings v2 WHERE v2.user_id = v.user_id AND v2.item_id = v.item_id AND v2.started_at <= v.started_at
            AND v2.status IN ('in-progress', 'completed', 'abandoned') AND v2.superseded_by IS NULL) AS ordinal,
        (SELECT COUNT(*) FROM sessions s WHERE s.viewing_id = v.viewing_id) AS session_count
        """;

    private const int EntryColumnCount = 15;

    private static readonly string EntrySelect = $"SELECT {EntryColumns}, {MediaColumns} FROM viewings v JOIN media m ON m.item_id = v.item_id";

    private static readonly string EntrySelectWithSeq = $"SELECT {EntryColumns}, {MediaColumns}, v.updated_seq FROM viewings v JOIN media m ON m.item_id = v.item_id";

    private static List<HistoryEntry> ReadEntries(SqliteConnection c, string sql, params (string, object?)[] parameters) =>
        ReadEntries(c, sql, parameters, out _);

    private static List<HistoryEntry> ReadEntries(SqliteConnection c, string sql, (string, object?)[] parameters, out List<long> seqs)
    {
        using var cmd = Command(c, sql, parameters);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        seqs = [];
        var hasSeq = r.FieldCount > EntryColumnCount + MediaColumnCount;
        while (r.Read())
        {
            list.Add(new HistoryEntry
            {
                Id = Guid.Parse(r.GetString(0)),
                Kind = Pascal(r.GetString(1)),
                Source = r.GetString(2),
                Status = Pascal(r.GetString(3)),
                StartedAt = Time(r.GetInt64(4)),
                LastActivityAt = Time(r.GetInt64(5)),
                CompletedAt = r.IsDBNull(6) ? null : Time(r.GetInt64(6)),
                CompletedBy = r.IsDBNull(7) ? null : Pascal(r.GetString(7)),
                FurthestSeconds = Seconds(r.GetInt64(8)),
                ActualSeconds = r.GetDouble(9),
                WallSeconds = r.GetDouble(10),
                CreditedSeconds = r.GetDouble(11),
                Estimated = r.GetInt64(12) != 0,
                Ordinal = r.GetInt32(13),
                Sessions = r.GetInt32(14),
                Item = ReadItem(r, EntryColumnCount),
            });
            if (hasSeq)
            {
                seqs.Add(r.GetInt64(EntryColumnCount + MediaColumnCount));
            }
        }

        return list;
    }

    private static ItemRef ReadItem(SqliteDataReader r, int o) => new()
    {
        Id = Guid.Parse(r.GetString(o)),
        Type = r.GetString(o + 1),
        Name = r.GetString(o + 2),
        OriginalTitle = NullableString(r, o + 3),
        ProductionYear = r.IsDBNull(o + 4) ? null : r.GetInt32(o + 4),
        RuntimeSeconds = r.IsDBNull(o + 5) ? null : Seconds(r.GetInt64(o + 5)),
        SeriesId = NullableGuid(r, o + 6),
        SeriesName = NullableString(r, o + 7),
        SeasonId = NullableGuid(r, o + 8),
        SeasonNumber = r.IsDBNull(o + 9) ? null : r.GetInt32(o + 9),
        EpisodeNumber = r.IsDBNull(o + 10) ? null : r.GetInt32(o + 10),
        EpisodeNumberEnd = r.IsDBNull(o + 11) ? null : r.GetInt32(o + 11),
        AlbumId = NullableGuid(r, o + 12),
        AlbumName = NullableString(r, o + 13),
        Artists = JsonList(NullableString(r, o + 14)),
        AlbumArtists = JsonList(NullableString(r, o + 15)),
        ProviderIds = JsonMap(NullableString(r, o + 16)),
        SeriesProviderIds = JsonMap(NullableString(r, o + 17)),
        RemovedAt = r.IsDBNull(o + 18) ? null : Time(r.GetInt64(o + 18)),
        // In the library now: never removed, and not something only an import mentioned.
        InLibrary = r.IsDBNull(o + 18) && r.IsDBNull(o + 19),
        Origin = r.IsDBNull(o + 19) ? "Library" : "Import",
    };

    private const string SessionSelect = """
        SELECT s.session_id, s.item_id, s.viewing_id, s.source, s.client, s.device_name, s.play_method, s.started_at,
               s.ended_at, s.start_ticks, s.end_ticks, s.furthest_ticks, s.actual_seconds, s.wall_seconds,
               s.paused_seconds, s.seek_count, s.end_reason, s.counted
        FROM sessions s
        """;

    private static List<SessionEntry> ReadSessions(SqliteConnection c, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(c, sql, parameters);
        using var r = cmd.ExecuteReader();
        var list = new List<SessionEntry>();
        while (r.Read())
        {
            list.Add(new SessionEntry
            {
                Id = Guid.Parse(r.GetString(0)),
                ItemId = Guid.Parse(r.GetString(1)),
                EntryId = NullableGuid(r, 2),
                Source = r.GetString(3),
                Client = NullableString(r, 4),
                DeviceName = NullableString(r, 5),
                PlayMethod = NullableString(r, 6),
                StartedAt = Time(r.GetInt64(7)),
                EndedAt = r.IsDBNull(8) ? null : Time(r.GetInt64(8)),
                StartPositionSeconds = Seconds(r.GetInt64(9)),
                EndPositionSeconds = r.IsDBNull(10) ? null : Seconds(r.GetInt64(10)),
                FurthestSeconds = Seconds(r.GetInt64(11)),
                ActualSeconds = r.GetDouble(12),
                WallSeconds = r.GetDouble(13),
                PausedSeconds = r.GetDouble(14),
                Seeks = r.GetInt32(15),
                EndReason = NullableString(r, 16),
                Counted = r.GetInt64(17) != 0,
            });
        }

        return list;
    }

    private static readonly IReadOnlyDictionary<string, string> NoIds = new Dictionary<string, string>();

    private static IReadOnlyList<string> JsonList(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static IReadOnlyDictionary<string, string> JsonMap(string? json) =>
        string.IsNullOrEmpty(json) ? NoIds : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? NoIds;

    /// <summary><c>in-progress</c> → <c>InProgress</c>: the database's words in the API's style.</summary>
    private static string Pascal(string stored) =>
        string.Concat(stored.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    private static string? Id(Guid? id) => id?.ToString("N");

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static DateTime Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static DateTimeOffset? NullableTime(SqliteDataReader r, int o) => r.IsDBNull(o) ? null : FromMs(r.GetInt64(o));

    private static double Seconds(long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;

    private static string? NullableString(SqliteDataReader r, int o) => r.IsDBNull(o) ? null : r.GetString(o);

    private static Guid? NullableGuid(SqliteDataReader r, int o) => r.IsDBNull(o) ? null : Guid.Parse(r.GetString(o));

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

    private static object? ScalarOf(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(c, sql, parameters);
        return cmd.ExecuteScalar();
    }
}
