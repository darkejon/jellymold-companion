using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JellyMoldCompanion.Storage;

/// <summary>
/// The history file: where it is, opening it, bringing its schema up to date, and copies of it (design §10).
///
/// One file, <c>history.db</c>, under Jellyfin's data path — not the plugin's own folder, which is replaced on
/// update — with backups beside it in <c>backups/</c>. WAL mode, so the API reads while the recorder writes.
/// </summary>
public sealed partial class HistoryDatabase
{
    /// <summary>The newest schema this build understands. One migration script per version.</summary>
    public const int SchemaVersion = 2;

    public const string FileName = "history.db";

    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public HistoryDatabase(string directory, ILogger logger, TimeProvider? time = null)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
        BackupDirectory = Path.Combine(directory, "backups");
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string BackupDirectory { get; }

    /// <summary>
    /// True when the file was written by a newer plugin than this one (somebody downgraded). It is opened for
    /// reading only: nothing is recorded, and it is never "repaired" (§10.2).
    /// </summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Something the admin should be told on the dashboard, in plain words. Null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>This database's identity (<c>meta.db_id</c>). Regenerated on a restore, so change-feed tokens from before it are refused (§9.4).</summary>
    public string DatabaseId { get; private set; } = string.Empty;

    private string ConnectionString(bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    /// <summary>Opens a connection with the pragmas every connection needs.</summary>
    public SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(ConnectionString(readOnly || IsReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Run once at startup: the integrity check (§10.4), then migrations (§10.2), then the database's identity.
    /// </summary>
    public void Initialise()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(BackupDirectory);

        var existed = File.Exists(FilePath);
        if (existed && !PassesIntegrityCheck())
        {
            RecoverFromDamage();
        }

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            IsReadOnly = true;
            Problem = $"The history database was written by a newer version of JellyMold Companion (schema {version}; " +
                      $"this version understands {SchemaVersion}). It is open for reading only and nothing new is being " +
                      "recorded. Install the newer version again to resume recording.";
            _logger.LogWarning("{Problem}", Problem);
            ReadIdentity(connection);
            return;
        }

        if (version < SchemaVersion)
        {
            if (existed && version > 0)
            {
                // Kept indefinitely: the copy that lets a bad migration be walked back.
                Backup($"history-before-v{SchemaVersion}");
            }

            foreach (var (number, sql) in Migrations().Where(m => m.Number > version))
            {
                using var transaction = connection.BeginTransaction();
                Execute(connection, sql, transaction);
                Execute(connection, $"PRAGMA user_version = {number};", transaction);
                transaction.Commit();
                _logger.LogInformation("History database migrated to schema {Version}", number);
            }
        }

        EnsureIdentity(connection);
    }

    private bool PassesIntegrityCheck()
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString(readOnly: true));
            connection.Open();
            var result = Scalar(connection, "PRAGMA quick_check;") as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException e)
        {
            _logger.LogError(e, "The history database could not be opened for its integrity check");
            return false;
        }
    }

    /// <summary>
    /// §10.4: keep the damaged file for salvage, restore the latest good backup (losing at most a day), carry on
    /// recording, and say so. Never a silent fresh start.
    /// </summary>
    private void RecoverFromDamage()
    {
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var damaged = FilePath + ".damaged-" + stamp;
        MoveWithSidecars(FilePath, damaged);

        var latest = ListBackups().FirstOrDefault();
        if (latest is not null)
        {
            File.Copy(latest.Path, FilePath);
            Problem = $"The history database failed its integrity check on {stamp} UTC. It was set aside as " +
                      $"'{Path.GetFileName(damaged)}' and the backup from {latest.TakenAt:yyyy-MM-dd} was restored; " +
                      "anything recorded after that backup may be in the damaged file.";
        }
        else
        {
            Problem = $"The history database failed its integrity check on {stamp} UTC and there was no backup to " +
                      $"restore. It was set aside as '{Path.GetFileName(damaged)}' and a new, empty database started.";
        }

        _logger.LogError("{Problem}", Problem);
    }

    private static void MoveWithSidecars(string from, string to)
    {
        File.Move(from, to);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(from + suffix))
            {
                File.Move(from + suffix, to + suffix);
            }
        }
    }

    private void EnsureIdentity(SqliteConnection connection)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        Execute(connection,
            "INSERT OR IGNORE INTO meta (key, value) VALUES ('db_id', $id), ('created_at', $now), ('change_seq', '0');",
            parameters: [("$id", Guid.NewGuid().ToString("N")), ("$now", now)]);
        ReadIdentity(connection);
    }

    private void ReadIdentity(SqliteConnection connection)
    {
        DatabaseId = Scalar(connection, "SELECT value FROM meta WHERE key = 'db_id';") as string ?? string.Empty;
    }

    // — Backups (§10.3) —

    public sealed record BackupInfo(string Path, DateTimeOffset TakenAt, long Bytes);

    /// <summary>
    /// A consistent copy, safe while the database is in use (SQLite's own <c>VACUUM INTO</c>). Returns its path.
    /// </summary>
    public string Backup(string? name = null)
    {
        var stamp = _time.GetUtcNow();
        name ??= "history-" + stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(BackupDirectory, name + ".db");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection(ConnectionString(readOnly: false));
        connection.Open();
        Execute(connection, "VACUUM INTO $path;", parameters: [("$path", path)]);
        _logger.LogInformation("History database backed up to {Path}", path);
        return path;
    }

    /// <summary>Daily backups, newest first. The ones taken before a migration are not in this list and are never pruned.</summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!System.IO.Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(BackupDirectory, "history-*.db")
            .Select(path => (path, match: DailyName().Match(Path.GetFileName(path))))
            .Where(x => x.match.Success)
            .Select(x => new BackupInfo(
                x.path,
                DateTimeOffset.ParseExact(x.match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal),
                new FileInfo(x.path).Length))
            .OrderByDescending(b => b.TakenAt)
            .ToList();
    }

    /// <summary>Keeps the newest <paramref name="daily"/>, plus one a week for <paramref name="weekly"/> weeks beyond them.</summary>
    public void PruneBackups(int daily = 7, int weekly = 4)
    {
        var backups = ListBackups();
        var keep = new HashSet<string>(backups.Take(daily).Select(b => b.Path));
        foreach (var week in backups.Skip(daily)
                     .GroupBy(b => ISOWeek.GetYear(b.TakenAt.UtcDateTime) * 100 + ISOWeek.GetWeekOfYear(b.TakenAt.UtcDateTime))
                     .Take(weekly))
        {
            keep.Add(week.First().Path);
        }

        foreach (var backup in backups.Where(b => !keep.Contains(b.Path)))
        {
            File.Delete(backup.Path);
        }
    }

    [GeneratedRegex(@"^history-(\d{4}-\d{2}-\d{2})\.db$")]
    private static partial Regex DailyName();

    // — Migrations —

    /// <summary>The embedded scripts, in order: <c>Storage/Migrations/0001_initial.sql</c> and on.</summary>
    internal static IReadOnlyList<(int Number, string Sql)> Migrations()
    {
        var assembly = typeof(HistoryDatabase).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Select(name => (name, match: MigrationName().Match(name)))
            .Where(x => x.match.Success)
            .Select(x => (Number: int.Parse(x.match.Groups[1].Value, CultureInfo.InvariantCulture), Sql: Read(assembly, x.name)))
            .OrderBy(x => x.Number)
            .ToList();

        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Number != i + 1)
            {
                throw new InvalidOperationException($"Migration scripts must be numbered 1, 2, 3… without gaps; found {scripts[i].Number} at position {i + 1}.");
            }
        }

        if (scripts.Count != SchemaVersion)
        {
            throw new InvalidOperationException($"SchemaVersion is {SchemaVersion} but there are {scripts.Count} migration scripts.");
        }

        return scripts;
    }

    [GeneratedRegex(@"\.Migrations\.(\d{4})_[A-Za-z0-9_]+\.sql$")]
    private static partial Regex MigrationName();

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // — Small helpers shared with the store —

    internal static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null,
        IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters ?? [])
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
