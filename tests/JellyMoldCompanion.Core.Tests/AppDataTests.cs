using JellyMoldCompanion.AppData;
using JellyMoldCompanion.Storage;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §16: the app's own data on the server.</summary>
public class DocumentTests
{
    private static readonly string Jon = Documents.Owner(Harness.Jon.Id);

    private static T Write<T>(Harness h, Func<HistoryStore, T> work)
    {
        T result = default!;
        h.Store.InTransaction(() => result = work(h.Store));
        return result;
    }

    [Fact]
    public void AWriteNamesTheVersionItRead()
    {
        using var h = new Harness();
        var first = Write(h, s => Documents.Put(s, Jon, "settings", """{"theme":"dark"}""", 0, Harness.Jon.Id, Harness.Tuesday));
        Assert.True(first.Saved);
        Assert.Equal(1, first.Document!.Version);

        var second = Write(h, s => Documents.Put(s, Jon, "settings", """{"theme":"light"}""", 1, Harness.Jon.Id, Harness.Tuesday));
        Assert.Equal(2, second.Document!.Version);
        Assert.Equal("light", new AppDataQueries(h.Database).Get(Jon, "settings")!.Value!.Value.GetProperty("theme").GetString());
    }

    [Fact]
    public void AStaleWriteIsRefusedWithTheCurrentDocument()
    {
        using var h = new Harness();
        Write(h, s => Documents.Put(s, Jon, "settings", """{"a":1}""", 0, Harness.Jon.Id, Harness.Tuesday));
        Write(h, s => Documents.Put(s, Jon, "settings", """{"a":2}""", 1, Harness.Jon.Id, Harness.Tuesday)); // the iPad

        var phone = Write(h, s => Documents.Put(s, Jon, "settings", """{"a":3}""", 1, Harness.Jon.Id, Harness.Tuesday));
        Assert.False(phone.Saved);
        Assert.Equal(2, phone.Document!.Version);
        Assert.Equal(2, phone.Document.Value!.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void CreatingWhatAlreadyExistsIsAClash()
    {
        using var h = new Harness();
        Write(h, s => Documents.Put(s, Jon, "k", "1", 0, Harness.Jon.Id, Harness.Tuesday));
        Assert.False(Write(h, s => Documents.Put(s, Jon, "k", "2", 0, Harness.Jon.Id, Harness.Tuesday)).Saved);
    }

    [Fact]
    public void ADeletionLeavesAMarkerOtherDevicesHear()
    {
        using var h = new Harness();
        var queries = new AppDataQueries(h.Database);
        Write(h, s => Documents.Put(s, Jon, "k", "1", 0, Harness.Jon.Id, Harness.Tuesday));
        var (_, token) = queries.Changes(Jon, 0, 100);

        Assert.True(Write(h, s => Documents.Delete(s, Jon, "k", 1, Harness.Jon.Id, Harness.Tuesday)).Saved);
        Assert.Null(queries.Get(Jon, "k"));
        var (changed, _) = queries.Changes(Jon, token, 100);
        Assert.True(Assert.Single(changed).Deleted);

        // And it can be made again, carrying on from the marker's version.
        Assert.True(Write(h, s => Documents.Put(s, Jon, "k", "2", 2, Harness.Jon.Id, Harness.Tuesday)).Saved);
    }

    [Fact]
    public void OnePersonsDocumentsAreNotAnothers()
    {
        using var h = new Harness();
        Write(h, s => Documents.Put(s, Jon, "settings", "1", 0, Harness.Jon.Id, Harness.Tuesday));
        Assert.Null(new AppDataQueries(h.Database).Get(Documents.Owner(Harness.Sam.Id), "settings"));
        Assert.Null(new AppDataQueries(h.Database).Get(Documents.Household, "settings"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("-leading")]
    public void KeysAreKeptPlain(string key)
    {
        using var h = new Harness();
        Assert.Throws<AppDataException>(() => Write(h, s => Documents.Put(s, Jon, key, "1", 0, Harness.Jon.Id, Harness.Tuesday)));
    }

    [Fact]
    public void AValueMustBeJson()
    {
        using var h = new Harness();
        Assert.Throws<AppDataException>(() => Write(h, s => Documents.Put(s, Jon, "k", "{not json", 0, Harness.Jon.Id, Harness.Tuesday)));
    }

    [Fact]
    public void AListCanBeNarrowedByPrefix()
    {
        using var h = new Harness();
        foreach (var key in new[] { "quiz-round:a", "quiz-round:b", "chapters:a" })
        {
            Write(h, s => Documents.Put(s, Documents.Household, key, "{}", 0, Harness.Jon.Id, Harness.Tuesday));
        }

        Assert.Equal(2, new AppDataQueries(h.Database).List(Documents.Household, "quiz-round:").Count);
    }
}

public class QuizLeaderboardTests
{
    private static QuizScoreInput Score(string id, int score, Guid? series = null) => new()
    {
        ScoreId = id,
        SeriesId = series ?? Harness.Episode().SeriesId,
        SeriesName = "Deep Space Nine",
        Score = score,
        OutOf = 5,
        CompletedAt = Harness.Tuesday.UtcDateTime,
    };

    [Fact]
    public void PostingTheSameQuizTwiceCountsItOnce()
    {
        using var h = new Harness();
        var added = 0;
        h.Store.InTransaction(() =>
        {
            added += Quiz.Record(h.Store, Harness.Jon.Id, [Score("q1", 4)]);
            added += Quiz.Record(h.Store, Harness.Jon.Id, [Score("q1", 4)]);
        });
        Assert.Equal(1, added);
    }

    [Fact]
    public void OthersAppearOnlyOnceTheyChooseToShare()
    {
        using var h = new Harness();
        h.Store.InTransaction(() =>
        {
            h.Store.UpsertUser(Harness.Jon, Harness.Tuesday);
            h.Store.UpsertUser(Harness.Sam, Harness.Tuesday);
            Quiz.Record(h.Store, Harness.Jon.Id, [Score("j1", 3)]);
            Quiz.Record(h.Store, Harness.Sam.Id, [Score("s1", 5)]);
        });
        var queries = new AppDataQueries(h.Database);

        var before = queries.Leaderboard(Harness.Jon.Id, null, null);
        Assert.True(Assert.Single(before).IsYou);

        h.Store.InTransaction(() => Quiz.SetSharing(h.Store, Harness.Sam.Id, true, Harness.Tuesday));
        var after = queries.Leaderboard(Harness.Jon.Id, null, null);
        Assert.Equal(["sam", "jon"], after.Select(r => r.Name));
        Assert.Equal(1, after[0].Perfect);
    }

    [Fact]
    public void ALeaderboardCanBeForOneProgramme()
    {
        using var h = new Harness();
        var other = Guid.NewGuid();
        h.Store.InTransaction(() => Quiz.Record(h.Store, Harness.Jon.Id, [Score("a", 3), Score("b", 5, other)]));
        Assert.Equal(1, Assert.Single(new AppDataQueries(h.Database).Leaderboard(Harness.Jon.Id, other, null)).Quizzes);
    }

    [Fact]
    public void AnImplausibleResultIsRefused()
    {
        using var h = new Harness();
        Assert.Throws<AppDataException>(() => h.Store.InTransaction(() => Quiz.Record(h.Store, Harness.Jon.Id, [Score("x", 9)])));
    }
}

public class SegmentTests
{
    private static readonly Guid Item = Harness.Episode().ItemId;
    private static long? Runtime(Guid id) => id == Item ? TimeSpan.FromMinutes(44).Ticks : null;

    [Fact]
    public void ALearnedIntroIsKeptOnePerKind()
    {
        using var h = new Harness();
        h.Store.InTransaction(() =>
        {
            Segments.Put(h.Store, [new SegmentInput { ItemId = Item, Kind = "intro", StartSeconds = 60, EndSeconds = 120, Source = "learned" }], Runtime, Harness.Jon.Id, Harness.Tuesday);
            Segments.Put(h.Store, [new SegmentInput { ItemId = Item, Kind = "Intro", StartSeconds = 62, EndSeconds = 121 }], Runtime, Harness.Jon.Id, Harness.Tuesday);
        });

        var segment = Assert.Single(new AppDataQueries(h.Database).SegmentsFor(Item));
        Assert.Equal("Intro", segment.Kind);
        Assert.Equal(62, segment.StartSeconds, 3);
    }

    [Theory]
    [InlineData("Intro", 120, 60)]
    [InlineData("Intro", 0, 99999)]
    [InlineData("Credits", 0, 60)]
    public void ImplausibleSegmentsAreRefused(string kind, double start, double end)
    {
        using var h = new Harness();
        Assert.Throws<AppDataException>(() => h.Store.InTransaction(() =>
            Segments.Put(h.Store, [new SegmentInput { ItemId = Item, Kind = kind, StartSeconds = start, EndSeconds = end }], Runtime, Harness.Jon.Id, Harness.Tuesday)));
    }

    [Fact]
    public void AnItemNotInTheLibraryIsRefused()
    {
        using var h = new Harness();
        Assert.Throws<AppDataException>(() => h.Store.InTransaction(() =>
            Segments.Put(h.Store, [new SegmentInput { ItemId = Guid.NewGuid(), Kind = "Intro", StartSeconds = 0, EndSeconds = 30 }], Runtime, Harness.Jon.Id, Harness.Tuesday)));
    }
}
