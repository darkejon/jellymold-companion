using System.Text.Json;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using JellyMoldCompanion.Storage;

namespace JellyMoldCompanion.Import;

/// <summary>
/// Applies imports (design §8.1), on the recorder's thread like every other write.
///
/// <list type="bullet">
/// <item><b>Preview, then commit.</b> A preview runs exactly what a commit would, inside a transaction that is then
/// rolled back — so the counts it shows are the counts a commit produces — and keeps the candidates with the import.</item>
/// <item><b>Idempotent.</b> A source key already imported adds nothing.</item>
/// <item><b>Before the cutover only.</b> Anything dated after recording began for that person is live recording's.</item>
/// <item><b>Never double-counted.</b> The same viewing from two sources — within 12 hours, 24 when one side is Jellyfin's
/// estimated date — is one viewing: the better source counts and the other is kept as superseded.</item>
/// <item><b>Undo.</b> Its viewings become <c>undone</c> (kept, not counted) and anything they had superseded counts again.
/// Undoing an import is not deleting history.</item>
/// </list>
/// </summary>
public sealed class Importer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const int SampleSize = 25;

    private readonly HistoryStore _store;

    public Importer(HistoryStore store) => _store = store;

    public ImportReport Preview(string source, Guid? userId, string? userName, IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<string> notes, DateTimeOffset now)
    {
        var importId = Guid.NewGuid();
        ImportReport report = null!;
        _store.InTransaction(() => report = Apply(importId, source, userId, candidates, now), commit: false);
        report.Notes.AddRange(notes);
        _store.InTransaction(() =>
        {
            if (userId is { } id && userName is not null)
            {
                _store.UpsertUser(new RecordedUser(id, userName), now);
            }

            _store.SaveImport(importId, source, userId, now, JsonSerializer.Serialize(candidates, Json), JsonSerializer.Serialize(report, Json));
        });
        return report;
    }

    public ImportReport Commit(Guid importId, DateTimeOffset now)
    {
        var import = _store.GetImport(importId) ?? throw new ImportException("There is no such import.");
        if (import.CommittedAt is not null)
        {
            throw new ImportException("That import has already been committed.");
        }

        if (import.UndoneAt is not null)
        {
            throw new ImportException("That import was undone. Preview it again to import it once more.");
        }

        var candidates = JsonSerializer.Deserialize<List<ImportCandidate>>(import.Candidates ?? "[]", Json) ?? [];
        ImportReport report = null!;
        _store.InTransaction(() =>
        {
            report = Apply(importId, import.Source, import.UserId, candidates, now);
            var previewed = import.Detail is null ? null : JsonSerializer.Deserialize<ImportReport>(import.Detail, Json);
            report.Notes.AddRange(previewed?.Notes ?? []);
            _store.SetImportCommitted(importId, now, JsonSerializer.Serialize(report, Json));
            _store.InsertEvent(now, "import-committed", import.UserId, detail: new { import = importId, import.Source, report.New, report.Replaces });
        });
        return report;
    }

    public UndoResult Undo(Guid importId, DateTimeOffset now, CountingRules rules)
    {
        var import = _store.GetImport(importId) ?? throw new ImportException("There is no such import.");
        if (import.CommittedAt is null)
        {
            throw new ImportException("Only a committed import can be undone.");
        }

        if (import.UndoneAt is not null)
        {
            throw new ImportException("That import has already been undone.");
        }

        var undone = 0;
        var kept = new Dictionary<Guid, ViewingRecord>();
        _store.InTransaction(() =>
        {
            var viewings = _store.ViewingsOfImport(importId);
            foreach (var viewing in viewings.Where(v => v.Status != ViewingStatus.Undone))
            {
                // Anything played here since, carrying the imported viewing on, stays: the undo takes back what the
                // import said, never what was watched.
                var played = _store.SessionsOfViewing(viewing.ViewingId)
                    .Where(s => s.Source is Sources.Live or Sources.Client).ToList();
                var playedSince = played.Count > 0 ? PlayedSince(viewing, played, rules) : null;

                viewing.Status = ViewingStatus.Undone;
                // Points at the viewing kept for what was played, so importing this again leaves it to that (see Add).
                viewing.SupersededBy = playedSince?.ViewingId ?? viewing.SupersededBy;
                _store.SaveViewing(viewing);
                _store.SetSessionsCounted(viewing.ViewingId, false);
                undone++;

                if (playedSince is not null)
                {
                    _store.SaveViewing(playedSince);
                    foreach (var session in played)
                    {
                        session.ViewingId = playedSince.ViewingId;
                        session.Counted = true;
                        _store.SaveSession(session);
                    }

                    _store.InsertEvent(now, "played-kept", viewing.UserId, viewing.ItemId,
                        detail: new { undone = viewing.ViewingId, kept = playedSince.ViewingId });
                    kept[viewing.ViewingId] = playedSince;
                }
            }

            // Whatever this import had replaced counts again — but not its own rows, undone just now with the rest.
            var replacedBy = viewings.Select(v => v.ViewingId).ToList();
            foreach (var replaced in _store.ViewingsSupersededBy(replacedBy).Where(r => r.Status != ViewingStatus.Undone))
            {
                replaced.SupersededBy = null;
                _store.SaveViewing(replaced);
                _store.SetSessionsCounted(replaced.ViewingId, true);
            }

            _store.SetImportUndone(importId, now);
            _store.InsertEvent(now, "import-undone", import.UserId,
                detail: new { import = importId, import.Source, undone, playedKept = kept.Count });
        });
        return new UndoResult(undone, kept);
    }

    /// <summary>
    /// What was played here of a viewing whose import is being undone, as a viewing of its own. It counts as finished
    /// only if it finished while being played here: credits watched after a film the import said was watched are the
    /// credits, not a second film.
    /// </summary>
    private static ViewingRecord PlayedSince(ViewingRecord viewing, IReadOnlyList<SessionRecord> played, CountingRules rules)
    {
        var first = played.Min(s => s.StartedAt);
        var actual = played.Sum(s => s.ActualSeconds);
        var furthest = played.Max(s => s.FurthestTicks);
        var finishedHere = viewing.IsCompleted && viewing.CompletedAt >= first &&
                           (viewing.CompletedBy == CompletedBy.Mark ||
                            Completion.IsComplete(viewing.MediaKind, viewing.RuntimeTicks, furthest, actual, rules));
        var completedBy = finishedHere ? viewing.CompletedBy : null;
        return new ViewingRecord
        {
            UserId = viewing.UserId,
            ItemId = viewing.ItemId,
            MediaKind = viewing.MediaKind,
            Kind = ViewingKind.Played,
            Source = played.Any(s => s.Source == Sources.Live) ? Sources.Live : Sources.Client,
            Status = finishedHere ? ViewingStatus.Completed
                : viewing.Status == ViewingStatus.Abandoned ? ViewingStatus.Abandoned
                : ViewingStatus.InProgress,
            StartedAt = first,
            LastActivityAt = played.Max(s => s.EndedAt ?? s.LastSeenAt),
            CompletedAt = finishedHere ? viewing.CompletedAt : null,
            CompletedBy = completedBy,
            FurthestTicks = furthest,
            RuntimeTicks = viewing.RuntimeTicks,
            ActualSeconds = actual,
            WallSeconds = played.Sum(s => s.WallSeconds),
            CreditedSeconds = Completion.Credited(viewing.MediaKind, viewing.RuntimeTicks, actual, completedBy, rules),
            Rule = Completion.RuleLabel(viewing.MediaKind, rules),
        };
    }

    private ImportReport Apply(Guid importId, string source, Guid? userId, IReadOnlyList<ImportCandidate> candidates, DateTimeOffset now)
    {
        var report = new ImportReport { ImportId = importId, Source = source, UserId = userId, Rows = candidates.Count };
        var knownUsers = new HashSet<Guid>();

        foreach (var c in candidates.OrderBy(c => c.When))
        {
            if (knownUsers.Add(c.UserId))
            {
                _store.UpsertUser(new RecordedUser(c.UserId, c.UserName), now);
            }

            string outcome;
            var cutover = _store.RecordingSince(c.UserId) ?? now;
            var existing = _store.ViewingBySourceKey(source, c.UserId, c.SourceKey);
            if (c.When >= cutover)
            {
                report.AfterCutover++;
                outcome = "After recording began";
            }
            else if (existing is not null && existing.Status != ViewingStatus.Undone)
            {
                report.AlreadyImported++;
                outcome = "Already imported";
            }
            else
            {
                outcome = Add(c, existing, importId, source, report);
            }

            if (report.Sample.Count < SampleSize)
            {
                report.Sample.Add(new ImportSample(Title(c.Item), c.When.UtcDateTime, outcome));
            }

            if (!c.Matched && report.UnmatchedTitles.Count < SampleSize && outcome is not ("Already imported" or "After recording began"))
            {
                report.UnmatchedTitles.Add(Title(c.Item));
            }

            if (c.Note is not null && !report.Notes.Contains(c.Note))
            {
                report.Notes.Add(c.Note);
            }
        }

        return report;
    }

    private string Add(ImportCandidate c, ViewingRecord? undone, Guid importId, string source, ImportReport report)
    {
        if (c.Matched)
        {
            _store.UpsertMedia(c.Item, c.LastActivityAt);
        }
        else
        {
            // Known only from the import: never written over a richer record the library gave.
            _store.InsertMediaIfMissing(c.Item, c.LastActivityAt, origin: "import");
            report.Unmatched++;
        }

        var keys = MediaKeys.For(c.Item);
        var viewing = new ViewingRecord
        {
            // An import undone and now made again carries on under its old id: one record of one viewing.
            ViewingId = undone?.ViewingId ?? Guid.NewGuid(),
            UserId = c.UserId,
            ItemId = c.Item.ItemId,
            MediaKind = c.Item.Kind,
            Kind = ViewingKind.Imported,
            Source = source,
            ImportId = importId,
            SourceKey = c.SourceKey,
            Status = c.Completed ? ViewingStatus.Completed : ViewingStatus.InProgress,
            StartedAt = c.StartedAt,
            LastActivityAt = c.LastActivityAt,
            CompletedAt = c.Completed ? c.CompletedAt ?? c.LastActivityAt : null,
            CompletedBy = c.Completed ? CompletedBy.Import : null,
            FurthestTicks = c.FurthestTicks,
            RuntimeTicks = c.Item.RuntimeTicks,
            ActualSeconds = c.ActualSeconds,
            WallSeconds = c.WallSeconds,
            CreditedSeconds = c.CreditedSeconds,
            Estimated = c.Estimated,
            PlayCountNoted = c.PlayCountNoted,
        };

        // The same viewing recorded already? Searched over the widest window, then held to the one for this pair of sources.
        // A record undone after it was played on is matched to the viewing kept for that play, however long after it
        // came: live recording wins.
        var near = undone?.SupersededBy is { } keptId && _store.GetViewing(keptId) is { SupersededBy: null } kept &&
                   kept.Status is ViewingStatus.InProgress or ViewingStatus.Completed or ViewingStatus.Abandoned
            ? kept
            : _store.NearbyViewing(c.UserId, c.Item.ItemId, keys, c.When, TimeSpan.FromHours(24), excluding: viewing.ViewingId);
        if (near is not null && near.ViewingId != undone?.SupersededBy &&
            ((near.CompletedAt ?? near.LastActivityAt) - c.When).Duration() > ImportSources.DuplicateWindow(source, near.Source))
        {
            near = null;
        }

        string outcome;
        if (near is null)
        {
            report.New++;
            outcome = c.Matched ? "New" : "New, not in this library";
        }
        else if (ImportSources.Precedence(near.Source) >= ImportSources.Precedence(source))
        {
            viewing.SupersededBy = near.ViewingId;
            report.Duplicates++;
            outcome = "Already recorded from a better source";
        }
        else
        {
            near.SupersededBy = viewing.ViewingId;
            _store.SaveViewing(near);
            _store.SetSessionsCounted(near.ViewingId, false);
            report.Replaces++;
            outcome = "Replaces a less exact record";
        }

        _store.SaveViewing(viewing);
        if (undone is not null)
        {
            _store.SetSessionsCounted(viewing.ViewingId, viewing.SupersededBy is null);
        }
        else
        {
            foreach (var play in c.Plays)
            {
                _store.SaveSession(new SessionRecord
                {
                    UserId = c.UserId,
                    ItemId = c.Item.ItemId,
                    Kind = c.Item.Kind,
                    ViewingId = viewing.ViewingId,
                    Source = source,
                    Client = play.Client,
                    DeviceName = play.DeviceName,
                    PlayMethod = play.PlayMethod,
                    StartedAt = play.StartedAt,
                    EndedAt = play.StartedAt.AddSeconds(play.Seconds),
                    LastSeenAt = play.StartedAt.AddSeconds(play.Seconds),
                    ReceivedAt = c.LastActivityAt,
                    StartTicks = 0,
                    LastTicks = 0,
                    FurthestTicks = 0,
                    RuntimeTicks = c.Item.RuntimeTicks,
                    ActualSeconds = play.Seconds,
                    WallSeconds = play.Seconds,
                    EndReason = "import",
                    Counted = viewing.SupersededBy is null,
                });
            }
        }

        return outcome;
    }

    private static string Title(MediaSnapshot item) => item.Kind switch
    {
        MediaKind.Episode => $"{item.SeriesName} S{item.SeasonNumber:00}E{item.EpisodeNumber:00} {item.Name}".Trim(),
        MediaKind.Audio when item.Artists.Count > 0 => $"{item.Artists[0]} – {item.Name}",
        _ => item.ProductionYear is { } year ? $"{item.Name} ({year})" : item.Name,
    };
}

/// <summary>An import that cannot be done as asked, with a reason a person can read.</summary>
public sealed class ImportException(string message) : Exception(message);
