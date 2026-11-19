using JellyMoldCompanion.Ratings;
using JellyMoldCompanion.Storage;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §16 idea 13: ratings from other sites, fetched from MDBList with the server owner's own key.</summary>
public class RatingTests
{
    private static readonly Guid Matrix = Guid.Parse("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static void Write(Harness h, Action<HistoryStore> work) => h.Store.InTransaction(() => work(h.Store));

    // — Reading MDBList's answer —

    [Fact]
    public void AFilmBatchIsReadByTheIdsAskedFor()
    {
        var found = RatingRules.ParseBatch(Fixture("mdblist-batch-tmdb-movie.json"), "tmdb");
        Assert.Equal(["27205", "603"], found.Keys.Order());
        var matrix = found["603"].ToDictionary(r => r.Source, r => r.Value);
        Assert.Equal(8.7, matrix[RatingSources.Imdb]);
        Assert.Equal(83, matrix[RatingSources.Tomatoes]);
        Assert.Equal(85, matrix[RatingSources.TomatoesAudience]);
        Assert.Equal(73, matrix[RatingSources.Metacritic]);
        Assert.Equal(87, matrix[RatingSources.Trakt]);
        Assert.Equal(3.0, matrix[RatingSources.RogerEbert]);
    }

    [Fact]
    public void TmdbAndLetterboxdAreGivenBackTheirOwnScales()
    {
        var matrix = RatingRules.ParseBatch(Fixture("mdblist-batch-tmdb-movie.json"), "tmdb")["603"].ToDictionary(r => r.Source, r => r.Value);
        Assert.Equal(8.2, matrix[RatingSources.Tmdb]);        // MDBList: 82
        Assert.Equal(4.2, matrix[RatingSources.Letterboxd]);  // MDBList: 8.4
    }

    [Fact]
    public void EmptySourcesAndTheMetacriticUserScoreAreLeftOut()
    {
        var matrix = RatingRules.ParseBatch(Fixture("mdblist-batch-tmdb-movie.json"), "tmdb")["603"].Select(r => r.Source).ToList();
        Assert.DoesNotContain(RatingSources.MyAnimeList, matrix);  // null for a film
        Assert.DoesNotContain("metacriticuser", matrix);
        Assert.Equal(8, matrix.Count);
    }

    [Fact]
    public void ASeriesBatchLeavesOutWhatMdbListDoesNotKnow()
    {
        var found = RatingRules.ParseBatch(Fixture("mdblist-batch-tvdb-show.json"), "tvdb");
        Assert.Equal(["72073"], found.Keys);   // 99999999 was asked for too
        var ds9 = found["72073"].ToDictionary(r => r.Source);
        Assert.Equal(8.1, ds9[RatingSources.Imdb].Value);
        Assert.Equal(78929, ds9[RatingSources.Imdb].Votes);
        Assert.False(ds9.ContainsKey(RatingSources.Letterboxd));
    }

    [Fact]
    public void BatchBodiesSendNumbersExceptForImdb()
    {
        Assert.Equal("""{"ids":[603,27205]}""", RatingRules.BatchBody("tmdb", ["603", "27205"]));
        Assert.Equal("""{"ids":["tt0133093"]}""", RatingRules.BatchBody("imdb", ["tt0133093"]));
    }

    // — Which id a title is looked up by —

    [Fact]
    public void FilmsByTmdbSeriesByTvdbImdbLast()
    {
        Assert.Equal(new RatingLookup("tmdb", "movie", "603"), RatingRules.Lookup(RatingKinds.Movie, "603", "169", "tt0133093"));
        Assert.Equal(new RatingLookup("tvdb", "show", "72073"), RatingRules.Lookup(RatingKinds.Show, "580", "72073", "tt0106145"));
        Assert.Equal(new RatingLookup("imdb", "movie", "tt0133093"), RatingRules.Lookup(RatingKinds.Movie, null, null, "tt0133093"));
        Assert.Null(RatingRules.Lookup(RatingKinds.Movie, "", "0", "nm123"));
    }

    // — Staying inside the allowance —

    [Fact]
    public void ThePluginTakesHalfTheDayAndNeverMoreThanIsLeft()
    {
        Assert.Equal(500, RatingRules.Allowance(1000, usedByEveryone: 0, usedByUs: 0));
        Assert.Equal(300, RatingRules.Allowance(1000, usedByEveryone: 200, usedByUs: 200));
        Assert.Equal(50, RatingRules.Allowance(1000, usedByEveryone: 950, usedByUs: 0));    // another tool used most of it
        Assert.Equal(0, RatingRules.Allowance(1000, usedByEveryone: 600, usedByUs: 600));
    }

    [Fact]
    public void TitlesAreAskedAgainOnlyWhenDue()
    {
        var now = Harness.Tuesday;
        var lookup = new RatingLookup("tmdb", "movie", "603");
        RatingFetch Last(string outcome, int daysAgo) => new(Matrix, lookup, outcome, now.AddDays(-daysAgo));
        Assert.True(RatingRules.NeedsFetching(null, now));
        Assert.False(RatingRules.NeedsFetching(Last(RatingOutcomes.Found, 29), now));
        Assert.True(RatingRules.NeedsFetching(Last(RatingOutcomes.Found, 30), now));
        Assert.False(RatingRules.NeedsFetching(Last(RatingOutcomes.NotFound, 10), now));
        Assert.True(RatingRules.NeedsFetching(Last(RatingOutcomes.Failed, 1), now));
    }

    // — Keeping them —

    [Fact]
    public void AFetchReplacesATitlesRatingsWhole()
    {
        using var h = new Harness();
        var lookup = new RatingLookup("tmdb", "movie", "603");
        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.Found,
            [new(RatingSources.Imdb, 8.6, 100), new(RatingSources.Trakt, 85, 10)], Harness.Tuesday.AddDays(-40)));
        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.Found, [new(RatingSources.Imdb, 8.7, 200)], Harness.Tuesday));

        var kept = new RatingQueries(h.Database).ForItem(Matrix)!;
        Assert.Equal([new TitleRating(RatingSources.Imdb, 8.7, 200)], kept.Ratings);
        Assert.Equal(Harness.Tuesday, kept.FetchedAt);
    }

    [Fact]
    public void AFailedRefreshKeepsWhatWasThere()
    {
        using var h = new Harness();
        var lookup = new RatingLookup("tmdb", "movie", "603");
        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.Found, [new(RatingSources.Imdb, 8.7, 200)], Harness.Tuesday));
        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.Failed, null, Harness.Tuesday.AddDays(31)));

        var queries = new RatingQueries(h.Database);
        Assert.Equal(8.7, queries.ForItem(Matrix)!.Ratings.Single().Value);
        Assert.Equal(RatingOutcomes.Failed, queries.Fetch(Matrix)!.Outcome);   // so it is asked again tomorrow
        Assert.Equal(new RatingCounts(0, 0, 1), queries.Counts());
    }

    [Fact]
    public void NeverFoundIsNoAnswerAndFoundEmptyIsOne()
    {
        using var h = new Harness();
        var queries = new RatingQueries(h.Database);
        var lookup = new RatingLookup("tmdb", "movie", "603");
        Assert.Null(queries.ForItem(Matrix));

        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.NotFound, null, Harness.Tuesday));
        Assert.Null(queries.ForItem(Matrix));

        Write(h, s => TitleRatings.Save(s, Matrix, lookup, RatingOutcomes.Found, [], Harness.Tuesday));
        Assert.Empty(queries.ForItem(Matrix)!.Ratings);
    }
}
