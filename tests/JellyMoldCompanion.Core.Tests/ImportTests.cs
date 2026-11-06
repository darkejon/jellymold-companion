using System.Text;
using JellyMoldCompanion.Api;
using JellyMoldCompanion.Import;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §8: the rules every import follows.</summary>
public class ImporterTests
{
    private static readonly DateTimeOffset Install = Harness.Tuesday.AddDays(100);

    private static ImportCandidate Watched(MediaSnapshot item, DateTimeOffset when, string key, bool matched = true) =>
        new()
        {
            UserId = Harness.Jon.Id,
            UserName = Harness.Jon.Name,
            Item = item,
            Matched = matched,
            SourceKey = key,
            Completed = true,
            StartedAt = when,
            LastActivityAt = when,
            CompletedAt = when,
            FurthestTicks = item.RuntimeTicks ?? 0,
            CreditedSeconds = item.Runtime?.TotalSeconds ?? 0,
            Estimated = true,
        };

    private static ImportReport Preview(Harness h, string source, params ImportCandidate[] candidates)
    {
        var request = new ImportPreviewRequest(Install) { Source = source, UserId = Harness.Jon.Id, Candidates = candidates };
        h.Recorder.Apply(request);
        return Harness.Answer(request.Done.Task);
    }

    private static ImportReport Commit(Harness h, Guid importId)
    {
        var request = new ImportCommitRequest(Install, importId);
        h.Recorder.Apply(request);
        return Harness.Answer(request.Done.Task);
    }

    private static ImportReport Import(Harness h, string source, params ImportCandidate[] candidates) =>
        Commit(h, Preview(h, source, candidates).ImportId);

    private static Harness Started()
    {
        var h = new Harness();
        // Recording began at install: that is the cutover.
        h.Store.InTransaction(() => h.Store.UpsertUser(Harness.Jon, Install));
        return h;
    }

    [Fact]
    public void APreviewChangesNothingAndSaysWhatACommitWillDo()
    {
        using var h = Started();
        var preview = Preview(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));

        Assert.Equal(1, preview.New);
        Assert.Empty(h.Viewings());

        var commit = Commit(h, preview.ImportId);
        Assert.Equal(preview.New, commit.New);
        Assert.Single(h.Viewings());
    }

    [Fact]
    public void ImportingTwiceAddsNothing()
    {
        using var h = Started();
        Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));
        var again = Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));

        Assert.Equal(1, again.AlreadyImported);
        Assert.Single(h.Viewings());
    }

    [Fact]
    public void AnythingAfterRecordingBeganIsLeftToLiveRecording()
    {
        using var h = Started();
        var report = Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Install.AddDays(1), "later"));

        Assert.Equal(1, report.AfterCutover);
        Assert.Empty(h.Viewings());
    }

    [Fact]
    public void TheSameViewingFromABetterSourceReplacesJellyfinsEstimate()
    {
        using var h = Started();
        var film = Harness.Film();
        Import(h, ImportSources.Jellyfin, Watched(film, Harness.Tuesday, "jf"));
        var letterboxd = Import(h, ImportSources.Letterboxd, Watched(film, Harness.Tuesday.AddHours(6), "lb"));

        Assert.Equal(1, letterboxd.Replaces);
        var counted = new HistoryQueries(h.Database).History(Harness.Jon.Id, null, null, null, 0, 10);
        Assert.Equal("letterboxd", Assert.Single(counted.Items).Source);
        Assert.Equal(2, h.Viewings().Count); // the other is kept, superseded
    }

    [Fact]
    public void LiveRecordingAlwaysWins()
    {
        using var h = new Harness();
        var film = Harness.Film();
        h.Start(film, Harness.Tuesday, 0);
        h.Stop(film, h.Play(film, Harness.Tuesday, 0, 7000, step: 60), 7000);
        h.Store.InTransaction(() => h.Store.UpsertUser(Harness.Jon, Install)); // the cutover stays as first seen

        var request = new ImportPreviewRequest(Install)
        {
            Source = ImportSources.Letterboxd,
            UserId = Harness.Jon.Id,
            Candidates = [Watched(film, Harness.Tuesday.AddHours(2), "lb")],
        };
        h.Recorder.Apply(request);
        // Dated after this person's recording began, so it never reaches the duplicate check at all.
        Assert.Equal(1, Harness.Answer(request.Done.Task).AfterCutover);
    }

    [Fact]
    public void UndoingAnImportPutsBackWhatItReplaced()
    {
        using var h = Started();
        var film = Harness.Film();
        Import(h, ImportSources.Jellyfin, Watched(film, Harness.Tuesday, "jf"));
        var letterboxd = Import(h, ImportSources.Letterboxd, Watched(film, Harness.Tuesday.AddHours(1), "lb"));

        var undo = new ImportUndoRequest(Install, letterboxd.ImportId);
        h.Recorder.Apply(undo);
        Assert.Equal(1, Harness.Answer(undo.Done.Task));

        var counted = new HistoryQueries(h.Database).History(Harness.Jon.Id, null, null, null, 0, 10);
        Assert.Equal("jellyfin", Assert.Single(counted.Items).Source);
        Assert.Contains(h.Viewings(), v => v.Status == "undone");
    }

    [Fact]
    public void AnImportUndoneCanBeMadeAgain()
    {
        using var h = Started();
        var report = Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));
        var undo = new ImportUndoRequest(Install, report.ImportId);
        h.Recorder.Apply(undo);
        _ = Harness.Answer(undo.Done.Task);

        var again = Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));
        Assert.Equal(1, again.New);
        Assert.Equal("completed", Assert.Single(h.Viewings()).Status);
    }

    [Fact]
    public void CommittingTwiceIsRefused()
    {
        using var h = Started();
        var report = Import(h, ImportSources.Letterboxd, Watched(Harness.Film(), Harness.Tuesday, "a"));
        var again = new ImportCommitRequest(Install, report.ImportId);
        Assert.Throws<ImportException>(() => h.Recorder.Apply(again));
    }

    [Fact]
    public void MediaNotInThisLibraryIsKeptAndSaysSo()
    {
        using var h = Started();
        var elsewhere = Unmatched.Film("A Film Never Here", 1987, new Dictionary<string, string> { ["Tmdb"] = "999" });
        var report = Import(h, ImportSources.Letterboxd, Watched(elsewhere, Harness.Tuesday, "x", matched: false));

        Assert.Equal(1, report.Unmatched);
        var entry = Assert.Single(new HistoryQueries(h.Database).History(Harness.Jon.Id, null, null, null, 0, 10).Items);
        Assert.False(entry.Item.InLibrary);
        Assert.Equal("Import", entry.Item.Origin);
    }

    [Fact]
    public void TheBaselineIsCountedInTheSummary()
    {
        using var h = Started();
        Import(h, ImportSources.Jellyfin, Watched(Harness.Film(minutes: 100), Harness.Tuesday, "jf"));

        var summary = new HistoryQueries(h.Database).Summary(Harness.Jon.Id, TimeZoneInfo.Utc, 5, new NoLibrary(), Install);
        Assert.Equal(1, summary.Films.Watched);
        Assert.Equal(6000, summary.Films.CreditedSeconds, 1);
    }

    [Fact]
    public void ALiveRewatchAfterTheBaselineIsASecondViewing()
    {
        using var h = Started();
        var film = Harness.Film(minutes: 100);
        Import(h, ImportSources.Jellyfin, Watched(film, Harness.Tuesday, "jf"));

        var t = Install.AddDays(1);
        h.Start(film, t, 0);
        h.Stop(film, h.Play(film, t, 0, 6000, step: 60), 6000);

        Assert.Equal(2, new HistoryQueries(h.Database).Summary(Harness.Jon.Id, TimeZoneInfo.Utc, 5, new NoLibrary(), t.AddHours(3)).Films.Viewings);
    }

    private static ImportCandidate InProgress(MediaSnapshot item, DateTimeOffset when, double seconds, string key) =>
        new()
        {
            UserId = Harness.Jon.Id,
            UserName = Harness.Jon.Name,
            Item = item,
            Matched = true,
            SourceKey = key,
            StartedAt = when,
            LastActivityAt = when,
            FurthestTicks = Harness.Ticks(seconds),
            ActualSeconds = seconds,
            WallSeconds = seconds,
            CreditedSeconds = seconds,
            Estimated = true,
        };

    [Fact]
    public void AResumeAfterTheBaselineCarriesItsViewingOn()
    {
        using var h = Started();
        var episode = Harness.Episode(minutes: 25);
        Import(h, ImportSources.Jellyfin, InProgress(episode, Install.AddMinutes(-5), 365, "jf"));

        h.Start(episode, Install.AddMinutes(1), 365);
        h.Stop(episode, h.Play(episode, Install.AddMinutes(1), 365, 1500), 1500);

        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(1500, viewing.Credited, 1);
    }

    [Fact]
    public void APlayResumedJustBeforeTheBaselineCarriesItsViewingOn()
    {
        // JRDisk, 2026-11-06: the app resumed an episode as the server came back up, half a second before the
        // baseline. The play was too short to be a viewing yet, so it had nothing to join and nothing to be matched.
        using var h = new Harness();
        var episode = Harness.Episode(minutes: 25);
        h.Start(episode, Install, 365); // recording begins: the cutover
        h.Progress(episode, Install.Seconds(2), 367);
        Import(h, ImportSources.Jellyfin, InProgress(episode, Install.AddMinutes(-5), 365, "jf"));

        var paused = h.Play(episode, Install.Seconds(2), 367, 900);
        var underWay = Assert.Single(h.Viewings());
        Assert.Equal("in-progress", underWay.Status);
        Assert.Equal(900, underWay.Credited, 1); // 365 before the restart, 535 since

        h.Stop(episode, h.Play(episode, paused, 900, 1500), 1500);
        var viewing = Assert.Single(h.Viewings());
        Assert.Equal("completed", viewing.Status);
        Assert.Equal(1500, viewing.Credited, 1); // its running time, once
        Assert.Equal(viewing.Id, Assert.Single(h.Sessions()).ViewingId);
    }

    private sealed class NoLibrary : ILibraryFacts
    {
        public int? EpisodesInSeries(Guid seriesId) => null;

        public Guid? LastFileOfBook(Guid albumId) => null;
    }
}

public class FileSourceTests
{
    private static readonly CountingRules Rules = new();
    private static readonly LibraryIndex Library = new([Harness.Film(minutes: 136), Harness.Episode(minutes: 44)]);

    [Fact]
    public void ALetterboxdDiaryMatchesByTitleAndYear()
    {
        const string csv = """
            Date,Name,Year,Letterboxd URI,Rating,Rewatch,Tags,Watched Date
            2024-03-02,The Matrix,1999,https://boxd.it/abc,5,,,2024-03-01
            2024-03-09,"Paris, Texas",1984,https://boxd.it/def,4.5,Yes,,2024-03-08
            """;
        var rows = LetterboxdSource.Read(new StringReader(csv), Harness.Jon.Id, "jon", Library, Rules);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Matched);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), rows[0].CompletedAt);
        Assert.Equal(136 * 60, rows[0].CreditedSeconds, 1);
        Assert.False(rows[1].Matched);
        Assert.Equal("Paris, Texas", rows[1].Item.Name);
    }

    [Fact]
    public void ATraktHistoryExportMatchesByProviderIds()
    {
        const string json = """
            [
              { "id": 1001, "watched_at": "2023-05-01T20:00:00.000Z", "action": "watch", "type": "movie",
                "movie": { "title": "The Matrix", "year": 1999, "ids": { "trakt": 481, "imdb": "tt0133093", "tmdb": 603 } } },
              { "id": 1002, "watched_at": "2023-05-02T21:00:00.000Z", "action": "scrobble", "type": "episode",
                "episode": { "season": 3, "number": 7, "title": "Episode", "ids": { "trakt": 9 } },
                "show": { "title": "Deep Space Nine", "year": 1993, "ids": { "tvdb": 73244, "tmdb": 580 } } },
              { "id": 1003, "watched_at": "2023-05-03T21:00:00.000Z", "type": "movie",
                "movie": { "title": "Not Here", "year": 2001, "ids": { "tmdb": 1 } } }
            ]
            """;
        var rows = TraktExportSource.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), Harness.Jon.Id, "jon", Library, Rules);

        Assert.Equal([true, true, false], rows.Select(r => r.Matched));
        Assert.Equal("h:1001", rows[0].SourceKey);
        Assert.Equal(Harness.Episode().ItemId, rows[1].Item.ItemId);
    }

    [Fact]
    public void ATraktWatchedExportGivesTheLastWatchAndKeepsThePlayCount()
    {
        const string json = """
            [ { "plays": 3, "last_watched_at": "2022-01-01T10:00:00.000Z",
                "movie": { "title": "The Matrix", "year": 1999, "ids": { "tmdb": 603 } } } ]
            """;
        var row = Assert.Single(TraktExportSource.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), Harness.Jon.Id, "jon", Library, Rules));
        Assert.Equal(3, row.PlayCountNoted);
    }

    [Fact]
    public void TheDocumentedCsvTakesEpisodesAndSeconds()
    {
        const string csv = """
            date,type,title,series,season,episode,tvdb,seconds,completed
            2021-06-01,episode,Episode,Deep Space Nine,3,7,73244,1200,no
            2021-06-02T20:00:00Z,movie,The Matrix,,,,,,
            """;
        var rows = CsvSource.Read(new StringReader(csv), Harness.Jon.Id, "jon", Library, Rules);

        Assert.True(rows[0].Matched);
        Assert.False(rows[0].Completed);
        Assert.Equal(1200, rows[0].CreditedSeconds, 1);
        Assert.True(rows[1].Matched);
        Assert.True(rows[1].Completed);
    }

    [Fact]
    public void QuotedCsvFieldsKeepTheirCommasAndQuotes()
    {
        var rows = Csv.Read(new StringReader("a,\"b, c\",\"say \"\"hi\"\"\"\r\n1,2,3\n"));
        Assert.Equal(["a", "b, c", "say \"hi\""], rows[0]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void TitlesMatchWhateverTheirPunctuation()
    {
        Assert.Equal(LibraryIndex.Normalise("The Matrix: Reloaded!"), LibraryIndex.Normalise("matrix reloaded"));
        Assert.Equal(LibraryIndex.Normalise("Tom & Jerry"), LibraryIndex.Normalise("tom and jerry"));
    }
}

/// <summary>Design §8.3, against a database built to Playback Reporting's real schema (v16).</summary>
public class PlaybackReportingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "jmc-pr-" + Guid.NewGuid().ToString("N") + ".db");
    private static readonly Guid User = Harness.Jon.Id;

    public PlaybackReportingTests()
    {
        using var c = new SqliteConnection("Data Source=" + _path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            create table if not exists PlaybackActivity (DateCreated DATETIME NOT NULL, UserId TEXT, ItemId TEXT, ItemType TEXT,
                ItemName TEXT, PlaybackMethod TEXT, ClientName TEXT, DeviceName TEXT, PlayDuration INT);
            create table if not exists UserList (UserId TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void Row(string localDate, Guid item, string type, string name, int seconds)
    {
        using var c = new SqliteConnection("Data Source=" + _path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "insert into PlaybackActivity values ($d, $u, $i, $t, $n, 'DirectPlay', 'Jellyfin Web', 'Chrome', $s);";
        cmd.Parameters.AddWithValue("$d", localDate);
        cmd.Parameters.AddWithValue("$u", User.ToString("N"));
        cmd.Parameters.AddWithValue("$i", item.ToString("N"));
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void TwoEveningsOfOneEpisodeAreOneWatchedViewing()
    {
        var episode = Harness.Episode(minutes: 44);
        Row("2024-02-06 21:00:00.1234567", episode.ItemId, "Episode", "Deep Space Nine - s03e07 - Episode", 1500);
        Row("2024-02-07 20:30:00", episode.ItemId, "Episode", "Deep Space Nine - s03e07 - Episode", 1000);

        var rows = PlaybackReportingSource.Read(_path, TimeZoneInfo.Utc);
        var viewing = Assert.Single(PlaybackReportingSource.Assemble(rows, id => id == episode.ItemId ? episode : null, _ => "jon", new CountingRules()));

        Assert.True(viewing.Completed);
        Assert.Equal(2500, viewing.ActualSeconds);
        Assert.Equal(44 * 60, viewing.CreditedSeconds, 1);
        Assert.Equal(2, viewing.Plays.Count);
    }

    [Fact]
    public void LocalTimesAreReadInTheServersZone()
    {
        Row("2024-07-01 21:00:00", Guid.NewGuid(), "Movie", "A Film", 600);
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var row = Assert.Single(PlaybackReportingSource.Read(_path, london));
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 20, 0, 0, TimeSpan.Zero), row.StartedAt);
    }

    [Fact]
    public void ADeletedEpisodeIsRebuiltFromItsName()
    {
        var gone = Guid.NewGuid();
        Row("2023-01-01 20:00:00", gone, "Episode", "Babylon 5 - s02e10 - Gropos", 2600);
        var viewing = Assert.Single(PlaybackReportingSource.Assemble(PlaybackReportingSource.Read(_path, TimeZoneInfo.Utc),
            _ => null, _ => "jon", new CountingRules()));

        Assert.False(viewing.Matched);
        Assert.Equal("Babylon 5", viewing.Item.SeriesName);
        Assert.Equal(2, viewing.Item.SeasonNumber);
        Assert.Equal(10, viewing.Item.EpisodeNumber);
        Assert.Equal(gone, viewing.Item.ItemId);
    }

    [Fact]
    public void ImportedPlaysCountTowardsStreaks()
    {
        using var h = new Harness();
        h.Store.InTransaction(() => h.Store.UpsertUser(Harness.Jon, Harness.Tuesday.AddDays(400)));
        var film = Harness.Film(minutes: 100);
        Row("2026-10-25 20:00:00", film.ItemId, "Movie", "The Matrix", 6000);
        Row("2026-10-26 20:00:00", film.ItemId, "Movie", "The Matrix", 3000);
        var candidates = PlaybackReportingSource.Assemble(PlaybackReportingSource.Read(_path, TimeZoneInfo.Utc), _ => film, _ => "jon", new CountingRules());

        var preview = new ImportPreviewRequest(Harness.Tuesday.AddDays(400)) { Source = ImportSources.PlaybackReporting, Candidates = candidates };
        h.Recorder.Apply(preview);
        var commit = new ImportCommitRequest(Harness.Tuesday.AddDays(400), Harness.Answer(preview.Done.Task).ImportId);
        h.Recorder.Apply(commit);

        Assert.Equal(2, Harness.Answer(commit.Done.Task).New);
        var summary = new HistoryQueries(h.Database).Summary(Harness.Jon.Id, TimeZoneInfo.Utc, 5, new NoFacts(), new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, summary.Streaks.CurrentDays);
    }

    private sealed class NoFacts : ILibraryFacts
    {
        public int? EpisodesInSeries(Guid seriesId) => null;

        public Guid? LastFileOfBook(Guid albumId) => null;
    }
}

public class UpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jmc-up-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ASchemaOneDatabaseUpgradesWithNothingLost()
    {
        // Build what release 0.1.0 wrote: schema 1, with a viewing in it.
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, HistoryDatabase.FileName);
        using (var c = new SqliteConnection("Data Source=" + file))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = HistoryDatabase.Migrations()[0].Sql + """

                PRAGMA user_version = 1;
                INSERT INTO meta VALUES ('db_id', 'old'), ('created_at', '0'), ('change_seq', '7');
                INSERT INTO users (user_id, name, first_seen_at, recording_since) VALUES ('u', 'jon', 0, 0);
                INSERT INTO media (item_id, kind, name, first_seen_at, updated_at) VALUES ('i', 'Movie', 'A Film', 0, 0);
                INSERT INTO viewings (viewing_id, user_id, item_id, kind, source, status, started_at, last_activity_at, updated_seq)
                    VALUES ('v', 'u', 'i', 'played', 'live', 'completed', 0, 0, 7);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var db = new HistoryDatabase(_directory, NullLogger.Instance);
        db.Initialise();

        Assert.Equal("old", db.DatabaseId);
        Assert.True(File.Exists(Path.Combine(db.BackupDirectory, $"history-before-v{HistoryDatabase.SchemaVersion}.db")));
        using var check = db.Open(readOnly: true);
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM viewings WHERE viewing_id = 'v' AND status = 'completed'; ";
        Assert.Equal(1L, count.ExecuteScalar());
        count.CommandText = "SELECT origin FROM media WHERE item_id = 'i';";
        Assert.Equal(DBNull.Value, count.ExecuteScalar());
    }
}
