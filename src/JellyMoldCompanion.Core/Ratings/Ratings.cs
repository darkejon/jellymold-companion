using System.Globalization;
using System.Text.Json;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.Ratings;

// Ratings from other sites (design §16 idea 13, decided 2026-11-19). Jellyfin keeps two numbers at most: its own
// community rating, from whichever metadata provider comes first, and the Tomatometer, only with the OMDb plugin. JellyMold
// lets a viewer prefer IMDb, Letterboxd, Metacritic and the rest; this fetches them from MDBList, which gathers them all
// in one answer, with the server owner's own free key. JellyMold never holds a key of its own.

/// <summary>The sources, named as the JellyMold app names them.</summary>
public static class RatingSources
{
    public const string Imdb = "imdb";
    public const string Tomatoes = "tomatoes";
    public const string TomatoesAudience = "tomatoesAudience";
    public const string Metacritic = "metacritic";
    public const string Tmdb = "tmdb";
    public const string Letterboxd = "letterboxd";
    public const string Trakt = "trakt";
    public const string RogerEbert = "rogerEbert";
    public const string MyAnimeList = "myAnimeList";
}

/// <summary>One source's rating of a title, on that source's own scale.</summary>
public sealed record TitleRating(string Source, double Value, long? Votes);

/// <summary>How a title is looked up at MDBList: by which provider's id, as a film or a series.</summary>
public sealed record RatingLookup(string Provider, string Kind, string Id);

public static class RatingKinds
{
    public const string Movie = "movie";
    public const string Show = "show";
}

public static class RatingOutcomes
{
    public const string Found = "found";

    /// <summary>MDBList does not know the title. Asked again after a month: it may have been added.</summary>
    public const string NotFound = "notFound";

    /// <summary>The request went wrong. Asked again the next day.</summary>
    public const string Failed = "failed";
}

/// <summary>When a title was last asked about, and what came of it.</summary>
public sealed record RatingFetch(Guid ItemId, RatingLookup Lookup, string Outcome, DateTimeOffset FetchedAt);

/// <summary>A title's ratings and when they were fetched, as the app is given them.</summary>
public sealed record ItemRatings(Guid ItemId, IReadOnlyList<TitleRating> Ratings, DateTimeOffset FetchedAt);

public sealed record RatingCounts(int Found, int NotFound, int Failed);

/// <summary>When to ask, how much to ask for, and how to read the answer.</summary>
public static class RatingRules
{
    /// <summary>Ratings move slowly; a month keeps a library current for a few requests a day.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    public static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// Titles in one request. One request counts once against the daily allowance however many titles it names (measured
    /// 2026-11-19: two titles moved the count by one). MDBList states no limit; a hundred keeps each answer small.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// The plugin's share of the key's daily allowance. The key is the owner's own, and may be shared with other tools
    /// (Kometa, a Jellyfin MDBList plugin), so the plugin leaves them half.
    /// </summary>
    public const double DailyShare = 0.5;

    /// <summary>
    /// How many more requests the plugin may make today: its share less what it has used, and never more than the key has
    /// left, whoever used it.
    /// </summary>
    public static int Allowance(int dailyLimit, int usedByEveryone, int usedByUs) =>
        Math.Max(0, Math.Min((int)(dailyLimit * DailyShare) - usedByUs, dailyLimit - usedByEveryone));

    public static bool NeedsFetching(RatingFetch? last, DateTimeOffset now) =>
        last is null
        || now - last.FetchedAt >= last.Outcome switch
        {
            RatingOutcomes.Found => RefreshAfter,
            RatingOutcomes.NotFound => RetryNotFoundAfter,
            _ => RetryFailedAfter,
        };

    /// <summary>
    /// The id a title is looked up by. A film by TMDb's, the one MDBList is built around; a series by TheTVDB's, which
    /// is what Jellyfin matches series with; IMDb's as a last resort for either. Null with none of them.
    /// </summary>
    public static RatingLookup? Lookup(string kind, string? tmdb, string? tvdb, string? imdb)
    {
        static bool Number(string? id) => !string.IsNullOrWhiteSpace(id) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;
        static bool Imdb(string? id) => !string.IsNullOrWhiteSpace(id) && id.StartsWith("tt", StringComparison.Ordinal) && id.Length > 2;

        string[] order = kind == RatingKinds.Show ? ["tvdb", "tmdb", "imdb"] : ["tmdb", "imdb", "tvdb"];
        foreach (var provider in order)
        {
            var id = provider switch { "tmdb" => tmdb, "tvdb" => tvdb, _ => imdb };
            if (provider == "imdb" ? Imdb(id) : Number(id))
            {
                return new RatingLookup(provider, kind, id!.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// MDBList's names for its sources, ours, and what turns its value into the source's own scale. It gives TMDb out of
    /// 100 and Letterboxd out of 10 (measured 2026-11-19: The Matrix as 82 and 8.4, which TMDb and Letterboxd show as 8.2
    /// and 4.2). Its Metacritic user score is left out: nobody asked for it.
    /// </summary>
    private static readonly Dictionary<string, (string Source, double Divisor)> Sources = new(StringComparer.Ordinal)
    {
        ["imdb"] = (RatingSources.Imdb, 1),
        ["tomatoes"] = (RatingSources.Tomatoes, 1),
        ["popcorn"] = (RatingSources.TomatoesAudience, 1),
        ["metacritic"] = (RatingSources.Metacritic, 1),
        ["tmdb"] = (RatingSources.Tmdb, 10),
        ["letterboxd"] = (RatingSources.Letterboxd, 2),
        ["trakt"] = (RatingSources.Trakt, 1),
        ["rogerebert"] = (RatingSources.RogerEbert, 1),
        ["myanimelist"] = (RatingSources.MyAnimeList, 1),
    };

    /// <summary>One title's <c>ratings</c> from MDBList: the sources it has a value for, on their own scales.</summary>
    public static IReadOnlyList<TitleRating> Parse(JsonElement ratings)
    {
        var found = new List<TitleRating>();
        if (ratings.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (var r in ratings.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("source", out var name) || name.ValueKind != JsonValueKind.String
                || !Sources.TryGetValue(name.GetString()!, out var source)
                || !r.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var scaled = Math.Round(value.GetDouble() / source.Divisor, 2);
            if (scaled <= 0 || double.IsNaN(scaled) || double.IsInfinity(scaled))
            {
                continue;
            }

            long? votes = r.TryGetProperty("votes", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            found.Add(new TitleRating(source.Source, scaled, votes));
        }

        return found;
    }

    /// <summary>
    /// A batch answer, by the id each title was asked for under <paramref name="provider"/>. Titles MDBList does not know
    /// are simply missing from it (measured 2026-11-19), so a name asked for and absent here was not found.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<TitleRating>> ParseBatch(string json, string provider)
    {
        var byId = new Dictionary<string, IReadOnlyList<TitleRating>>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return byId;
        }

        foreach (var title in document.RootElement.EnumerateArray())
        {
            if (title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object
                || !ids.TryGetProperty(provider, out var id))
            {
                continue;
            }

            var key = id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            };
            if (key is not null)
            {
                byId[key] = title.TryGetProperty("ratings", out var ratings) ? Parse(ratings) : [];
            }
        }

        return byId;
    }

    /// <summary>The body of a batch request: ids as numbers for TMDb and TheTVDB, as text for IMDb.</summary>
    public static string BatchBody(string provider, IEnumerable<string> ids) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ids"] = provider == "imdb"
                ? ids.ToArray()
                : ids.Select(i => long.Parse(i, CultureInfo.InvariantCulture)).ToArray(),
        });
}

/// <summary>The writes, on the recorder's thread.</summary>
public static class TitleRatings
{
    /// <summary>What came of asking about a title. Found replaces its ratings whole; anything else leaves them as they were.</summary>
    public static void Save(HistoryStore s, Guid itemId, RatingLookup lookup, string outcome, IReadOnlyList<TitleRating>? ratings, DateTimeOffset at)
    {
        var item = itemId.ToString("N");
        var stamp = at.ToUnixTimeMilliseconds();
        if (outcome == RatingOutcomes.Found)
        {
            s.Execute("DELETE FROM title_ratings WHERE item_id = $item;", ("$item", item));
            foreach (var r in ratings ?? [])
            {
                s.Execute(
                    "INSERT INTO title_ratings (item_id, source, value, votes, fetched_at) VALUES ($item, $source, $value, $votes, $at);",
                    ("$item", item), ("$source", r.Source), ("$value", r.Value), ("$votes", r.Votes), ("$at", stamp));
            }
        }

        s.Execute(
            """
            INSERT INTO rating_fetches (item_id, provider, provider_id, kind, outcome, fetched_at)
            VALUES ($item, $provider, $id, $kind, $outcome, $at)
            ON CONFLICT (item_id) DO UPDATE SET
                provider = excluded.provider, provider_id = excluded.provider_id, kind = excluded.kind,
                outcome = excluded.outcome, fetched_at = excluded.fetched_at;
            """,
            ("$item", item), ("$provider", lookup.Provider), ("$id", lookup.Id), ("$kind", lookup.Kind), ("$outcome", outcome), ("$at", stamp));
    }
}

/// <summary>The reads, each on its own read-only connection.</summary>
public sealed class RatingQueries(HistoryDatabase database)
{
    /// <summary>
    /// A title's ratings, or null when it has never been found. A title found with no ratings at all (too new, too
    /// obscure) answers an empty list, which is an answer; a later failed refresh keeps the ratings it had.
    /// </summary>
    public ItemRatings? ForItem(Guid itemId)
    {
        using var c = database.Open(readOnly: true);
        var ratings = new List<TitleRating>();
        DateTimeOffset? at = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT source, value, votes, fetched_at FROM title_ratings WHERE item_id = $item ORDER BY source;";
            cmd.Parameters.AddWithValue("$item", itemId.ToString("N"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ratings.Add(new TitleRating(r.GetString(0), r.GetDouble(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
                at ??= DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3));
            }
        }

        if (at is null && Fetch(itemId) is { Outcome: RatingOutcomes.Found } found)
        {
            at = found.FetchedAt;
        }

        return at is { } fetched ? new ItemRatings(itemId, ratings, fetched) : null;
    }

    public RatingFetch? Fetch(Guid itemId)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provider, provider_id, kind, outcome, fetched_at FROM rating_fetches WHERE item_id = $item;";
        cmd.Parameters.AddWithValue("$item", itemId.ToString("N"));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new RatingFetch(itemId, new RatingLookup(r.GetString(0), r.GetString(2), r.GetString(1)), r.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)))
            : null;
    }

    /// <summary>Every title asked about, for picking out those due.</summary>
    public IReadOnlyDictionary<Guid, RatingFetch> Fetches()
    {
        using var c = database.Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT item_id, provider, provider_id, kind, outcome, fetched_at FROM rating_fetches;";
        using var r = cmd.ExecuteReader();
        var all = new Dictionary<Guid, RatingFetch>();
        while (r.Read())
        {
            var id = Guid.Parse(r.GetString(0));
            all[id] = new RatingFetch(id, new RatingLookup(r.GetString(1), r.GetString(3), r.GetString(2)), r.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)));
        }

        return all;
    }

    public RatingCounts Counts()
    {
        using var c = database.Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT COALESCE(SUM(outcome = 'found'), 0), COALESCE(SUM(outcome = 'notFound'), 0), COALESCE(SUM(outcome = 'failed'), 0)
            FROM rating_fetches;
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        r.Read();
        return new RatingCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }
}
