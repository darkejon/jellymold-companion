using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.Import;

/// <summary>
/// Playback Reporting's database (§8.3), read directly and read-only. One row a play: a local-time date, user,
/// item, client, device and seconds played (pauses already taken out). No positions, so plays are assembled into
/// viewings by seconds against the running time.
/// </summary>
public static partial class PlaybackReportingSource
{
    public sealed record Row(string Key, DateTimeOffset StartedAt, Guid UserId, Guid ItemId, string? ItemType, string? ItemName,
        string? PlaybackMethod, string? ClientName, string? DeviceName, int Seconds, bool AmbiguousTime);

    /// <summary>
    /// Reads every row. Dates are the server's local time stored as text with no offset, so they are converted with
    /// the server's time zone; the hour repeated when the clocks go back is ambiguous, and such rows are flagged.
    /// </summary>
    public static IReadOnlyList<Row> Read(string path, TimeZoneInfo serverZone)
    {
        var rows = new List<Row>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration
            FROM PlaybackActivity ORDER BY DateCreated;
            """;
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            var dateText = r.IsDBNull(0) ? null : r.GetValue(0).ToString();
            if (dateText is null || !Guid.TryParse(Text(r, 1), out var user) || !Guid.TryParse(Text(r, 2), out var item) ||
                !DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                continue;
            }

            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            var ambiguous = serverZone.IsAmbiguousTime(local);
            DateTimeOffset at;
            try
            {
                at = new DateTimeOffset(local, serverZone.GetUtcOffset(local));
            }
            catch (ArgumentException)
            {
                // A time skipped when the clocks went forward: read it as standard time.
                at = new DateTimeOffset(local, serverZone.BaseUtcOffset);
            }

            rows.Add(new Row(
                // No primary key: a row is its date, user and item, the same three columns the plugin finds its own rows by.
                $"{dateText}|{user:N}|{item:N}",
                at.ToUniversalTime(), user, item, Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6), Text(r, 7),
                r.IsDBNull(8) ? 0 : Convert.ToInt32(r.GetValue(8), CultureInfo.InvariantCulture), ambiguous));
        }

        return rows;
    }

    /// <summary>
    /// Plays of the same item by the same person, gathered into viewings: added together until the seconds reach the
    /// completion point, then the next play starts a new viewing. A gap of more than sixty days also starts one.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> Assemble(IReadOnlyList<Row> rows, Func<Guid, MediaSnapshot?> library,
        Func<Guid, string?> userName, CountingRules rules)
    {
        var candidates = new List<ImportCandidate>();
        foreach (var group in rows.GroupBy(r => (r.UserId, r.ItemId)))
        {
            var item = library(group.Key.ItemId) ?? FromName(group.First());
            if (item is null)
            {
                continue;
            }

            var matched = library(group.Key.ItemId) is not null;
            var name = userName(group.Key.UserId) ?? "(deleted user)";
            var current = new List<Row>();

            void Close(bool completed, DateTimeOffset? completedAt)
            {
                if (current.Count == 0)
                {
                    return;
                }

                var seconds = current.Sum(r => (double)r.Seconds);
                var end = current[^1].StartedAt.AddSeconds(current[^1].Seconds);
                candidates.Add(new ImportCandidate
                {
                    UserId = group.Key.UserId,
                    UserName = name,
                    Item = item,
                    Matched = matched,
                    SourceKey = current[0].Key,
                    Completed = completed,
                    StartedAt = current[0].StartedAt,
                    LastActivityAt = end,
                    CompletedAt = completedAt,
                    FurthestTicks = TimeSpan.FromSeconds(Math.Min(seconds, item.Runtime?.TotalSeconds ?? seconds)).Ticks,
                    ActualSeconds = seconds,
                    WallSeconds = seconds,
                    CreditedSeconds = Completion.Credited(item.Kind, item.RuntimeTicks, seconds, completed ? CompletedBy.Import : null, rules),
                    Plays = current.Select(r => new ImportedPlay(r.StartedAt, r.Seconds, r.ClientName, r.DeviceName, r.PlaybackMethod)).ToList(),
                    Note = current.Any(r => r.AmbiguousTime) ? "A play's local time fell in the hour the clocks went back." : null,
                });
                current.Clear();
            }

            foreach (var row in group.OrderBy(r => r.StartedAt))
            {
                if (current.Count > 0 && row.StartedAt - current[^1].StartedAt.AddSeconds(current[^1].Seconds) > TimeSpan.FromDays(60))
                {
                    Close(false, null);
                }

                current.Add(row);
                var heard = current.Sum(r => (double)r.Seconds);
                if (Completion.IsComplete(item.Kind, item.RuntimeTicks, TimeSpan.FromSeconds(heard).Ticks, heard, rules))
                {
                    Close(true, row.StartedAt.AddSeconds(row.Seconds));
                }
            }

            Close(false, null);
        }

        return candidates;
    }

    /// <summary>An item since deleted: what its row's name says. Episodes are "Series - s01e02 - Title".</summary>
    internal static MediaSnapshot? FromName(Row row)
    {
        var name = row.ItemName ?? string.Empty;
        switch (row.ItemType)
        {
            case "Episode" when EpisodeName().Match(name) is { Success: true } m:
                return new MediaSnapshot
                {
                    ItemId = row.ItemId,
                    Kind = MediaKind.Episode,
                    Name = m.Groups["title"].Value,
                    SeriesName = m.Groups["series"].Value,
                    SeriesId = Unmatched.SyntheticId("series:" + LibraryIndex.Normalise(m.Groups["series"].Value)),
                    SeasonNumber = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                    EpisodeNumber = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture),
                };
            case "Audio" when SongName().Match(name) is { Success: true } m:
                return new MediaSnapshot
                {
                    ItemId = row.ItemId,
                    Kind = MediaKind.Audio,
                    Name = m.Groups["track"].Value,
                    Artists = [m.Groups["artist"].Value],
                    AlbumName = m.Groups["album"].Value,
                };
            case "Movie" or "Episode" or "Video" or "MusicVideo" or "Audio" or "AudioBook" when name.Length > 0:
                return new MediaSnapshot
                {
                    ItemId = row.ItemId,
                    Kind = row.ItemType switch
                    {
                        "Movie" => MediaKind.Movie,
                        "Episode" => MediaKind.Episode,
                        "Audio" => MediaKind.Audio,
                        "AudioBook" => MediaKind.AudioBook,
                        _ => MediaKind.Video,
                    },
                    Name = name,
                };
            default:
                return null;
        }
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();

    [GeneratedRegex(@"^(?<series>.*) - s(?<s>\d+)e(?<e>\d+) - (?<title>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeName();

    [GeneratedRegex(@"^(?<artist>.*) - (?<track>.*) \((?<album>.*)\)$")]
    private static partial Regex SongName();
}

/// <summary>
/// A Trakt export (§8.5): the history format (one entry per watch, with Trakt's id for it) or the watched format
/// (one entry per film or episode, with its last watched date). Dates and provider ids, no durations.
/// </summary>
public static class TraktExportSource
{
    public static IReadOnlyList<ImportCandidate> Read(Stream json, Guid userId, string userName, LibraryIndex library, CountingRules rules)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("A Trakt export is a list of watched entries.");
        }

        var candidates = new List<ImportCandidate>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("watched_at", out var watchedAt))
            {
                // History: one entry per watch.
                var key = entry.TryGetProperty("id", out var id) ? "h:" + id.ToString() : null;
                AddWatch(entry, Date(watchedAt), key, null);
            }
            else if (entry.TryGetProperty("last_watched_at", out var last) && entry.TryGetProperty("movie", out _))
            {
                AddWatch(entry, Date(last), null, Plays(entry));
            }
            else if (entry.TryGetProperty("seasons", out var seasons) && entry.TryGetProperty("show", out var show))
            {
                foreach (var season in seasons.EnumerateArray())
                {
                    foreach (var episode in season.GetProperty("episodes").EnumerateArray())
                    {
                        if (!episode.TryGetProperty("last_watched_at", out var at))
                        {
                            continue;
                        }

                        Add(EpisodeOf(show, Int(season, "number"), Int(episode, "number"), null, Ids(null)), Date(at), null, Plays(episode));
                    }
                }
            }
        }

        return candidates;

        void AddWatch(JsonElement entry, DateTimeOffset when, string? key, int? plays)
        {
            if (entry.TryGetProperty("movie", out var movie))
            {
                var ids = Ids(movie);
                var title = Str(movie, "title") ?? "Untitled";
                var year = Int(movie, "year");
                Add((library.Film(ids, title, year), Unmatched.Film(title, year, ids)), when, key, plays);
            }
            else if (entry.TryGetProperty("episode", out var episode) && entry.TryGetProperty("show", out var show))
            {
                Add(EpisodeOf(show, Int(episode, "season"), Int(episode, "number"), Str(episode, "title"), Ids(episode)), when, key, plays);
            }
        }

        (MediaSnapshot? Matched, MediaSnapshot Fallback) EpisodeOf(JsonElement show, int? season, int? number, string? title, IReadOnlyDictionary<string, string> episodeIds)
        {
            var showIds = Ids(show);
            return (library.Episode(showIds, season, number, episodeIds),
                Unmatched.Episode(Str(show, "title") ?? "Untitled", showIds, season, number, title, episodeIds));
        }

        void Add((MediaSnapshot? Matched, MediaSnapshot Fallback) item, DateTimeOffset when, string? key, int? plays)
        {
            var snapshot = item.Matched ?? item.Fallback;
            candidates.Add(Watched(userId, userName, snapshot, item.Matched is not null,
                key ?? string.Create(CultureInfo.InvariantCulture, $"w:{snapshot.ItemId:N}|{when:O}"), when, rules, plays));
        }
    }

    private static IReadOnlyDictionary<string, string> Ids(JsonElement? owner)
    {
        var map = new Dictionary<string, string>();
        if (owner is { } o && o.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
        {
            foreach (var (from, to) in new[] { ("tmdb", "Tmdb"), ("imdb", "Imdb"), ("tvdb", "Tvdb") })
            {
                if (ids.TryGetProperty(from, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        map[to] = text;
                    }
                }
            }
        }

        return map;
    }

    private static int? Plays(JsonElement entry) => Int(entry, "plays");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset Date(JsonElement value) =>
        DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// A completed viewing from a source with dates and no durations (§8.5). History from before the plugin existed:
    /// credited its running time where the library knows it, as the install-time baseline is (decided 2026-10-30).
    /// </summary>
    internal static ImportCandidate Watched(Guid userId, string userName, MediaSnapshot item, bool matched, string key,
        DateTimeOffset when, CountingRules rules, int? plays = null) => new()
    {
        UserId = userId,
        UserName = userName,
        Item = item,
        Matched = matched,
        SourceKey = key,
        Completed = true,
        StartedAt = when,
        LastActivityAt = when,
        CompletedAt = when,
        FurthestTicks = item.RuntimeTicks ?? 0,
        CreditedSeconds = Completion.Credited(item.Kind, item.RuntimeTicks, 0, CompletedBy.Import, rules),
        Estimated = true,
        PlayCountNoted = plays,
    };
}

/// <summary>
/// Letterboxd's diary (<c>diary.csv</c>: Date, Name, Year, Letterboxd URI, Rating, Rewatch, Tags, Watched Date) or
/// watched list (<c>watched.csv</c>: Date, Name, Year, Letterboxd URI). Films only, and **no provider ids**: matched
/// by title and year, so more land unmatched, and the preview says which.
/// </summary>
public static class LetterboxdSource
{
    public static IReadOnlyList<ImportCandidate> Read(TextReader csv, Guid userId, string userName, LibraryIndex library, CountingRules rules)
    {
        var rows = Csv.Read(csv);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = Csv.Header(rows[0]);
        if (!header.ContainsKey("name") || !(header.ContainsKey("date") || header.ContainsKey("watched date")))
        {
            throw new FormatException("This does not look like a Letterboxd diary.csv or watched.csv.");
        }

        var candidates = new List<ImportCandidate>();
        foreach (var row in rows.Skip(1))
        {
            var title = Csv.Field(row, header, "name");
            var dateText = Csv.Field(row, header, "watched date") ?? Csv.Field(row, header, "date");
            if (string.IsNullOrWhiteSpace(title) || !Csv.TryDate(dateText, out var when))
            {
                continue;
            }

            var year = int.TryParse(Csv.Field(row, header, "year"), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : (int?)null;
            var none = new Dictionary<string, string>();
            var matched = library.Film(none, title, year);
            var item = matched ?? Unmatched.Film(title, year, none);
            var uri = Csv.Field(row, header, "letterboxd uri") ?? title + "|" + year;
            candidates.Add(TraktExportSource.Watched(userId, userName, item, matched is not null, uri + "|" + dateText, when, rules));
        }

        return candidates;
    }
}

/// <summary>
/// A documented CSV for anything else (docs/api.md): <c>date</c> (required), <c>type</c> (movie, episode, audio,
/// audiobook; movie when left out), <c>title</c>, <c>year</c>, <c>tmdb</c>, <c>imdb</c>, <c>tvdb</c>, <c>series</c>,
/// <c>season</c>, <c>episode</c>, <c>seconds</c> (time actually watched, if known), <c>completed</c> (yes or no; yes).
/// </summary>
public static class CsvSource
{
    public static IReadOnlyList<ImportCandidate> Read(TextReader csv, Guid userId, string userName, LibraryIndex library, CountingRules rules)
    {
        var rows = Csv.Read(csv);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = Csv.Header(rows[0]);
        if (!header.ContainsKey("date"))
        {
            throw new FormatException("The CSV needs a 'date' column. See docs/api.md for the columns.");
        }

        var candidates = new List<ImportCandidate>();
        foreach (var row in rows.Skip(1))
        {
            if (!Csv.TryDate(Csv.Field(row, header, "date"), out var when))
            {
                continue;
            }

            string? F(string name) => Csv.Field(row, header, name);
            int? I(string name) => int.TryParse(F(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            var ids = new Dictionary<string, string>();
            foreach (var (column, provider) in new[] { ("tmdb", "Tmdb"), ("imdb", "Imdb"), ("tvdb", "Tvdb") })
            {
                if (!string.IsNullOrWhiteSpace(F(column)))
                {
                    ids[provider] = F(column)!.Trim();
                }
            }

            var type = (F("type") ?? "movie").Trim().ToLowerInvariant();
            MediaSnapshot? matched;
            MediaSnapshot item;
            if (type == "episode")
            {
                var series = F("series") ?? F("title") ?? "Untitled";
                matched = library.Episode(ids, I("season"), I("episode"), new Dictionary<string, string>());
                item = matched ?? Unmatched.Episode(series, ids, I("season"), I("episode"), F("title"), new Dictionary<string, string>());
            }
            else
            {
                var title = F("title") ?? "Untitled";
                matched = type == "movie" ? library.Film(ids, title, I("year")) : null;
                var kind = type switch { "audio" => MediaKind.Audio, "audiobook" => MediaKind.AudioBook, _ => MediaKind.Movie };
                item = matched ?? (Unmatched.Film(title, I("year"), ids) with { Kind = kind });
            }

            var key = "csv:" + string.Join("|", row);
            var completed = !string.Equals(F("completed")?.Trim(), "no", StringComparison.OrdinalIgnoreCase);
            var seconds = double.TryParse(F("seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : (double?)null;
            var candidate = TraktExportSource.Watched(userId, userName, item, matched is not null, key, when, rules);
            candidates.Add(completed && seconds is null
                ? candidate
                : candidate with
                {
                    Completed = completed,
                    CompletedAt = completed ? when : null,
                    ActualSeconds = seconds ?? 0,
                    WallSeconds = seconds ?? 0,
                    Estimated = seconds is null,
                    CreditedSeconds = Completion.Credited(item.Kind, item.RuntimeTicks, seconds ?? 0, completed ? CompletedBy.Import : null, rules),
                });
        }

        return candidates;
    }
}

/// <summary>A small CSV reader: quoted fields, doubled quotes, newlines inside quotes.</summary>
public static class Csv
{
    public static List<List<string>> Read(TextReader reader)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    public static Dictionary<string, int> Header(List<string> row) =>
        row.Select((name, i) => (name: name.Trim().TrimStart('﻿').ToLowerInvariant(), i))
            .GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First().i);

    public static string? Field(List<string> row, Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var i) && i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

    /// <summary>A date with a time and offset, or a plain date — which is read as midday UTC, so it stays on its day everywhere.</summary>
    public static bool TryDate(string? text, out DateTimeOffset when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            when = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            return true;
        }

        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when);
    }
}
