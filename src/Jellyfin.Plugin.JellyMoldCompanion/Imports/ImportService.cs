using System.Text;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using JellyMoldCompanion.Import;
using JellyMoldCompanion.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Imports;

/// <summary>
/// Builds an import's candidates from its source (design §8) and hands them to the recorder's queue to preview,
/// commit or undo — and, on its own, takes each person's install-time baseline from Jellyfin's watch state (§8.2):
/// once, a little after the server starts, and for anyone added later.
/// </summary>
public sealed class ImportService : IHostedService, IDisposable
{
    /// <summary>The most a file import may be — a decade of daily Letterboxd entries is well under a megabyte.</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    private readonly CompanionService _companion;
    private readonly ILogger<ImportService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _baselines;

    public ImportService(CompanionService companion, ILogger<ImportService> logger)
    {
        _companion = companion;
        _logger = logger;
    }

    private JellyfinHost Host => _companion.Host;

    private CountingRules Rules => Host.Rules(CompanionService.Settings);

    /// <summary>Builds the candidates for a source. <paramref name="file"/> is the uploaded file, for the file sources.</summary>
    public (IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<string> Notes) Candidates(string source, Guid? userId, Stream? file)
    {
        var person = userId is { } id ? Host.Person(id) : null;
        switch (source)
        {
            case ImportSources.Jellyfin:
                return (Host.WatchState(Who(person).Id, Rules), []);

            case ImportSources.PlaybackReporting:
            {
                var path = Host.PlaybackReportingDatabase
                           ?? throw new ImportException("Playback Reporting's database is not on this server.");
                var rows = PlaybackReportingSource.Read(path, TimeZoneInfo.Local);
                var notes = new List<string>
                {
                    $"{rows.Count} plays read from Playback Reporting, in the server's time zone ({TimeZoneInfo.Local.Id}).",
                    "Playback Reporting kept only the first person on a shared play, and never plays under about 20 seconds.",
                };
                return (PlaybackReportingSource.Assemble(rows, Host.SnapshotById, id => Host.Person(id)?.Name, Rules), notes);
            }

            case ImportSources.TraktExport:
                return (TraktExportSource.Read(Upload(file), Who(person).Id, person!.Name, Host.BuildLibraryIndex(), Rules), []);

            case ImportSources.Letterboxd:
                return (LetterboxdSource.Read(Reader(Upload(file)), Who(person).Id, person!.Name, Host.BuildLibraryIndex(), Rules),
                    ["Letterboxd gives no provider ids, so films are matched by title and year."]);

            case ImportSources.Csv:
                return (CsvSource.Read(Reader(Upload(file)), Who(person).Id, person!.Name, Host.BuildLibraryIndex(), Rules), []);

            default:
                throw new ImportException($"There is no import source called '{source}'.");
        }

        static RecordedUser Who(RecordedUser? person) => person ?? throw new ImportException("No such person on this server.");
        static Stream Upload(Stream? file) => file ?? throw new ImportException("This import needs a file.");
        static TextReader Reader(Stream file) => new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    public async Task<ImportReport> Preview(string source, Guid? userId, Stream? file)
    {
        var (candidates, notes) = Candidates(source, userId, file);
        return await _companion.Recording.Preview(new ImportPreviewRequest(DateTimeOffset.UtcNow)
        {
            Source = source,
            UserId = ImportSources.IsServerWide(source) ? null : userId,
            UserName = userId is { } id && !ImportSources.IsServerWide(source) ? Host.Person(id)?.Name : null,
            Candidates = candidates,
            Notes = notes,
        }).ConfigureAwait(false);
    }

    public Task<ImportReport> Commit(Guid importId) =>
        _companion.Recording.Commit(new ImportCommitRequest(DateTimeOffset.UtcNow, importId));

    public Task<int> Undo(Guid importId) =>
        _companion.Recording.Undo(new ImportUndoRequest(DateTimeOffset.UtcNow, importId));

    // — The baseline, on its own (§8.2) —

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _baselines = Task.Run(() => TakeBaselinesAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_baselines is not null)
        {
            await Task.WhenAny(_baselines, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task TakeBaselinesAsync(CancellationToken stopping)
    {
        try
        {
            // Let the server finish starting — and its library settle — first.
            await Task.Delay(TimeSpan.FromSeconds(30), stopping).ConfigureAwait(false);
            while (!stopping.IsCancellationRequested)
            {
                await TakeMissingBaselines(stopping).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromHours(6), stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>For each recorded person without a baseline, takes one. Each person once; nothing to import is still a baseline taken.</summary>
    public async Task TakeMissingBaselines(CancellationToken stopping)
    {
        if (!_companion.Recording.IsRecording)
        {
            return;
        }

        var scope = JellyfinHost.Scope(CompanionService.Settings);
        foreach (var person in Host.Everyone().Where(p => scope.Records(p.Id)))
        {
            if (stopping.IsCancellationRequested || _companion.Queries.HasCommittedImport(ImportSources.Jellyfin, person.Id))
            {
                continue;
            }

            try
            {
                var preview = await Preview(ImportSources.Jellyfin, person.Id, null).ConfigureAwait(false);
                var report = await Commit(preview.ImportId).ConfigureAwait(false);
                _logger.LogInformation(
                    "JellyMold Companion took {Name}'s baseline from Jellyfin's watch state: {New} added, {Unchanged} already recorded",
                    person.Name, report.New, report.AlreadyImported + report.Duplicates + report.AfterCutover);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "JellyMold Companion could not take {Name}'s baseline", person.Name);
            }
        }
    }
}
