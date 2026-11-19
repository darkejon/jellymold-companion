using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JellyMoldCompanion.Hls;

/// <summary>
/// Subtitles that keep time with Jellyfin's HLS video (design §16 idea 14), for players that fetch every address
/// themselves — CarPlay, Apple's HomeView, an Apple TV under AirPlay — and draw Apple's subtitles, not Jellyfin's.
///
/// Jellyfin's own subtitle pieces all open with <c>X-TIMESTAMP-MAP=MPEGTS:900000</c>, which is right for MPEG-TS video
/// and ten seconds out for the fMP4 video it sends Apple's players, so every line shows about ten seconds late; and its
/// master playlist cuts them into 30-second pieces. Jellyfin's fix (PR #17299, not in a release yet, and never for 10.10)
/// sets the value by the video's segment container. The plugin serves Jellyfin's own master playlist with only the
/// subtitle playlists swapped for its own, which cut 6-second pieces and set the value the same way.
///
/// Addresses are kept relative, so they hold behind a base URL or a reverse proxy. Everything here is text in and text
/// out; the Jellyfin side is in the plugin.
/// </summary>
public static class HlsSubtitles
{
    /// <summary>Length of the plugin's subtitle pieces: lines appear sooner and settle faster after a scrub (measured
    /// in Apple's CarPlay Simulator, 2026-11-19).</summary>
    public const int PieceSeconds = 6;

    public const long TicksPerSecond = 10_000_000;

    /// <summary>MPEG-TS video from Jellyfin starts at ten seconds (90 kHz clock); fMP4 video starts at zero.</summary>
    public const long MpegTsOffset = 900_000;

    /// <summary>The plugin's master playlist, at the address an app gives: Jellyfin's <c>/videos/</c> swapped for this.</summary>
    public const string MasterPrefix = "/JellyMold/v1/Videos/";

    /// <summary>From the plugin's master (<c>…/JellyMold/v1/Videos/{itemId}/</c>) back up to the server's root, base URL
    /// and all: four levels. Jellyfin's own addresses are written relative to its master, <c>…/videos/{itemId}/</c>.</summary>
    private const string UpToServerRoot = "../../../../";

    private static readonly Regex UriAttribute = new("URI=\"([^\"]*)\"", RegexOptions.CultureInvariant);

    private static readonly Regex JellyfinSubtitlePlaylist =
        new(@"^(?<source>[^/?#]+)/Subtitles/(?<index>\d+)/subtitles\.m3u8$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// For a request to the plugin's master playlist, the same request's path as Jellyfin's own master: everything before
    /// <c>/JellyMold/v1/Videos/</c> (a base URL) kept, the rest Jellyfin's <c>/videos/{itemId}/master.m3u8</c>. Null for
    /// any other path, including an item id that is not a GUID.
    /// </summary>
    public static (string JellyfinPath, string ItemId)? MasterRequest(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var at = path.IndexOf(MasterPrefix, StringComparison.OrdinalIgnoreCase);
        const string Master = "/master.m3u8";
        if (at < 0 || !path.EndsWith(Master, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var itemStart = at + MasterPrefix.Length;
        var itemLength = path.Length - Master.Length - itemStart;
        if (itemLength <= 0)
        {
            return null;
        }

        var itemId = path.Substring(itemStart, itemLength);
        if (!Guid.TryParse(itemId, out _))
        {
            return null;
        }

        return (path[..at] + "/videos/" + itemId + Master, itemId);
    }

    /// <summary>
    /// Jellyfin's master playlist as the plugin serves it: unchanged except for its addresses. Each subtitle rendition
    /// (<c>EXT-X-MEDIA:TYPE=SUBTITLES</c>) points at the plugin's subtitle playlist, with the token Jellyfin wrote there
    /// and the video's segment container; every other relative address (the video playlists, trickplay) points back at
    /// Jellyfin's own, as it resolved from Jellyfin's master. Absolute addresses are left alone.
    /// </summary>
    /// <param name="master">Jellyfin's master playlist.</param>
    /// <param name="itemId">The item id as it appears in the request's path.</param>
    /// <param name="requestedContainer">The request's <c>SegmentContainer</c>, used when the video lines carry none.</param>
    public static string RewriteMaster(string master, string itemId, string? requestedContainer)
    {
        var jellyfin = UpToServerRoot + "videos/" + itemId + "/";
        var container = VideoContainer(master) ?? requestedContainer;
        var lines = master.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var ending = lines[i].Length > line.Length ? "\r" : string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                var subtitles = line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)
                    && line.Contains("TYPE=SUBTITLES", StringComparison.Ordinal);
                lines[i] = UriAttribute.Replace(line, match =>
                {
                    var uri = match.Groups[1].Value;
                    var rewritten = subtitles ? SubtitlePlaylistAddress(uri, container) : null;
                    return "URI=\"" + (rewritten ?? Repoint(uri, jellyfin)) + "\"";
                }) + ending;
            }
            else
            {
                lines[i] = Repoint(line, jellyfin) + ending;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The plugin's subtitle playlist: <paramref name="runtimeTicks"/> cut into 6-second pieces (the last one shorter),
    /// each asked for at <c>stream.vtt</c> beside it with its start and end and <paramref name="pieceQuery"/> (the
    /// segment container and the token). Video on demand: the whole list at once, closed.
    /// </summary>
    public static string SubtitlePlaylist(long runtimeTicks, string pieceQuery)
    {
        if (runtimeTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeTicks), "Subtitles in pieces need a running time.");
        }

        const long PieceTicks = PieceSeconds * TicksPerSecond;
        var builder = new StringBuilder()
            .Append("#EXTM3U\n")
            .Append("#EXT-X-TARGETDURATION:").Append(PieceSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("#EXT-X-VERSION:3\n")
            .Append("#EXT-X-MEDIA-SEQUENCE:0\n")
            .Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        for (long start = 0; start < runtimeTicks; start += PieceTicks)
        {
            var end = Math.Min(runtimeTicks, start + PieceTicks);
            builder.Append("#EXTINF:")
                .Append(((end - start) / (double)TicksPerSecond).ToString("0.000", CultureInfo.InvariantCulture))
                .Append(",\n")
                .Append("stream.vtt?StartPositionTicks=").Append(start.ToString(CultureInfo.InvariantCulture))
                .Append("&EndPositionTicks=").Append(end.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(pieceQuery))
            {
                builder.Append('&').Append(pieceQuery);
            }

            builder.Append('\n');
        }

        return builder.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <summary>The query every piece carries: the segment container, then the token. Values escaped.</summary>
    public static string PieceQuery(string? segmentContainer, string? token)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(segmentContainer))
        {
            parts.Add("SegmentContainer=" + Uri.EscapeDataString(segmentContainer));
        }

        if (!string.IsNullOrEmpty(token))
        {
            parts.Add("ApiKey=" + Uri.EscapeDataString(token));
        }

        return string.Join('&', parts);
    }

    /// <summary>
    /// The <c>X-TIMESTAMP-MAP</c> a piece should carry for video in <paramref name="segmentContainer"/>: <c>MPEGTS:0</c>
    /// for fMP4 (<c>mp4</c>), <c>MPEGTS:900000</c> for anything else, both at <c>LOCAL:00:00:00.000</c>, as Jellyfin's
    /// own fix does.
    /// </summary>
    public static string TimestampMap(string? segmentContainer) =>
        "X-TIMESTAMP-MAP=MPEGTS:" + Offset(segmentContainer).ToString(CultureInfo.InvariantCulture) + ",LOCAL:00:00:00.000";

    public static long Offset(string? segmentContainer) =>
        string.Equals(segmentContainer?.Trim(), "mp4", StringComparison.OrdinalIgnoreCase) ? 0 : MpegTsOffset;

    /// <summary>
    /// A WebVTT piece with its timing line set for <paramref name="segmentContainer"/>: any <c>X-TIMESTAMP-MAP</c> already
    /// in its header is taken out and the right one written straight after the <c>WEBVTT</c> line. Set rather than
    /// adjusted, so it stays right whatever Jellyfin wrote. The cues are untouched.
    /// </summary>
    public static string SetTimestampMap(string vtt, string? segmentContainer)
    {
        var text = vtt.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            lines.Insert(0, "WEBVTT");
            if (lines.Count > 1 && lines[1].Length > 0)
            {
                lines.Insert(1, string.Empty);
            }
        }

        // The header runs to the first blank line; only there can a timing line be.
        var headerEnd = lines.FindIndex(1, line => line.Length == 0);
        if (headerEnd < 0)
        {
            headerEnd = lines.Count;
        }

        for (var i = headerEnd - 1; i >= 1; i--)
        {
            if (lines[i].StartsWith("X-TIMESTAMP-MAP", StringComparison.Ordinal))
            {
                lines.RemoveAt(i);
            }
        }

        lines.Insert(1, TimestampMap(segmentContainer));
        return string.Join('\n', lines);
    }

    /// <summary>The <c>SegmentContainer</c> of the first video playlist the master points at, if it names one.</summary>
    private static string? VideoContainer(string master)
    {
        foreach (var raw in master.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return QueryValue(line, "SegmentContainer");
            }
        }

        return null;
    }

    /// <summary>
    /// The plugin's subtitle playlist for one of Jellyfin's (<c>{mediaSourceId}/Subtitles/{index}/subtitles.m3u8?…</c>):
    /// the same relative path, which from the plugin's master reaches the plugin, with the container and Jellyfin's token.
    /// Null for an address of any other shape, which then keeps pointing at Jellyfin's own.
    /// </summary>
    private static string? SubtitlePlaylistAddress(string uri, string? container)
    {
        var query = uri.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? uri : uri[..query];
        if (!JellyfinSubtitlePlaylist.IsMatch(path))
        {
            return null;
        }

        var token = QueryValue(uri, "ApiKey") ?? QueryValue(uri, "api_key");
        var pieceQuery = PieceQuery(container, token);
        return pieceQuery.Length == 0 ? path : path + "?" + pieceQuery;
    }

    /// <summary>A relative address from Jellyfin's master, made to reach the same place from the plugin's.</summary>
    private static string Repoint(string uri, string jellyfin) => IsRelative(uri) ? jellyfin + uri : uri;

    private static bool IsRelative(string uri) =>
        !uri.StartsWith('/') && !Regex.IsMatch(uri, "^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant);

    private static string? QueryValue(string uri, string name)
    {
        var query = uri.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            return null;
        }

        foreach (var pair in uri[(query + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? pair : pair[..equals];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
            {
                var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}
