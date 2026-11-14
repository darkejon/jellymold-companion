using JellyMoldCompanion.Picture;
using JellyMoldCompanion.Storage;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §16 idea 11: black bars built into the picture, measured once per file.</summary>
public class PictureCropTests
{
    private static readonly Guid Film = Guid.Parse("f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1");
    private static readonly FileStamp Remux = new("/films/Companion (2025).mkv", 41_000_000_000, Harness.Tuesday.AddDays(-30));
    private static readonly FileStamp Replaced = Remux with { Size = 38_500_000_000, Modified = Harness.Tuesday.AddDays(-1) };

    // What ffmpeg's cropdetect prints for a 2.39:1 film in a 1920 × 1080 frame, and for a black frame.
    private const string Scope =
        "[Parsed_cropdetect_0 @ 0x7c4f008b40] x1:0 x2:1919 y1:138 y2:941 w:1920 h:804 x:0 y:138 pts:1000 t:1.000000 limit:0.094000 crop=1920:804:0:138";
    private const string Black =
        "[Parsed_cropdetect_0 @ 0x7b61154480] x1:1919 x2:0 y1:1079 y2:0 w:-1918 h:-1078 x:1920 y:1080 pts:0 t:0.000000 limit:0.094000 crop=-1918:-1078:1920:1080";

    private static T Write<T>(Harness h, Func<HistoryStore, T> work)
    {
        T result = default!;
        h.Store.InTransaction(() => result = work(h.Store));
        return result;
    }

    private static PictureCrop Crop(string source, Bars bars, FileStamp? file = null, int width = 1920, int height = 1080, DateTimeOffset? at = null,
        string outcome = CropOutcomes.Measured) => new()
    {
        ItemId = Film,
        MediaSourceId = Film,
        Source = source,
        Outcome = outcome,
        Width = width,
        Height = height,
        Bars = bars,
        File = file ?? Remux,
        Samples = source == CropSources.Server ? 12 : 0,
        ReportedBy = source == CropSources.App ? Harness.Jon.Id : null,
        MeasuredAt = at ?? Harness.Tuesday,
    };

    private static IReadOnlyList<Bars> Point(int top, int bottom, int left = 0, int right = 0) => [new Bars(top, bottom, left, right)];

    // — Measuring —

    [Fact]
    public void TwelvePointsFromFivePercentToNinety()
    {
        var points = CropRules.SamplePoints(TimeSpan.FromMinutes(100));
        Assert.Equal(12, points.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), points[0]);
        Assert.Equal(TimeSpan.FromMinutes(90), points[^1]);
        Assert.Empty(CropRules.SamplePoints(TimeSpan.Zero));
    }

    [Fact]
    public void EachSampleLooksAtOneKeyframeOfTheFirstRealVideoStream()
    {
        var args = CropRules.FfmpegArguments("/films/a b.mkv", TimeSpan.FromSeconds(147.7491));
        Assert.Equal(["-noaccurate_seek", "-ss", "147.749", "-skip_frame", "nokey", "-i", "/films/a b.mkv"], args.Skip(5).Take(7));
        Assert.Contains("0:V:0", args); // capital V: not cover art
        Assert.Contains("cropdetect=limit=0.094:round=2:reset=1:skip=0", args);
        Assert.Equal("1", args[args.ToList().IndexOf("-frames:v") + 1]);
    }

    [Fact]
    public void CropdetectsReportBecomesBars()
    {
        var frame = Assert.Single(CropRules.Frames(Scope, 1920, 1080));
        Assert.Equal(new Bars(138, 138, 0, 0), frame);
    }

    [Fact]
    public void ABlackFrameIsLeftOut() => Assert.Empty(CropRules.Frames(Black + "\n" + Black, 1920, 1080));

    [Fact]
    public void AScopeFilmIsMeasured()
    {
        var points = Enumerable.Range(0, 12).Select(_ => CropRules.Frames(Scope, 1920, 1080)).ToList();
        var (outcome, bars, samples) = CropRules.Combine(points, 1920, 1080);
        Assert.Equal(CropOutcomes.Measured, outcome);
        Assert.Equal(new Bars(138, 138, 0, 0), bars);
        Assert.Equal(12, samples);
    }

    [Fact]
    public void AFilmThatOpensOutForItsImaxScenesKeepsTheTallerPicture()
    {
        // Mostly 2.39:1, with one point in a 1.90:1 IMAX scene: the crop is the IMAX one's.
        var points = Enumerable.Range(0, 11).Select(_ => Point(138, 138)).Append(Point(35, 35)).ToList();
        Assert.Equal(new Bars(35, 35, 0, 0), CropRules.Combine(points, 1920, 1080).Bars);
    }

    [Fact]
    public void ADarkSceneAndABlackFrameChangeNothing()
    {
        // A night scene whose lower part is black reads as a deeper bottom bar; a fade reads as nothing at all.
        var points = Enumerable.Range(0, 10).Select(_ => Point(138, 138))
            .Append(Point(138, 260))
            .Append(CropRules.Frames(Black, 1920, 1080))
            .ToList();
        var (_, bars, samples) = CropRules.Combine(points, 1920, 1080);
        Assert.Equal(new Bars(138, 138, 0, 0), bars);
        Assert.Equal(11, samples);
    }

    [Fact]
    public void BarsUnderTwoPercentAreIgnored()
    {
        // 4 columns at the left of a 16:9 episode is an encoder's ragged edge, not a bar; 21 rows is under 2% of 1080.
        var points = Enumerable.Range(0, 12).Select(_ => Point(21, 21, 4, 0)).ToList();
        Assert.Equal(Bars.None, CropRules.Combine(points, 1920, 1080).Bars);
    }

    [Fact]
    public void AFourByThreeShowInAWideFrameHasBarsAtTheSides()
    {
        var points = Enumerable.Range(0, 12).Select(_ => Point(0, 0, 240, 240)).ToList();
        Assert.Equal(new Bars(0, 0, 240, 240), CropRules.Combine(points, 1920, 1080).Bars);
    }

    [Fact]
    public void TooFewPointsWithAPictureIsNotTrusted()
    {
        IReadOnlyList<Bars> black = [];
        var points = Enumerable.Range(0, 10).Select(_ => black).Append(Point(138, 138)).Append(Point(138, 138)).ToList();
        var (outcome, bars, _) = CropRules.Combine(points, 1920, 1080);
        Assert.Equal(CropOutcomes.Unmeasurable, outcome);
        Assert.Equal(Bars.None, bars);
    }

    [Fact]
    public void BarsLeavingUnderHalfTheFrameAreNotBelieved()
    {
        var points = Enumerable.Range(0, 12).Select(_ => Point(300, 300)).ToList();
        Assert.Equal(CropOutcomes.Unmeasurable, CropRules.Combine(points, 1920, 1080).Outcome);
    }

    // — What an app is given —

    [Fact]
    public void ThePluginsMeasurementWinsOverAnAppsReport()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(132, 132, 0, 0))));
        Write(h, s => { PictureCrops.Save(s, Crop(CropSources.Server, new Bars(138, 138, 0, 0))); return 0; });

        var rows = new PictureCropQueries(h.Database).ForSource(Film);
        Assert.Equal(2, rows.Count);
        var given = CropRules.Choose(rows, Remux)!;
        Assert.Equal(CropSources.Server, given.Source);
        Assert.Equal(138, given.Bars.Top);
    }

    [Fact]
    public void AnAppsReportIsGivenUntilThePluginHasMeasured()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0))));
        Write(h, s => { PictureCrops.Save(s, Crop(CropSources.Server, Bars.None, outcome: CropOutcomes.Failed)); return 0; });

        var given = CropRules.Choose(new PictureCropQueries(h.Database).ForSource(Film), Remux)!;
        Assert.Equal(CropSources.App, given.Source);
    }

    [Fact]
    public void SecondOpinionsOnlyEverCropLess()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0))));
        var kept = Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(130, 140, 0, 6), at: Harness.Tuesday.AddHours(1))));

        Assert.Equal(new Bars(130, 138, 0, 0), kept.Bars);
        Assert.Equal(new Bars(130, 138, 0, 0), Assert.Single(new PictureCropQueries(h.Database).ForSource(Film)).Bars);
    }

    [Fact]
    public void AReportInAnotherFrameSizeIsTheSameShareOfTheFrame()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0))));
        var kept = Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(270, 270, 0, 0), width: 3840, height: 2160)));

        Assert.Equal((1920, 1080), (kept.Width, kept.Height));
        Assert.Equal(new Bars(135, 135, 0, 0), kept.Bars);
    }

    [Fact]
    public void AReportOfAReplacedFileStartsAgain()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(0, 0, 0, 0))));
        var kept = Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0), file: Replaced)));
        Assert.Equal(new Bars(138, 138, 0, 0), kept.Bars);
    }

    [Fact]
    public void AReplacedFileIsNotGivenTheOldFilesBarsAndIsMeasuredAgain()
    {
        var measured = Crop(CropSources.Server, new Bars(138, 138, 0, 0));
        Assert.Null(CropRules.Choose([measured], Replaced));
        Assert.True(CropRules.NeedsMeasuring(measured, Replaced, Harness.Tuesday));
        Assert.False(CropRules.NeedsMeasuring(measured, Remux, Harness.Tuesday));
        Assert.True(CropRules.NeedsMeasuring(null, Remux, Harness.Tuesday));
    }

    [Fact]
    public void AFailedMeasurementIsTriedAgainAfterAWeek()
    {
        var failed = Crop(CropSources.Server, Bars.None, outcome: CropOutcomes.Failed);
        Assert.False(CropRules.NeedsMeasuring(failed, Remux, Harness.Tuesday.AddDays(6)));
        Assert.True(CropRules.NeedsMeasuring(failed, Remux, Harness.Tuesday.AddDays(7)));

        var nothingThere = Crop(CropSources.Server, Bars.None, outcome: CropOutcomes.Unmeasurable);
        Assert.False(CropRules.NeedsMeasuring(nothingThere, Remux, Harness.Tuesday.AddDays(30)));
    }

    [Fact]
    public void AMeasurementMadeAnOlderWayIsMadeAgain() =>
        Assert.True(CropRules.NeedsMeasuring(Crop(CropSources.Server, Bars.None) with { Method = CropRules.Method - 1 }, Remux, Harness.Tuesday));

    [Theory]
    [InlineData(0, 1080, 0, 0, 0, 0)]
    [InlineData(1920, 1080, -2, 0, 0, 0)]
    [InlineData(1920, 1080, 270, 270, 0, 0)]
    [InlineData(1920, 1080, 0, 0, 480, 480)]
    public void ImplausibleReportsAreRefused(int width, int height, int top, int bottom, int left, int right) =>
        Assert.NotNull(CropRules.CheckReport(width, height, new Bars(top, bottom, left, right)));

    [Fact]
    public void ForgettingAnItemClearsWhatWasMeasuredAndReported()
    {
        using var h = new Harness();
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0))));
        Write(h, s => { PictureCrops.Save(s, Crop(CropSources.Server, new Bars(138, 138, 0, 0))); return 0; });

        Assert.Equal(2, Write(h, s => PictureCrops.Forget(s, Film)));
        Assert.Empty(new PictureCropQueries(h.Database).ForSource(Film));
    }

    [Fact]
    public void TheCountsSayWhatHasBeenMeasured()
    {
        using var h = new Harness();
        Write(h, s => { PictureCrops.Save(s, Crop(CropSources.Server, new Bars(138, 138, 0, 0))); return 0; });
        Write(h, s => { PictureCrops.Save(s, Crop(CropSources.Server, Bars.None) with { MediaSourceId = Guid.NewGuid() }); return 0; });
        Write(h, s => PictureCrops.Report(s, Crop(CropSources.App, new Bars(138, 138, 0, 0))));

        Assert.Equal(new CropCounts(Measured: 2, WithBars: 1, Unmeasurable: 0, Failed: 0, Reported: 1), new PictureCropQueries(h.Database).Counts());
    }
}
