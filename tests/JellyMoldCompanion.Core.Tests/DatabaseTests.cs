using System.Text.RegularExpressions;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §10: migrations only add, backups, and the newer-schema guard.</summary>
public partial class DatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jmc-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void NoMigrationEverDestroysAnything()
    {
        foreach (var (number, sql) in HistoryDatabase.Migrations())
        {
            var code = SqlComment().Replace(sql, string.Empty);
            Assert.False(Destructive().IsMatch(code), $"Migration {number} contains a destructive statement.");
        }
    }

    [Fact]
    public void InitialisingTwiceChangesNothing()
    {
        var first = new HistoryDatabase(_directory, NullLogger.Instance);
        first.Initialise();
        var second = new HistoryDatabase(_directory, NullLogger.Instance);
        second.Initialise();

        Assert.Equal(first.DatabaseId, second.DatabaseId);
        Assert.False(second.IsReadOnly);
        Assert.Null(second.Problem);
    }

    [Fact]
    public void ANewerSchemaIsOpenedReadOnlyAndNeverRepaired()
    {
        var db = new HistoryDatabase(_directory, NullLogger.Instance);
        db.Initialise();
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {HistoryDatabase.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        var downgraded = new HistoryDatabase(_directory, NullLogger.Instance);
        downgraded.Initialise();

        Assert.True(downgraded.IsReadOnly);
        Assert.NotNull(downgraded.Problem);
    }

    [Fact]
    public void ADamagedFileIsSetAsideAndTheLatestBackupRestored()
    {
        var db = new HistoryDatabase(_directory, NullLogger.Instance);
        db.Initialise();
        db.Backup();
        SqliteConnection.ClearAllPools();
        File.WriteAllText(db.FilePath, "this is not a database");

        var reopened = new HistoryDatabase(_directory, NullLogger.Instance);
        reopened.Initialise();

        Assert.NotNull(reopened.Problem);
        Assert.Contains(Directory.EnumerateFiles(_directory), f => f.Contains(".damaged-", StringComparison.Ordinal));
        Assert.Equal(db.DatabaseId, reopened.DatabaseId);
    }

    [Fact]
    public void BackupsArePrunedToSevenDailyAndFourWeekly()
    {
        var db = new HistoryDatabase(_directory, NullLogger.Instance);
        db.Initialise();
        Directory.CreateDirectory(db.BackupDirectory);
        var day = new DateTime(2026, 6, 1);
        for (var i = 0; i < 60; i++)
        {
            File.WriteAllText(Path.Combine(db.BackupDirectory, $"history-{day.AddDays(i):yyyy-MM-dd}.db"), "x");
        }

        File.WriteAllText(Path.Combine(db.BackupDirectory, "history-before-v2.db"), "kept for good");
        db.PruneBackups(daily: 7, weekly: 4);

        Assert.Equal(11, db.ListBackups().Count);
        Assert.True(File.Exists(Path.Combine(db.BackupDirectory, "history-before-v2.db")));
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex SqlComment();

    [GeneratedRegex(@"\bDROP\s+(TABLE|COLUMN)\b|\bRENAME\s+TO\b", RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();
}
