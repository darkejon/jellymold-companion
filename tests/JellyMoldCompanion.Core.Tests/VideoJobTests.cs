using System.Buffers.Binary;
using System.Text;
using JellyMoldCompanion.Hls;

namespace JellyMoldCompanion.Tests;

/// <summary>
/// Design §19 #20: on Jellyfin 10.10, where a video job begun mid-film puts its clock's zero, read from the files it
/// writes, so the plugin's subtitle pieces can say so.
/// </summary>
public class VideoJobTests
{
    private const long Second = HlsSubtitles.TicksPerSecond;

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // — Where a job began: its init piece —

    [Fact]
    public void AJobBegunMidFilmSaysWhereInItsInitPiece()
    {
        // Jellyfin 10.10.7 asked for the piece at 306 s of a transcode (HEVC, keyframes every 5 s): the job began at the
        // keyframe before it. Its pieces' clock reads 0 there.
        var init = VideoJob.ReadInit(Fixture("init-10.10-transcode-306s.mp4"));
        Assert.NotNull(init);
        Assert.Equal(305.0, init.Value.Start, 6);
        Assert.Equal(1u, init.Value.TrackId);
        Assert.Equal(10240u, init.Value.Timescale);
    }

    [Fact]
    public void AJobFromTheStartBeganAtTheStart()
    {
        // A remux from 0:00. The film's video itself starts 23 ms in.
        var init = VideoJob.ReadInit(Fixture("init-10.10-remux-0s.mp4"));
        Assert.NotNull(init);
        Assert.Equal(0.023, init.Value.Start, 6);
        Assert.Equal(16000u, init.Value.Timescale);
    }

    [Fact]
    public void NoEmptyEditMeansZero()
    {
        // Jellyfin 12.1 keeps the film's time in its pieces; its edit list only trims the first frame's delay.
        Assert.Equal(0, VideoJob.ReadInit(Init(edits: [(0, 1280)]))!.Value.Start);
        Assert.Equal(0, VideoJob.ReadInit(Init(edits: null))!.Value.Start);
    }

    [Fact]
    public void LeadingEmptyEditsAddUpInEitherVersion()
    {
        Assert.Equal(12.5, VideoJob.ReadInit(Init(edits: [(10_000, -1), (2_500, -1), (0, 0), (9_000, -1)], wideEdits: true))!.Value.Start, 6);
        Assert.Equal(7.25, VideoJob.ReadInit(Init(edits: [(7_250, -1), (0, 0)]))!.Value.Start, 6);
    }

    [Fact]
    public void TheVideoTrackIsTheOneRead()
    {
        var init = VideoJob.ReadInit(Init(edits: [(3_000, -1)], audioFirst: true));
        Assert.Equal((3.0, 7u, 90_000u), init!.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(64)]
    public void AnythingElseIsNotAnInitPiece(int length) => Assert.Null(VideoJob.ReadInit(new byte[length]));

    [Fact]
    public void APieceIsNotAnInitPiece() => Assert.Null(VideoJob.ReadInit(Piece(7, 90_000, 0)));

    // — How far it has got: its pieces —

    [Fact]
    public void APiecesTimeIsItsVideoTracksDecodeTime()
    {
        Assert.Equal(3.0, VideoJob.PieceTime(Piece(7, 90_000, 270_000), 7, 90_000));
        Assert.Equal(40_000.0, VideoJob.PieceTime(Piece(7, 90_000, 3_600_000_000, wide: true), 7, 90_000));
    }

    [Fact]
    public void APieceStillBeingWrittenHasNoTime()
    {
        var piece = Piece(7, 90_000, 270_000);
        Assert.Null(VideoJob.PieceTime(piece.AsSpan(0, 40), 7, 90_000));
        Assert.Null(VideoJob.PieceTime([], 7, 90_000));
        Assert.Null(VideoJob.PieceTime(piece, 8, 90_000));
    }

    // — Which job gives a subtitle piece's video —

    [Theory]
    [InlineData(300, 306, true)]    // runs into the job's start: the cues before it have no video
    [InlineData(294, 300, false)]   // all before it: a new job, or an earlier one
    [InlineData(306, 312, true)]
    [InlineData(338, 344, true)]    // within the lead Jellyfin gives the job
    [InlineData(345, 351, false)]   // beyond: Jellyfin starts a new job for that video
    public void AJobGivesThePiecesFromItsStartToJustPastWhereItHasGot(long start, long end, bool gives) =>
        Assert.Equal(gives, new VideoJob(305, 320).Gives(start * Second, end * Second));

    // — The transcode folder —

    [Fact]
    public void TheFolderSaysWhereTheJobBeganAndHowFarItHasGot()
    {
        using var folder = new Folder();
        var name = "0f3c9d0b1e7a4c2d9e8f7a6b5c4d3e2f";
        var playlist = Path.Combine(folder.Path, name + ".m3u8");
        var now = DateTime.UtcNow;

        // An earlier job's piece, left behind: older than this job's init piece, whatever its clock says.
        folder.Write(name + "50.mp4", Piece(1, 10240, 10240 * 900), now.AddMinutes(-3));
        folder.Write(name + "-1.mp4", Fixture("init-10.10-transcode-306s.mp4"), now.AddSeconds(-20));
        folder.Write(name + "102.mp4", Piece(1, 10240, 0), now.AddSeconds(-19));
        folder.Write(name + "103.mp4", Piece(1, 10240, 10240 * 3), now.AddSeconds(-18));
        folder.Write(name + "104.mp4", [], now.AddSeconds(-17));                       // being written
        folder.Write("aaaa" + name[4..] + "105.mp4", Piece(1, 10240, 10240 * 60), now); // another play session's

        Assert.Equal(new VideoJob(305, 308), VideoJob.Read(playlist));
    }

    [Fact]
    public void AJobWithNoFinishedPieceHasGotNowhereYet()
    {
        using var folder = new Folder();
        var playlist = Path.Combine(folder.Path, "abc.m3u8");
        folder.Write("abc-1.mp4", Fixture("init-10.10-transcode-306s.mp4"), DateTime.UtcNow);
        Assert.Equal(new VideoJob(305, 305), VideoJob.Read(playlist));
    }

    [Fact]
    public void NoInitPieceNoJob()
    {
        using var folder = new Folder();
        folder.Write("abc102.mp4", Piece(1, 10240, 0), DateTime.UtcNow);
        Assert.Null(VideoJob.Read(Path.Combine(folder.Path, "abc.m3u8")));
        Assert.Null(VideoJob.Read(Path.Combine(folder.Path, "missing", "abc.m3u8")));
    }

    // — The timing line it gives —

    [Theory]
    [InlineData(305.0, "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:05:05.000")]
    [InlineData(0.023, "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.023")]
    [InlineData(3725.5, "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:01:02:05.500")]
    [InlineData(360_000.0, "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:100:00:00.000")]
    [InlineData(0.0, "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000")]
    [InlineData(-0.08, "X-TIMESTAMP-MAP=MPEGTS:7200,LOCAL:00:00:00.000")]
    public void FmpFourVideoBegunLaterPutsTheCuesThere(double start, string line) =>
        Assert.Equal(line, HlsSubtitles.TimestampMap("mp4", start));

    [Fact]
    public void TransportStreamsKeepTheFilmsTimeWhereverTheJobBegan() =>
        Assert.Equal("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000", HlsSubtitles.TimestampMap("ts", 305));

    [Fact]
    public void APieceFromAJobBegunMidFilm()
    {
        const string Piece = "WEBVTT\n\n00:05:07.170 --> 00:05:09.360\nAnd here it is\n\n";
        Assert.Equal(
            "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:05:05.000\n\n00:05:07.170 --> 00:05:09.360\nAnd here it is\n\n",
            HlsSubtitles.SetTimestampMap(Piece, "mp4", 305));
    }

    // — Building boxes —

    private static byte[] Init(IReadOnlyList<(long Duration, long MediaTime)>? edits, bool wideEdits = false, bool audioFirst = false)
    {
        var mvhd = FullBox("mvhd", 0, U32(0), U32(0), U32(1000), U32(0), new byte[80]);
        var video = Box("trak",
            FullBox("tkhd", 0, U32(0), U32(0), U32(7), U32(0), U32(0), new byte[60]),
            edits is null ? [] : Box("edts", Elst(edits, wideEdits)),
            Box("mdia",
                FullBox("mdhd", 0, U32(0), U32(0), U32(90_000), U32(0), U32(0)),
                FullBox("hdlr", 0, U32(0), Encoding.ASCII.GetBytes("vide"), new byte[12], [0])));
        var audio = Box("trak",
            FullBox("tkhd", 0, U32(0), U32(0), U32(2), U32(0), U32(0), new byte[60]),
            Box("edts", Elst([(99_000, -1)], false)),
            Box("mdia",
                FullBox("mdhd", 0, U32(0), U32(0), U32(48_000), U32(0), U32(0)),
                FullBox("hdlr", 0, U32(0), Encoding.ASCII.GetBytes("soun"), new byte[12], [0])));
        return [.. Box("ftyp", Encoding.ASCII.GetBytes("iso5"), U32(512)), .. Box("moov", mvhd, audioFirst ? audio : video, audioFirst ? video : audio)];
    }

    private static byte[] Elst(IReadOnlyList<(long Duration, long MediaTime)> edits, bool wide)
    {
        var entries = edits.SelectMany(e => wide
            ? [.. U64((ulong)e.Duration), .. I64(e.MediaTime), .. U32(0x10000)]
            : (byte[])[.. U32((uint)e.Duration), .. I32((int)e.MediaTime), .. U32(0x10000)]).ToArray();
        return FullBox("elst", (byte)(wide ? 1 : 0), U32((uint)edits.Count), entries);
    }

    /// <summary>A piece: its index, the audio's fragment, then the video's, then (cut short) its media.</summary>
    private static byte[] Piece(uint videoTrack, uint timescale, ulong decodeTime, bool wide = false)
    {
        var audio = Box("traf", FullBox("tfhd", 0, U32(2)), FullBox("tfdt", 0, U32(12_345)), FullBox("trun", 0, U32(0)));
        var video = Box("traf", FullBox("tfhd", 0, U32(videoTrack)),
            wide ? FullBox("tfdt", 1, U64(decodeTime)) : FullBox("tfdt", 0, U32((uint)decodeTime)), FullBox("trun", 0, U32(0)));
        return [.. FullBox("sidx", 0, U32(videoTrack), U32(timescale), U32(0), U32(0)), .. Box("moof", FullBox("mfhd", 0, U32(1)), audio, video),
            .. U32(1_000_000), .. Encoding.ASCII.GetBytes("mdat"), .. new byte[64]];
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U32((uint)(8 + body.Length)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    private static byte[] FullBox(string type, byte version, params byte[][] parts) => Box(type, [[version, 0, 0, 0], .. parts]);

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] I32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] I64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("jmc-videojob-").FullName;

        public void Write(string name, byte[] content, DateTime writtenUtc)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(file, content);
            File.SetLastWriteTimeUtc(file, writtenUtc);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
