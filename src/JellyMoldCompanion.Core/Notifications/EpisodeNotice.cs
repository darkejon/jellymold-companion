using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JellyMoldCompanion.Notifications;

/// <summary>A series as the library has it when its batch is sent.</summary>
public sealed record SeriesFacts(Guid JellyfinId, string Name, string? Tmdb, string? Tvdb);

/// <summary>An episode as the library has it when its batch is sent: read then, not when it arrived.</summary>
public sealed record EpisodeFacts(Guid JellyfinId, int? Season, int? Episode, string Name);

/// <summary>The shows somebody on this server wants each new episode of, as the relay lists them.</summary>
public sealed record WantedShows(IReadOnlySet<string> Tmdb, IReadOnlySet<string> Tvdb)
{
    public static readonly WantedShows None = new(new HashSet<string>(), new HashSet<string>());

    public bool Any => Tmdb.Count > 0 || Tvdb.Count > 0;

    public bool Wants(SeriesFacts series) =>
        (EpisodeNotice.Id(series.Tmdb) is { } tmdb && Tmdb.Contains(tmdb))
        || (EpisodeNotice.Id(series.Tvdb) is { } tvdb && Tvdb.Contains(tvdb));
}

public enum BatchVerdict
{
    /// <summary>Tell the relay now.</summary>
    Send,

    /// <summary>The series has no TMDB or TVDB id yet: its metadata may still be on its way. Look again later.</summary>
    Hold,

    /// <summary>Nothing to tell: the episodes or the series are gone, or the series never got an id.</summary>
    Drop,
}

/// <summary>
/// What the plugin tells JellyMold's notification relay about new episodes (design §16 idea 16): only for shows
/// somebody here asked to hear about, and only the show's name and ids and each episode's season, number, title and id.
/// </summary>
public static class EpisodeNotice
{
    /// <summary>The relay reads at most this many episodes of one request.</summary>
    public const int MaxEpisodes = 200;

    /// <summary>The relay reads 64 KB of a request; well under, so a batch is never refused for its size.</summary>
    public const int MaxBodyBytes = 60 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>A TMDB or TVDB id as the relay takes it: digits only, not zero. Anything else is no id.</summary>
    public static string? Id(string? id)
    {
        var trimmed = id?.Trim();
        return trimmed is { Length: > 0 and <= 12 } && trimmed.All(char.IsAsciiDigit) && trimmed.TrimStart('0').Length > 0
            ? trimmed
            : null;
    }

    /// <summary>
    /// Whether a batch goes now. The relay finds a show by its TMDB or TVDB id, so a series with neither is held while
    /// its metadata may still arrive; ten minutes on, it goes if it has one by then, or not at all.
    /// </summary>
    public static BatchVerdict Decide(SeriesFacts? series, IReadOnlyCollection<EpisodeFacts> episodes, bool pastLongest)
    {
        if (series is null || episodes.Count == 0)
        {
            return BatchVerdict.Drop;
        }

        if (Id(series.Tmdb) is not null || Id(series.Tvdb) is not null)
        {
            return BatchVerdict.Send;
        }

        return pastLongest ? BatchVerdict.Drop : BatchVerdict.Hold;
    }

    /// <summary>
    /// The request body: <c>EPISODES_ADDED</c>, the series and its episodes in order. A batch bigger than the relay
    /// reads (<see cref="MaxEpisodes"/>, or <see cref="MaxBodyBytes"/> for very long titles) keeps its earliest
    /// episodes, and the rest are left out.
    /// </summary>
    public static string Body(SeriesFacts series, IEnumerable<EpisodeFacts> episodes)
    {
        var ordered = episodes
            .OrderBy(e => e.Season ?? int.MaxValue)
            .ThenBy(e => e.Episode ?? int.MaxValue)
            .Take(MaxEpisodes)
            .Select(e => new EpisodeJson(e.Season, e.Episode, e.Name, Hex(e.JellyfinId)))
            .ToList();
        var show = new SeriesJson(series.Name, Id(series.Tmdb), Id(series.Tvdb), Hex(series.JellyfinId));

        var count = ordered.Count;
        while (true)
        {
            var body = JsonSerializer.Serialize(new BodyJson("EPISODES_ADDED", show, ordered.Take(count).ToList()), Json);
            if (Encoding.UTF8.GetByteCount(body) <= MaxBodyBytes || count <= 1)
            {
                return body;
            }

            // Cut in proportion, then step down: never more than a few passes.
            count = Math.Max(1, Math.Min(count - 1, count * MaxBodyBytes / Encoding.UTF8.GetByteCount(body)));
        }
    }

    /// <summary>Jellyfin writes its ids as 32 hex digits, without dashes.</summary>
    private static string Hex(Guid id) => id.ToString("N");

    private sealed record BodyJson(string Type, SeriesJson Series, IReadOnlyList<EpisodeJson> Episodes);

    private sealed record SeriesJson(string Name, string? TmdbId, string? TvdbId, string JellyfinId);

    private sealed record EpisodeJson(int? Season, int? Episode, string Name, string JellyfinId);
}
