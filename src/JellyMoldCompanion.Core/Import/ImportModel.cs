using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JellyMoldCompanion.Model;

namespace JellyMoldCompanion.Import;

/// <summary>The sources an import can come from (§8). Their names are what the database and the API use.</summary>
public static class ImportSources
{
    public const string Jellyfin = "jellyfin";
    public const string PlaybackReporting = "playback-reporting";
    public const string TraktExport = "trakt-export";
    public const string Letterboxd = "letterboxd";
    public const string Csv = "csv";

    public static readonly IReadOnlyList<string> All = [Jellyfin, PlaybackReporting, TraktExport, Letterboxd, Csv];

    /// <summary>
    /// When two sources describe the same viewing, the better one wins and the other is kept as superseded (§8.1):
    /// live › client › Playback Reporting › Trakt, Simkl › files › Jellyfin's own watch state.
    /// </summary>
    public static int Precedence(string source) => source switch
    {
        "live" => 6,
        "client" => 5,
        PlaybackReporting => 4,
        "trakt" or "simkl" => 3,
        TraktExport or Letterboxd or Csv => 2,
        Jellyfin => 1,
        _ => 0,
    };

    /// <summary>Server-wide sources hold everyone's rows, so only an admin may run them.</summary>
    public static bool IsServerWide(string source) => source == PlaybackReporting;

    /// <summary>Sources whose dates are estimates (Jellyfin's last-played date is the last *start*): a wider window to recognise the same viewing.</summary>
    public static TimeSpan DuplicateWindow(string a, string b) =>
        a == Jellyfin || b == Jellyfin ? TimeSpan.FromHours(24) : TimeSpan.FromHours(12);
}

/// <summary>One viewing an import proposes to add, before it is checked against what is already recorded.</summary>
public sealed record ImportCandidate
{
    public required Guid UserId { get; init; }
    public required string UserName { get; init; }
    public required MediaSnapshot Item { get; init; }

    /// <summary>False when the item is not in this library: it is kept anyway, with what the source knew (§8.1).</summary>
    public bool Matched { get; init; }

    /// <summary>The source's own id for the record, so importing twice adds nothing.</summary>
    public required string SourceKey { get; init; }

    public bool Completed { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long FurthestTicks { get; init; }
    public double ActualSeconds { get; init; }
    public double WallSeconds { get; init; }
    public double CreditedSeconds { get; init; }
    public bool Estimated { get; init; }
    public int? PlayCountNoted { get; init; }

    /// <summary>The plays it was assembled from, where the source has them (Playback Reporting).</summary>
    public IReadOnlyList<ImportedPlay> Plays { get; init; } = [];

    /// <summary>Something the person should know about this row — an ambiguous local time, say.</summary>
    public string? Note { get; init; }

    /// <summary>The moment that decides cutover and duplicates: when it was finished, or last touched.</summary>
    public DateTimeOffset When => CompletedAt ?? LastActivityAt;
}

/// <summary>
/// What an undo did: how many viewings it took back, and — by the viewing taken back — each viewing it made of what
/// was played here since, so a play still going can carry on in it.
/// </summary>
public sealed record UndoResult(int Undone, IReadOnlyDictionary<Guid, Storage.ViewingRecord> PlayedKept);

public sealed record ImportedPlay(DateTimeOffset StartedAt, double Seconds, string? Client, string? DeviceName, string? PlayMethod);

/// <summary>What an import did, or would do. Kept with the import.</summary>
public sealed record ImportReport
{
    public Guid ImportId { get; init; }
    public string Source { get; init; } = string.Empty;
    public Guid? UserId { get; init; }
    /// <summary>Viewings added.</summary>
    public int New { get; set; }
    /// <summary>Already imported before — importing twice adds nothing.</summary>
    public int AlreadyImported { get; set; }
    /// <summary>The same viewing is already recorded from a better source; kept, not counted.</summary>
    public int Duplicates { get; set; }
    /// <summary>This import is the better record of a viewing already there, which it replaces (the other is kept).</summary>
    public int Replaces { get; set; }
    /// <summary>Dated after recording began, which live recording already has.</summary>
    public int AfterCutover { get; set; }
    /// <summary>Not in this library: kept with the title and ids the source gave.</summary>
    public int Unmatched { get; set; }
    public int Rows { get; set; }
    public List<ImportSample> Sample { get; init; } = [];
    public List<string> UnmatchedTitles { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

public sealed record ImportSample(string Title, DateTime When, string Outcome);

/// <summary>Snapshots for media an import names that this library does not have (§8.1).</summary>
public static partial class Unmatched
{
    public static MediaSnapshot Film(string title, int? year, IReadOnlyDictionary<string, string> ids)
    {
        var snapshot = new MediaSnapshot { ItemId = Guid.Empty, Kind = MediaKind.Movie, Name = title, ProductionYear = year, ProviderIds = ids };
        return snapshot with { ItemId = SyntheticId(MediaKeys.For(snapshot).FirstOrDefault() ?? $"film:{LibraryIndex.Normalise(title)}|{year}") };
    }

    public static MediaSnapshot Episode(string series, IReadOnlyDictionary<string, string> seriesIds, int? season, int? episode,
        string? title, IReadOnlyDictionary<string, string> episodeIds)
    {
        var seriesKey = MediaKeys.For(new MediaSnapshot { ItemId = Guid.Empty, Kind = MediaKind.Movie, Name = series, ProviderIds = seriesIds })
            .FirstOrDefault() ?? "series:" + LibraryIndex.Normalise(series);
        var snapshot = new MediaSnapshot
        {
            ItemId = Guid.Empty,
            Kind = MediaKind.Episode,
            Name = string.IsNullOrWhiteSpace(title) ? $"Episode {episode}" : title,
            SeriesId = SyntheticId(seriesKey),
            SeriesName = series,
            SeasonNumber = season,
            EpisodeNumber = episode,
            ProviderIds = episodeIds,
            SeriesProviderIds = seriesIds,
        };
        return snapshot with
        {
            ItemId = SyntheticId(MediaKeys.For(snapshot).FirstOrDefault() ?? string.Create(CultureInfo.InvariantCulture, $"{seriesKey}|s{season}e{episode}")),
        };
    }

    /// <summary>
    /// The same unmatched media always gets the same id, so two imports naming it meet on one item, and its viewings
    /// are recognised as the same ones.
    /// </summary>
    public static Guid SyntheticId(string key) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("jellymold-companion:unmatched:" + key)));
}

/// <summary>
/// The library, indexed for matching imports to it (§8.1): by provider ids first, then — for sources with no ids,
/// like Letterboxd — by title and year.
/// </summary>
public sealed partial class LibraryIndex
{
    private readonly Dictionary<string, MediaSnapshot> _byKey = [];
    private readonly Dictionary<Guid, MediaSnapshot> _byId = [];
    private readonly Dictionary<string, List<MediaSnapshot>> _filmsByTitle = [];

    public LibraryIndex(IEnumerable<MediaSnapshot> items)
    {
        foreach (var item in items)
        {
            _byId[item.ItemId] = item;
            foreach (var key in MediaKeys.For(item))
            {
                _byKey.TryAdd(key, item);
            }

            if (item.Kind == MediaKind.Movie)
            {
                foreach (var title in new[] { item.Name, item.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    var key = Normalise(title!);
                    if (!_filmsByTitle.TryGetValue(key, out var list))
                    {
                        _filmsByTitle[key] = list = [];
                    }

                    list.Add(item);
                }
            }
        }
    }

    public int Count => _byId.Count;

    public MediaSnapshot? ById(Guid id) => _byId.GetValueOrDefault(id);

    public MediaSnapshot? Film(IReadOnlyDictionary<string, string> ids, string? title, int? year)
    {
        var probe = new MediaSnapshot { ItemId = Guid.Empty, Kind = MediaKind.Movie, Name = title ?? string.Empty, ProviderIds = ids };
        foreach (var key in MediaKeys.For(probe))
        {
            if (_byKey.TryGetValue(key, out var found))
            {
                return found;
            }
        }

        if (string.IsNullOrWhiteSpace(title) || !_filmsByTitle.TryGetValue(Normalise(title), out var candidates))
        {
            return null;
        }

        // Same title: the same year, or within one (release dates differ by country); a title alone only if it is unique.
        return candidates.FirstOrDefault(c => year is not null && c.ProductionYear == year)
               ?? candidates.FirstOrDefault(c => year is not null && c.ProductionYear is { } y && Math.Abs(y - year.Value) == 1)
               ?? (year is null && candidates.Count == 1 ? candidates[0] : null);
    }

    public MediaSnapshot? Episode(IReadOnlyDictionary<string, string> seriesIds, int? season, int? episode, IReadOnlyDictionary<string, string> episodeIds)
    {
        var probe = new MediaSnapshot
        {
            ItemId = Guid.Empty,
            Kind = MediaKind.Episode,
            Name = string.Empty,
            SeasonNumber = season,
            EpisodeNumber = episode,
            ProviderIds = episodeIds,
            SeriesProviderIds = seriesIds,
        };
        foreach (var key in MediaKeys.For(probe))
        {
            if (_byKey.TryGetValue(key, out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>"The Matrix: Reloaded!" and "matrix reloaded" are the same title.</summary>
    public static string Normalise(string title)
    {
        var lower = title.Trim().ToLowerInvariant().Replace("&", "and", StringComparison.Ordinal);
        lower = NotWord().Replace(lower, " ");
        lower = Spaces().Replace(lower, " ").Trim();
        return lower.StartsWith("the ", StringComparison.Ordinal) ? lower[4..] : lower;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
