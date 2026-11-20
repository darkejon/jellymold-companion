using System.Text.RegularExpressions;
using JellyMoldCompanion.Hls;

namespace JellyMoldCompanion.Tests;

/// <summary>
/// Design §16 idea 14: Jellyfin's HLS master served with the plugin's subtitle playlists — 6-second pieces whose timing
/// line suits the video's segment container — and every other address still reaching Jellyfin's own.
/// </summary>
public class HlsSubtitlesTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string ItemId = "d857cf18-e0bb-dd8c-9861-cc749e2bd0b9";
    private const string Source = "d857cf18e0bbdd8c9861cc749e2bd0b9";

    private const string VideoQuery =
        "DeviceId=car&MediaSourceId=" + Source + "&VideoCodec=hevc,h264&AudioCodec=copy&SubtitleStreamIndex=4"
        + "&SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey=" + Token + "&SubtitleMethod=Hls";

    // Jellyfin 12.1's master for Planet Earth with the AirPlay profile (2026-11-19), the token replaced, plus the
    // trickplay line Jellyfin adds when the item has trickplay images.
    private const string Master12 =
        "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"ITA Completi - Italian - ASS\",DEFAULT=NO,FORCED=NO,AUTOSELECT=YES,URI=\"" + Source + "/Subtitles/3/subtitles.m3u8?SegmentLength=30&ApiKey=" + Token + "\",LANGUAGE=\"ita\"\n"
        + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"ENG Full - English - ASS\",DEFAULT=YES,FORCED=NO,AUTOSELECT=YES,URI=\"" + Source + "/Subtitles/4/subtitles.m3u8?SegmentLength=30&ApiKey=" + Token + "\",LANGUAGE=\"eng\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=6901917,AVERAGE-BANDWIDTH=6901917,VIDEO-RANGE=SDR,CODECS=\"hvc1.1.4.L120.B0,ac-3\",RESOLUTION=1920x1080,FRAME-RATE=25,SUBTITLES=\"subs\"\n"
        + "main.m3u8?" + VideoQuery + "\n"
        + "#EXT-X-IMAGE-STREAM-INF:BANDWIDTH=12819,RESOLUTION=320x180,CODECS=\"jpeg\",URI=\"Trickplay/320/tiles.m3u8?MediaSourceId=" + Source + "&ApiKey=" + Token + "\"\n";

    private static readonly Uri PluginMaster = new($"https://example.org/jellyfin/JellyMold/v1/Videos/{ItemId}/master.m3u8?{VideoQuery}");

    private static string[] Lines(string text) => text.Split('\n');

    private static IEnumerable<string> SubtitleUris(string master) =>
        Regex.Matches(master, "TYPE=SUBTITLES[^\n]*URI=\"([^\"]+)\"").Select(m => m.Groups[1].Value);

    // — Which requests are the plugin's master —

    [Theory]
    [InlineData("/JellyMold/v1/Videos/" + ItemId + "/master.m3u8", "/videos/" + ItemId + "/master.m3u8")]
    [InlineData("/jellyfin/JellyMold/v1/Videos/" + ItemId + "/master.m3u8", "/jellyfin/videos/" + ItemId + "/master.m3u8")]
    [InlineData("/jellymold/V1/videos/" + Source + "/MASTER.m3u8", "/videos/" + Source + "/master.m3u8")]
    public void TheMastersAddressBecomesJellyfinsOwnBaseUrlAndAll(string path, string jellyfin)
    {
        var request = HlsSubtitles.MasterRequest(path);
        Assert.NotNull(request);
        Assert.Equal(jellyfin, request.Value.JellyfinPath, ignoreCase: true);
    }

    [Theory]
    [InlineData("/videos/" + ItemId + "/master.m3u8")]
    [InlineData("/JellyMold/v1/Videos/" + ItemId + "/main.m3u8")]
    [InlineData("/JellyMold/v1/Videos/not-an-item/master.m3u8")]
    [InlineData("/JellyMold/v1/Videos//master.m3u8")]
    [InlineData("/JellyMold/v1/Videos/" + ItemId + "/" + Source + "/Subtitles/4/subtitles.m3u8")]
    [InlineData("/JellyMold/v1/Info")]
    [InlineData(null)]
    public void NothingElseIsTouched(string? path) => Assert.Null(HlsSubtitles.MasterRequest(path));

    // — The master —

    [Fact]
    public void SubtitleRenditionsPointAtThePluginsPlaylistWithTheContainerPlaySessionAndToken()
    {
        var uris = SubtitleUris(HlsSubtitles.RewriteMaster(Master12, ItemId, null)).ToList();
        Assert.Equal(
            [
                $"{Source}/Subtitles/3/subtitles.m3u8?SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}",
                $"{Source}/Subtitles/4/subtitles.m3u8?SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}",
            ],
            uris);
    }

    [Fact]
    public void ThePlaySessionComesFromTheVideoLineThenTheRequest()
    {
        // A piece finds its video job by it (design §19 #20).
        var noneNamed = Master12.Replace("&PlaySessionId=7bd2", string.Empty, StringComparison.Ordinal);
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(noneNamed, ItemId, null, "e41f")), uri => Assert.Contains("&PlaySessionId=e41f&", uri, StringComparison.Ordinal));
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(noneNamed, ItemId, null)), uri => Assert.DoesNotContain("PlaySessionId", uri, StringComparison.Ordinal));
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(Master12, ItemId, null, "e41f")), uri => Assert.Contains("&PlaySessionId=7bd2&", uri, StringComparison.Ordinal));
    }

    [Fact]
    public void FromThePluginsMasterTheyReachThePluginBehindABaseUrl()
    {
        var uri = SubtitleUris(HlsSubtitles.RewriteMaster(Master12, ItemId, null)).Last();
        Assert.Equal(
            $"https://example.org/jellyfin/JellyMold/v1/Videos/{ItemId}/{Source}/Subtitles/4/subtitles.m3u8?SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}",
            new Uri(PluginMaster, uri).ToString());
    }

    [Fact]
    public void TheVideoPlaylistStillReachesJellyfinsOwnMainPlaylistUntouched()
    {
        var video = Lines(HlsSubtitles.RewriteMaster(Master12, ItemId, null)).Single(l => l.Contains("main.m3u8"));
        Assert.Equal($"../../../../videos/{ItemId}/main.m3u8?{VideoQuery}", video);
        Assert.Equal($"https://example.org/jellyfin/videos/{ItemId}/main.m3u8?{VideoQuery}", new Uri(PluginMaster, video).ToString());
    }

    [Fact]
    public void TrickplayStillReachesJellyfin()
    {
        var line = Lines(HlsSubtitles.RewriteMaster(Master12, ItemId, null)).Single(l => l.StartsWith("#EXT-X-IMAGE-STREAM-INF"));
        var uri = Regex.Match(line, "URI=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal($"https://example.org/jellyfin/videos/{ItemId}/Trickplay/320/tiles.m3u8?MediaSourceId={Source}&ApiKey={Token}",
            new Uri(PluginMaster, uri).ToString());
    }

    [Fact]
    public void EverythingButTheAddressesIsJellyfinsOwn()
    {
        var before = Lines(Master12);
        var after = Lines(HlsSubtitles.RewriteMaster(Master12, ItemId, null));
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].StartsWith('#') || before[i].Length == 0)
            {
                // Tags: every attribute but URI as Jellyfin wrote it.
                Assert.Equal(Regex.Replace(before[i], "URI=\"[^\"]*\"", "URI"), Regex.Replace(after[i], "URI=\"[^\"]*\"", "URI"));
            }
            else
            {
                // A playlist's address: Jellyfin's, with only the way back to it in front.
                Assert.Equal($"../../../../videos/{ItemId}/{before[i]}", after[i]);
            }
        }
    }

    [Fact]
    public void TenTenTokensAreWrittenAsApiKeyWhichBothVersionsRead()
    {
        // Jellyfin 10.10.7 writes the lowercase api_key; 12 refuses it unless legacy sign-in is on.
        var master = Master12.Replace("ApiKey=", "api_key=", StringComparison.Ordinal);
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(master, ItemId, null)), uri =>
        {
            Assert.Contains("&ApiKey=" + Token, uri, StringComparison.Ordinal);
            Assert.DoesNotContain("api_key", uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheContainerComesFromTheVideoLineThenTheRequest()
    {
        var transportStream = Master12.Replace("SegmentContainer=mp4", "SegmentContainer=ts", StringComparison.Ordinal);
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(transportStream, ItemId, "mp4")), uri => Assert.Contains("SegmentContainer=ts&", uri, StringComparison.Ordinal));

        var noneNamed = Master12.Replace("&SegmentContainer=mp4", string.Empty, StringComparison.Ordinal);
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(noneNamed, ItemId, "mp4")), uri => Assert.Contains("SegmentContainer=mp4&", uri, StringComparison.Ordinal));
        Assert.All(SubtitleUris(HlsSubtitles.RewriteMaster(noneNamed, ItemId, null)), uri => Assert.DoesNotContain("SegmentContainer", uri, StringComparison.Ordinal));
    }

    [Fact]
    public void AnAddressOfAnotherShapeKeepsReachingJellyfin()
    {
        // Should a later Jellyfin write its subtitle playlist elsewhere, its subtitles keep working, late, rather than
        // pointing at nothing.
        var master = Master12.Replace("/Subtitles/4/subtitles.m3u8", "/Subtitles/4/other.m3u8", StringComparison.Ordinal);
        var uri = SubtitleUris(HlsSubtitles.RewriteMaster(master, ItemId, null)).Last();
        Assert.Equal($"../../../../videos/{ItemId}/{Source}/Subtitles/4/other.m3u8?SegmentLength=30&ApiKey={Token}", uri);
    }

    [Fact]
    public void AbsoluteAddressesAndLineEndingsAreLeftAlone()
    {
        var master = "#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1\r\nhttps://cdn.example/x.m3u8\r\n#EXT-X-STREAM-INF:BANDWIDTH=2\r\n/videos/a/main.m3u8\r\n";
        Assert.Equal(master, HlsSubtitles.RewriteMaster(master, ItemId, null));
    }

    // — The subtitle playlist —

    [Fact]
    public void TheTrackIsCutIntoSixSecondPiecesTheLastOneShorter()
    {
        var runtime = 20 * HlsSubtitles.TicksPerSecond + 5_000_000;   // 20.5 s
        var playlist = HlsSubtitles.SubtitlePlaylist(runtime, HlsSubtitles.PieceQuery("mp4", "7bd2", Token));
        Assert.Equal(
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n"
            + $"#EXTINF:6.000,\nstream.vtt?StartPositionTicks=0&EndPositionTicks=60000000&SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}\n"
            + $"#EXTINF:6.000,\nstream.vtt?StartPositionTicks=60000000&EndPositionTicks=120000000&SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}\n"
            + $"#EXTINF:6.000,\nstream.vtt?StartPositionTicks=120000000&EndPositionTicks=180000000&SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}\n"
            + $"#EXTINF:2.500,\nstream.vtt?StartPositionTicks=180000000&EndPositionTicks=205000000&SegmentContainer=mp4&PlaySessionId=7bd2&ApiKey={Token}\n"
            + "#EXT-X-ENDLIST\n",
            playlist);
    }

    [Fact]
    public void AFilmsPiecesCoverItEndToEndWithoutGaps()
    {
        var runtime = 2L * 60 * 60 * HlsSubtitles.TicksPerSecond + 123_456_789;   // 2 h 0 min 12.3 s
        var lines = Lines(HlsSubtitles.SubtitlePlaylist(runtime, HlsSubtitles.PieceQuery("mp4", null, Token)));
        var pieces = lines.Where(l => l.StartsWith("stream.vtt")).ToList();
        Assert.Equal((int)Math.Ceiling(runtime / (6.0 * HlsSubtitles.TicksPerSecond)), pieces.Count);

        long expected = 0;
        foreach (var piece in pieces)
        {
            var match = Regex.Match(piece, "StartPositionTicks=(\\d+)&EndPositionTicks=(\\d+)");
            Assert.Equal(expected, long.Parse(match.Groups[1].Value));
            expected = long.Parse(match.Groups[2].Value);
        }

        Assert.Equal(runtime, expected);
        Assert.All(lines.Where(l => l.StartsWith("#EXTINF:")), l => Assert.True(double.Parse(l[8..^1], System.Globalization.CultureInfo.InvariantCulture) <= 6));
        Assert.Equal("#EXT-X-ENDLIST", lines[^2]);
    }

    [Fact]
    public void NoRunningTimeNoPieces() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HlsSubtitles.SubtitlePlaylist(0, string.Empty));

    [Fact]
    public void ThePiecesQueryIsEscapedAndSkipsWhatIsMissing()
    {
        Assert.Equal("SegmentContainer=mp4&PlaySessionId=p%20q&ApiKey=a%2Bb%26c", HlsSubtitles.PieceQuery("mp4", "p q", "a+b&c"));
        Assert.Equal("SegmentContainer=mp4&ApiKey=t", HlsSubtitles.PieceQuery("mp4", null, "t"));
        Assert.Equal("ApiKey=t", HlsSubtitles.PieceQuery(null, null, "t"));
        Assert.Equal(string.Empty, HlsSubtitles.PieceQuery(null, null, null));
    }

    // — Each piece's timing line —

    [Theory]
    [InlineData("mp4", 0)]
    [InlineData("MP4", 0)]
    [InlineData("ts", 900_000)]
    [InlineData(null, 900_000)]
    [InlineData("", 900_000)]
    public void TheTimingLineSuitsTheContainer(string? container, long mpegts) =>
        Assert.Equal($"X-TIMESTAMP-MAP=MPEGTS:{mpegts},LOCAL:00:00:00.000", HlsSubtitles.TimestampMap(container));

    [Fact]
    public void APieceFromJellyfinsEncoderGetsTheLineAfterItsHeader()
    {
        // What Jellyfin's subtitle encoder gives, times kept as they are in the film (CopyTimestamps).
        const string Piece = "WEBVTT\n\n00:00:39.170 --> 00:00:45.360\nA hundred years ago there were\n\n";
        Assert.Equal(
            "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n\n00:00:39.170 --> 00:00:45.360\nA hundred years ago there were\n\n",
            HlsSubtitles.SetTimestampMap(Piece, "mp4"));
    }

    [Fact]
    public void ALineAlreadyThereIsReplacedNotAdjusted()
    {
        // Jellyfin's own wrong line, or — once its fix ships — its right one: either way, one line, the right value.
        const string Piece = "WEBVTT\r\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\r\n\r\n00:00:01.000 --> 00:00:02.000\r\nHello\r\n";
        var set = HlsSubtitles.SetTimestampMap(Piece, "mp4");
        Assert.Equal("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n\n00:00:01.000 --> 00:00:02.000\nHello\n", set);
        Assert.Equal(set, HlsSubtitles.SetTimestampMap(set, "mp4"));
    }

    [Fact]
    public void ACueThatMentionsTheTimingLineIsLeftAlone()
    {
        const string Piece = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nX-TIMESTAMP-MAP is a line\n";
        Assert.EndsWith("\nX-TIMESTAMP-MAP is a line\n", HlsSubtitles.SetTimestampMap(Piece, "mp4"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPieceIsStillAPiece()
    {
        Assert.Equal("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n\n", HlsSubtitles.SetTimestampMap("﻿WEBVTT\n\n", "mp4"));
        Assert.Equal("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n", HlsSubtitles.SetTimestampMap(string.Empty, "ts"));
    }
}
