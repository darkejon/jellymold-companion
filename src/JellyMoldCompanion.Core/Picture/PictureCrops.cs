using System.Globalization;
using System.Text.RegularExpressions;
using JellyMoldCompanion.Storage;
using Microsoft.Data.Sqlite;

namespace JellyMoldCompanion.Picture;

// Black bars built into the picture (design §16 idea 11). A widescreen film on Blu-ray is stored in a full 16:9 frame,
// with the bars as black pixels; JellyMold crops them away. Measured here, once per file, every app opens it already
// cropped, the first time too.

/// <summary>Black bars in a frame, in its pixels: rows of black at the top and bottom, columns at the sides.</summary>
public sealed record Bars(int Top, int Bottom, int Left, int Right)
{
    public static readonly Bars None = new(0, 0, 0, 0);
}

/// <summary>Which file a measurement was of, so a replaced file is measured again.</summary>
public sealed record FileStamp(string Path, long Size, DateTimeOffset Modified)
{
    public bool Matches(FileStamp other) =>
        string.Equals(Path, other.Path, StringComparison.Ordinal) && Size == other.Size &&
        Modified.ToUnixTimeMilliseconds() == other.Modified.ToUnixTimeMilliseconds();
}

public static class CropSources
{
    /// <summary>Measured by the plugin. Always wins over an app's report.</summary>
    public const string Server = "server";

    /// <summary>Reported by an app that has settled a crop itself.</summary>
    public const string App = "app";
}

public static class CropOutcomes
{
    public const string Measured = "measured";

    /// <summary>Too few frames showed a picture to trust (a black file, a test card), or the bars made no sense.</summary>
    public const string Unmeasurable = "unmeasurable";

    /// <summary>ffmpeg could not read the file. Tried again after a week.</summary>
    public const string Failed = "failed";
}

/// <summary>One file's bars, from one source.</summary>
public sealed record PictureCrop
{
    public required Guid ItemId { get; init; }
    public required Guid MediaSourceId { get; init; }
    public required string Source { get; init; }
    public string Outcome { get; init; } = CropOutcomes.Measured;

    /// <summary>The frame the bars are in, in its stored pixels: 1920 × 1080 for a Blu-ray, 720 × 576 for a PAL DVD.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }
    public Bars Bars { get; init; } = Bars.None;
    public FileStamp? File { get; init; }

    /// <summary>Sample points that showed a picture. 0 for an app's report.</summary>
    public int Samples { get; init; }
    public int Method { get; init; } = CropRules.Method;
    public Guid? ReportedBy { get; init; }
    public required DateTimeOffset MeasuredAt { get; init; }
}

/// <summary>
/// How bars are measured, and which answer is given. The same rules as the app's own detection, so the two agree:
/// sample across the running time rather than the start (openings, title cards and night scenes are black edge to
/// edge), keep the smallest bars seen (a film that opens out for its IMAX scenes keeps the taller picture: a crop only
/// ever loosens), and ignore bars under 2%.
/// </summary>
public static partial class CropRules
{
    /// <summary>How the plugin measures. Raised when the way changes, so everything is measured again.</summary>
    public const int Method = 1;

    /// <summary>Points across the running time, from 5% to 90%.</summary>
    public const int Points = 12;
    public const double FirstPoint = 0.05;
    public const double LastPoint = 0.90;

    /// <summary>Bars thinner than this share of the frame are not bars: an encoder's ragged edge, a dark border in the film itself.</summary>
    public const double ThinnestBar = 0.02;

    /// <summary>Fewer points than this showing a picture, and nothing is trusted.</summary>
    public const int FewestPoints = 3;

    /// <summary>Black, as a share of the brightest value: ffmpeg's own default (24 of 255), given as a share so 10-bit pictures work too.</summary>
    public const double BlackLevel = 0.094;

    /// <summary>A failed measurement is tried again after this long: the disk may have been asleep, the file half copied.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(7);

    public static IReadOnlyList<TimeSpan> SamplePoints(TimeSpan runtime) =>
        runtime <= TimeSpan.Zero
            ? []
            : Enumerable.Range(0, Points)
                .Select(i => TimeSpan.FromTicks((long)(runtime.Ticks * (FirstPoint + (LastPoint - FirstPoint) * i / (Points - 1)))))
                .ToList();

    /// <summary>
    /// One sample: ffmpeg seeks to the keyframe at or before the point and looks at it alone. Only keyframes are
    /// decoded — the bars are the same in every frame, and decoding a couple of seconds of every frame would cost dozens
    /// of times more — and only the first video stream that is not cover art. One thread, so it never crowds out
    /// playback.
    /// </summary>
    public static IReadOnlyList<string> FfmpegArguments(string path, TimeSpan at) =>
    [
        "-hide_banner", "-nostdin", "-nostats",
        "-threads", "1",
        "-noaccurate_seek", "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-skip_frame", "nokey",
        "-i", path,
        "-map", "0:V:0", "-an", "-sn", "-dn",
        "-vf", string.Create(CultureInfo.InvariantCulture, $"cropdetect=limit={BlackLevel}:round=2:reset=1:skip=0"),
        "-frames:v", "1",
        "-f", "null", "-",
    ];

    /// <summary>
    /// The frames ffmpeg's cropdetect looked at in one sample, as bars. It reports the first and last rows and columns
    /// that are not black; a frame with none (a black frame, a fade) reports them crossed, and is left out.
    /// </summary>
    public static IReadOnlyList<Bars> Frames(string ffmpegOutput, int width, int height)
    {
        var frames = new List<Bars>();
        foreach (Match m in CropdetectLine().Matches(ffmpegOutput))
        {
            var x1 = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var x2 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var y1 = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var y2 = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            if (x1 > x2 || y1 > y2)
            {
                continue;
            }

            frames.Add(new Bars(
                Math.Max(0, y1),
                Math.Max(0, height - 1 - y2),
                Math.Max(0, x1),
                Math.Max(0, width - 1 - x2)));
        }

        return frames;
    }

    /// <summary>A file's bars from its samples: the smallest seen on each side, with thin ones ignored.</summary>
    public static (string Outcome, Bars Bars, int Samples) Combine(IReadOnlyList<IReadOnlyList<Bars>> points, int width, int height)
    {
        var seen = points.Where(p => p.Count > 0).ToList();
        if (seen.Count < FewestPoints)
        {
            return (CropOutcomes.Unmeasurable, Bars.None, seen.Count);
        }

        var frames = seen.SelectMany(p => p).ToList();
        var smallest = new Bars(frames.Min(f => f.Top), frames.Min(f => f.Bottom), frames.Min(f => f.Left), frames.Min(f => f.Right));
        var bars = Tidy(smallest, width, height);
        return Plausible(bars, width, height)
            ? (CropOutcomes.Measured, bars, seen.Count)
            : (CropOutcomes.Unmeasurable, Bars.None, seen.Count);
    }

    /// <summary>Bars under 2% of the frame become 0.</summary>
    public static Bars Tidy(Bars bars, int width, int height) =>
        new(Thin(bars.Top, height), Thin(bars.Bottom, height), Thin(bars.Left, width), Thin(bars.Right, width));

    private static int Thin(int bar, int size) => bar < size * ThinnestBar ? 0 : bar;

    /// <summary>Bars leaving under half the frame are not believed: no film is stored that way.</summary>
    public static bool Plausible(Bars bars, int width, int height) =>
        width > 0 && height > 0 &&
        bars.Top >= 0 && bars.Bottom >= 0 && bars.Left >= 0 && bars.Right >= 0 &&
        bars.Top + bars.Bottom < height / 2.0 && bars.Left + bars.Right < width / 2.0;

    /// <summary>Bars measured in one frame size, in another: the same share of the frame.</summary>
    public static Bars Scale(Bars bars, int fromWidth, int fromHeight, int toWidth, int toHeight) =>
        fromWidth == toWidth && fromHeight == toHeight
            ? bars
            : new Bars(
                (int)Math.Round(bars.Top * (double)toHeight / fromHeight),
                (int)Math.Round(bars.Bottom * (double)toHeight / fromHeight),
                (int)Math.Round(bars.Left * (double)toWidth / fromWidth),
                (int)Math.Round(bars.Right * (double)toWidth / fromWidth));

    /// <summary>The smaller bar on each side: the wider picture.</summary>
    public static Bars Smaller(Bars a, Bars b) =>
        new(Math.Min(a.Top, b.Top), Math.Min(a.Bottom, b.Bottom), Math.Min(a.Left, b.Left), Math.Min(a.Right, b.Right));

    /// <summary>
    /// What to give an app for a file: the plugin's own measurement of it when there is one, otherwise an app's report of
    /// it. Either is passed over once the file has been replaced.
    /// </summary>
    public static PictureCrop? Choose(IReadOnlyList<PictureCrop> rows, FileStamp? current)
    {
        bool OfThisFile(PictureCrop c) => current is null || c.File is null || c.File.Matches(current);
        return rows.FirstOrDefault(c => c.Source == CropSources.Server && c.Outcome == CropOutcomes.Measured && OfThisFile(c))
               ?? rows.FirstOrDefault(c => c.Source == CropSources.App && c.Outcome == CropOutcomes.Measured && OfThisFile(c));
    }

    /// <summary>Whether a file needs measuring: never measured, replaced since, measured an older way, or failed a week ago.</summary>
    public static bool NeedsMeasuring(PictureCrop? measured, FileStamp? current, DateTimeOffset now) =>
        measured is null
        || (current is not null && measured.File is not null && !measured.File.Matches(current))
        || measured.Method < Method
        || (measured.Outcome == CropOutcomes.Failed && now - measured.MeasuredAt >= RetryFailedAfter);

    /// <summary>Why an app's report cannot be taken, or null when it can.</summary>
    public static string? CheckReport(int width, int height, Bars bars)
    {
        if (width is < 16 or > 16384 || height is < 16 or > 16384)
        {
            return "Width and Height are the frame's size in pixels, 16 to 16384.";
        }

        return Plausible(bars, width, height)
            ? null
            : "Bars are 0 or more, and leave at least half the frame: Top + Bottom under half the Height, Left + Right under half the Width.";
    }

    [GeneratedRegex(@"x1:(-?\d+) x2:(-?\d+) y1:(-?\d+) y2:(-?\d+)")]
    private static partial Regex CropdetectLine();
}

/// <summary>The writes, on the recorder's thread.</summary>
public static class PictureCrops
{
    public static void Save(HistoryStore s, PictureCrop c) => s.Execute(
        """
        INSERT INTO picture_crops (media_source_id, source, item_id, outcome, width, height, bar_top, bar_bottom, bar_left, bar_right,
                                   file_path, file_size, file_modified, samples, method, reported_by, measured_at)
        VALUES ($source_id, $source, $item, $outcome, $width, $height, $top, $bottom, $left, $right,
                $path, $size, $modified, $samples, $method, $by, $at)
        ON CONFLICT (media_source_id, source) DO UPDATE SET
            item_id = excluded.item_id, outcome = excluded.outcome, width = excluded.width, height = excluded.height,
            bar_top = excluded.bar_top, bar_bottom = excluded.bar_bottom, bar_left = excluded.bar_left, bar_right = excluded.bar_right,
            file_path = excluded.file_path, file_size = excluded.file_size, file_modified = excluded.file_modified,
            samples = excluded.samples, method = excluded.method, reported_by = excluded.reported_by, measured_at = excluded.measured_at;
        """,
        ("$source_id", c.MediaSourceId.ToString("N")), ("$source", c.Source), ("$item", c.ItemId.ToString("N")), ("$outcome", c.Outcome),
        ("$width", c.Width), ("$height", c.Height), ("$top", c.Bars.Top), ("$bottom", c.Bars.Bottom), ("$left", c.Bars.Left),
        ("$right", c.Bars.Right), ("$path", c.File?.Path), ("$size", c.File?.Size), ("$modified", c.File?.Modified.ToUnixTimeMilliseconds()),
        ("$samples", c.Samples), ("$method", c.Method), ("$by", c.ReportedBy?.ToString("N")), ("$at", c.MeasuredAt.ToUnixTimeMilliseconds()));

    /// <summary>
    /// An app's report of a file. Merged with an earlier report of the same file: the smaller bar wins on each side, so
    /// a second opinion only ever crops less. A report of a replaced file starts again. Returns what is kept.
    /// </summary>
    public static PictureCrop Report(HistoryStore s, PictureCrop report)
    {
        var kept = report with { Source = CropSources.App, Outcome = CropOutcomes.Measured, Samples = 0 };
        using (var command = s.Command(PictureCropQueries.Select + " WHERE media_source_id = $id AND source = $source;",
                   ("$id", report.MediaSourceId.ToString("N")), ("$source", CropSources.App)))
        using (var r = command.ExecuteReader())
        {
            if (r.Read() && PictureCropQueries.FromRow(r) is { Outcome: CropOutcomes.Measured } earlier
                         && (earlier.File is null || report.File is null || earlier.File.Matches(report.File)))
            {
                var scaled = CropRules.Scale(report.Bars, report.Width, report.Height, earlier.Width, earlier.Height);
                kept = kept with
                {
                    Width = earlier.Width,
                    Height = earlier.Height,
                    Bars = CropRules.Smaller(earlier.Bars, scaled),
                    File = report.File ?? earlier.File,
                };
            }
        }

        Save(s, kept);
        return kept;
    }

    /// <summary>An admin's fresh start for an item: everything measured and reported goes, and it is measured again.</summary>
    public static int Forget(HistoryStore s, Guid itemId) =>
        s.Execute("DELETE FROM picture_crops WHERE item_id = $item;", ("$item", itemId.ToString("N")));
}

public sealed record CropCounts(int Measured, int WithBars, int Unmeasurable, int Failed, int Reported);

/// <summary>The reads, each on its own read-only connection.</summary>
public sealed class PictureCropQueries(HistoryDatabase database)
{
    internal const string Select =
        "SELECT media_source_id, source, item_id, outcome, width, height, bar_top, bar_bottom, bar_left, bar_right, " +
        "file_path, file_size, file_modified, samples, method, reported_by, measured_at FROM picture_crops";

    /// <summary>Everything known of one file: the plugin's measurement and apps' report, either or both.</summary>
    public IReadOnlyList<PictureCrop> ForSource(Guid mediaSourceId) =>
        Read(Select + " WHERE media_source_id = $id;", ("$id", mediaSourceId.ToString("N")));

    public CropCounts Counts()
    {
        using var c = database.Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                COALESCE(SUM(source = 'server' AND outcome = 'measured'), 0),
                COALESCE(SUM(source = 'server' AND outcome = 'measured' AND (bar_top + bar_bottom + bar_left + bar_right) > 0), 0),
                COALESCE(SUM(source = 'server' AND outcome = 'unmeasurable'), 0),
                COALESCE(SUM(source = 'server' AND outcome = 'failed'), 0),
                COALESCE(SUM(source = 'app'), 0)
            FROM picture_crops;
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new CropCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
    }

    internal static PictureCrop FromRow(SqliteDataReader r) => new()
    {
        MediaSourceId = Guid.Parse(r.GetString(0)),
        Source = r.GetString(1),
        ItemId = Guid.Parse(r.GetString(2)),
        Outcome = r.GetString(3),
        Width = r.GetInt32(4),
        Height = r.GetInt32(5),
        Bars = new Bars(r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9)),
        File = r.IsDBNull(10) ? null : new FileStamp(r.GetString(10), r.IsDBNull(11) ? 0 : r.GetInt64(11),
            DateTimeOffset.FromUnixTimeMilliseconds(r.IsDBNull(12) ? 0 : r.GetInt64(12))),
        Samples = r.GetInt32(13),
        Method = r.GetInt32(14),
        ReportedBy = r.IsDBNull(15) ? null : Guid.Parse(r.GetString(15)),
        MeasuredAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(16)),
    };

    private List<PictureCrop> Read(string sql, params (string Name, object? Value)[] parameters)
    {
        using var c = database.Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using var r = cmd.ExecuteReader();
        var rows = new List<PictureCrop>();
        while (r.Read())
        {
            rows.Add(FromRow(r));
        }

        return rows;
    }
}
