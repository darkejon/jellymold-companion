using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyMoldCompanion.Tests;

/// <summary>
/// A recorder on a real SQLite file in a temporary folder, and a vocabulary for writing playback the way Jellyfin
/// reports it: positions and times in seconds, so a test reads like the scenario it describes.
/// </summary>
public sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset Tuesday = new(2026, 10, 27, 21, 0, 0, TimeSpan.Zero);

    public static readonly RecordedUser Jon = new(Guid.Parse("11111111111111111111111111111111"), "jon");
    public static readonly RecordedUser Sam = new(Guid.Parse("22222222222222222222222222222222"), "sam");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jmc-tests-" + Guid.NewGuid().ToString("N"));

    public Harness(CountingRules? rules = null, RecordingScope? scope = null)
    {
        Rules = rules ?? new CountingRules();
        Database = new HistoryDatabase(_directory, NullLogger.Instance);
        Database.Initialise();
        Store = new HistoryStore(Database);
        Recorder = new Recorder(Store, () => Rules, () => scope ?? RecordingScope.Everything, NullLogger.Instance);
    }

    public CountingRules Rules { get; set; }
    public HistoryDatabase Database { get; }
    public HistoryStore Store { get; }
    public Recorder Recorder { get; }

    public void Dispose()
    {
        Store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // — Items —

    public static MediaSnapshot Episode(int minutes = 44, Guid? id = null, string tvdbSeries = "73244", int season = 3, int episode = 7) => new()
    {
        ItemId = id ?? Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        Kind = MediaKind.Episode,
        Name = "Episode",
        SeriesId = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        SeriesName = "Deep Space Nine",
        SeasonId = Guid.Parse("cccccccccccccccccccccccccccccccc"),
        SeasonNumber = season,
        EpisodeNumber = episode,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = tvdbSeries },
    };

    public static MediaSnapshot Film(int minutes = 120, Guid? id = null, string tmdb = "603") => new()
    {
        ItemId = id ?? Guid.Parse("dddddddddddddddddddddddddddddddd"),
        Kind = MediaKind.Movie,
        Name = "The Matrix",
        ProductionYear = 1999,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdb },
    };

    public static MediaSnapshot Song(int seconds = 200) => new()
    {
        ItemId = Guid.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        Kind = MediaKind.Audio,
        Name = "A Song",
        AlbumName = "An Album",
        Artists = ["An Artist"],
        RuntimeTicks = TimeSpan.FromSeconds(seconds).Ticks,
    };

    public static MediaSnapshot Book(int hours = 12) => new()
    {
        ItemId = Guid.Parse("ffffffffffffffffffffffffffffffff"),
        Kind = MediaKind.AudioBook,
        Name = "A Book",
        RuntimeTicks = TimeSpan.FromHours(hours).Ticks,
    };

    // — Reports —

    public static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    public PlaybackReport Report(ReportKind kind, MediaSnapshot item, DateTimeOffset at, double positionSeconds,
        bool paused = false, bool automated = false, string session = "phone", string? playSession = null,
        params RecordedUser[] users) => new(at)
    {
        Kind = kind,
        JellyfinSessionId = session,
        PlaySessionId = playSession,
        Users = users.Length == 0 ? [Jon] : users,
        Item = item,
        PositionTicks = Ticks(positionSeconds),
        IsPaused = paused,
        IsAutomated = automated,
        Client = "JellyMold",
        DeviceName = "iPhone",
    };

    public void Start(MediaSnapshot item, DateTimeOffset at, double position, string session = "phone", params RecordedUser[] users) =>
        Recorder.Apply(Report(ReportKind.Start, item, at, position, session: session, users: users));

    public void Progress(MediaSnapshot item, DateTimeOffset at, double position, bool paused = false, bool automated = false, string session = "phone") =>
        Recorder.Apply(Report(ReportKind.Progress, item, at, position, paused, automated, session));

    public void Stop(MediaSnapshot item, DateTimeOffset at, double position, string session = "phone", string? playSession = null) =>
        Recorder.Apply(Report(ReportKind.Stop, item, at, position, session: session, playSession: playSession));

    /// <summary>
    /// Plays from one position to another at normal speed, reporting every <paramref name="step"/> seconds as a
    /// client does. Returns the moment it got there.
    /// </summary>
    public DateTimeOffset Play(MediaSnapshot item, DateTimeOffset at, double from, double to, double step = 10, string session = "phone", double rate = 1)
    {
        var position = from;
        var now = at;
        while (position < to)
        {
            var advance = Math.Min(step * rate, to - position);
            position += advance;
            now += TimeSpan.FromSeconds(advance / rate);
            Progress(item, now, position, session: session);
        }

        return now;
    }

    public void Marked(MediaSnapshot item, DateTimeOffset at, bool played, SaveReason reason = SaveReason.TogglePlayed, RecordedUser? user = null, double position = 0) =>
        Recorder.Apply(new UserDataSaved(at)
        {
            User = user ?? Jon,
            Item = item,
            Reason = reason,
            Played = played,
            PositionTicks = Ticks(position),
        });

    /// <summary>
    /// The answer to a message the recorder has already applied. <see cref="Recorder.Apply"/> is synchronous, so its
    /// answers are always in by the time a test asks.
    /// </summary>
    public static T Answer<T>(Task<T> task) =>
        task.IsCompleted ? task.GetAwaiter().GetResult() : throw new InvalidOperationException("The recorder has not answered.");

    // — Reading back —

    public sealed record ViewingRow(string Id, string ItemId, string Kind, string Status, string? CompletedBy,
        double Actual, double Credited, double Wall, long FurthestTicks, long? CompletedAt, long StartedAt);

    public sealed record SessionRow(string Id, string? ViewingId, double Actual, double Wall, double Paused,
        int Seeks, string? EndReason, bool Counted, long StartTicks, long? EndTicks, long? EndedAt);

    public IReadOnlyList<ViewingRow> Viewings(RecordedUser? user = null) => Query(
        "SELECT viewing_id, item_id, kind, status, completed_by, actual_seconds, credited_seconds, wall_seconds, furthest_ticks, completed_at, started_at FROM viewings WHERE user_id = $u ORDER BY started_at, rowid",
        r => new ViewingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.GetInt64(10)),
        user ?? Jon);

    public IReadOnlyList<SessionRow> Sessions(RecordedUser? user = null) => Query(
        "SELECT session_id, viewing_id, actual_seconds, wall_seconds, paused_seconds, seek_count, end_reason, counted, start_ticks, end_ticks, ended_at FROM sessions WHERE user_id = $u ORDER BY started_at, rowid",
        r => new SessionRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
            r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0, r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9),
            r.IsDBNull(10) ? null : r.GetInt64(10)),
        user ?? Jon);

    public IReadOnlyList<string> EventKinds(RecordedUser? user = null) => Query(
        "SELECT kind FROM events WHERE user_id = $u ORDER BY event_id", r => r.GetString(0), user ?? Jon);

    public long Scalar(string sql)
    {
        using var connection = Database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, RecordedUser user)
    {
        using var connection = Database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$u", user.Id.ToString("N"));
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}

public static class TimeExtensions
{
    public static DateTimeOffset Seconds(this DateTimeOffset at, double seconds) => at + TimeSpan.FromSeconds(seconds);

    public static DateTimeOffset Minutes(this DateTimeOffset at, double minutes) => at + TimeSpan.FromMinutes(minutes);
}
