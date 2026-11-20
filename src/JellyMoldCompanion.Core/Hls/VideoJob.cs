using System.Buffers.Binary;
using System.Text;

namespace JellyMoldCompanion.Hls;

/// <summary>
/// One of Jellyfin 10.10's fMP4 video jobs, as the files it writes in Jellyfin's transcode folder describe it: where in
/// the film it began, and how far it has got (design §19 #20).
///
/// Jellyfin restarts its video job whenever a player asks for a piece far from the one being written — a resume, a jump
/// — and the new job begins at the keyframe before that piece. On 10.10 the new job's clock then reads 0 there, not at
/// the start of the film (Jellyfin 12 keeps the film's own time, with <c>frag_discont</c>). Subtitle cues carry the
/// film's time, so a subtitle piece's timing line has to say where the job began, or every line shows late by that
/// much. The job writes it down itself: an empty edit at the head of its video track, in its init piece, exact to the
/// keyframe — 305.000 s for a job asked to begin at 306 s (measured on 10.10.7, 2026-11-19).
/// </summary>
/// <param name="Start">The time in the film, in seconds, at which the job's clock reads zero.</param>
/// <param name="Reached">The time in the film, in seconds, at which the job's newest finished piece begins.</param>
public sealed record VideoJob(double Start, double Reached)
{
    /// <summary>How far past its newest piece Jellyfin keeps a job going for a player's request: further than this
    /// (24 seconds' worth of pieces, in Jellyfin's <c>GetDynamicSegment</c>) and it starts a new one.</summary>
    public const double Lead = 24;

    /// <summary>
    /// Whether this job gives the video for a subtitle piece: the piece ends after the job began and starts no further
    /// ahead than the job carries on to. Otherwise a new job is on its way, or the video comes from another.
    /// </summary>
    public bool Gives(long startTicks, long endTicks) =>
        endTicks / (double)HlsSubtitles.TicksPerSecond > Start
        && startTicks / (double)HlsSubtitles.TicksPerSecond <= Reached + Lead;

    /// <summary>
    /// The job writing to <paramref name="playlistPath"/> (Jellyfin's <c>{folder}/{name}.m3u8</c>): where it began, from
    /// its init piece <c>{name}-1.mp4</c>, and how far it has got, from the newest of its pieces <c>{name}{n}.mp4</c>
    /// written since. Null while there is no init piece to read, or it cannot be read.
    /// </summary>
    public static VideoJob? Read(string playlistPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(playlistPath);
            var name = Path.GetFileNameWithoutExtension(playlistPath);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var initFile = new FileInfo(Path.Combine(folder, name + "-1.mp4"));
            if (!initFile.Exists || ReadInit(ReadHead(initFile.FullName, 1 << 20)) is not { } track)
            {
                return null;
            }

            // Pieces an earlier job left behind are older than this job's init piece, which a job writes before its first
            // piece. The newest piece is usually still being written, and has nothing to read yet.
            var since = initFile.LastWriteTimeUtc.AddMilliseconds(-50);
            var reached = track.Start;
            var newest = new DirectoryInfo(folder).EnumerateFiles(name + "*.mp4")
                .Where(file => IsPiece(file.Name, name) && file.LastWriteTimeUtc >= since)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(4);
            foreach (var piece in newest)
            {
                if (PieceTime(ReadHead(piece.FullName, 1 << 18), track.TrackId, track.Timescale) is { } time)
                {
                    reached = track.Start + time;
                    break;
                }
            }

            return new VideoJob(track.Start, reached);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// From an fMP4 init piece: where the video track's clock reads zero in the film (its leading empty edits, in the
    /// movie's timescale; 0 when it has none), and the track's id and timescale for reading its pieces. Null for anything
    /// that is not an init piece with a video track.
    /// </summary>
    public static (double Start, uint TrackId, uint Timescale)? ReadInit(ReadOnlySpan<byte> init)
    {
        var moov = Child(init, "moov");
        if (moov.IsEmpty)
        {
            return null;
        }

        var mvhd = Child(moov, "mvhd");
        if (mvhd.Length < 24)
        {
            return null;
        }

        var movieScale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[(mvhd[0] == 1 ? 20 : 12)..]);
        foreach (var (type, trak) in Children(moov))
        {
            if (type != "trak")
            {
                continue;
            }

            var box = moov.Slice(trak.Start, trak.Length);
            var mdia = Child(box, "mdia");
            var hdlr = Child(mdia, "hdlr");
            if (hdlr.Length < 12 || Encoding.ASCII.GetString(hdlr.Slice(8, 4)) != "vide")
            {
                continue;
            }

            var tkhd = Child(box, "tkhd");
            var mdhd = Child(mdia, "mdhd");
            if (tkhd.Length < 24 || mdhd.Length < 24 || movieScale == 0)
            {
                return null;
            }

            var trackId = BinaryPrimitives.ReadUInt32BigEndian(tkhd[(tkhd[0] == 1 ? 20 : 12)..]);
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
            if (timescale == 0)
            {
                return null;
            }

            return (EmptyEdits(Child(Child(box, "edts"), "elst")) / (double)movieScale, trackId, timescale);
        }

        return null;
    }

    /// <summary>
    /// From an fMP4 piece: the time on the job's clock, in seconds, at which the track's samples in it begin (its
    /// <c>tfdt</c>). Null for anything without a finished <c>moof</c> for that track.
    /// </summary>
    public static double? PieceTime(ReadOnlySpan<byte> piece, uint trackId, uint timescale)
    {
        foreach (var (type, moof) in Children(piece))
        {
            if (type != "moof")
            {
                continue;
            }

            var fragment = piece.Slice(moof.Start, moof.Length);
            foreach (var (kind, traf) in Children(fragment))
            {
                if (kind != "traf")
                {
                    continue;
                }

                var box = fragment.Slice(traf.Start, traf.Length);
                var tfhd = Child(box, "tfhd");
                var tfdt = Child(box, "tfdt");
                if (tfhd.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(tfhd[4..]) != trackId)
                {
                    continue;
                }

                if (tfdt.Length < (tfdt.IsEmpty || tfdt[0] != 1 ? 8 : 12))
                {
                    return null;
                }

                var decodeTime = tfdt[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tfdt[4..]);
                return decodeTime / (double)timescale;
            }
        }

        return null;
    }

    /// <summary>The length of the empty edits an edit list opens with (<c>media_time</c> −1), in the movie's timescale.</summary>
    private static long EmptyEdits(ReadOnlySpan<byte> elst)
    {
        if (elst.Length < 8)
        {
            return 0;
        }

        var wide = elst[0] == 1;
        var entry = wide ? 20 : 12;
        var count = BinaryPrimitives.ReadUInt32BigEndian(elst[4..]);
        long empty = 0;
        for (var i = 0; i < count && 8 + ((i + 1) * entry) <= elst.Length; i++)
        {
            var at = elst[(8 + (i * entry))..];
            var duration = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);
            var mediaTime = wide ? BinaryPrimitives.ReadInt64BigEndian(at[8..]) : BinaryPrimitives.ReadInt32BigEndian(at[4..]);
            if (mediaTime != -1)
            {
                break;
            }

            empty += duration;
        }

        return empty;
    }

    /// <summary>The body of the first child box of that type, or nothing.</summary>
    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> parent, string type)
    {
        foreach (var (kind, body) in Children(parent))
        {
            if (kind == type)
            {
                return parent.Slice(body.Start, body.Length);
            }
        }

        return ReadOnlySpan<byte>.Empty;
    }

    /// <summary>The boxes directly inside <paramref name="parent"/>, as (type, body), up to the first one cut short.</summary>
    private static List<(string Type, (int Start, int Length) Body)> Children(ReadOnlySpan<byte> parent)
    {
        var boxes = new List<(string, (int, int))>();
        var at = 0;
        while (at + 8 <= parent.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(parent[at..]);
            var header = 8;
            if (size == 1)
            {
                if (at + 16 > parent.Length)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(parent[(at + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = parent.Length - at;
            }

            if (size < header || at + size > parent.Length)
            {
                break;
            }

            boxes.Add((Encoding.ASCII.GetString(parent.Slice(at + 4, 4)), (at + header, (int)size - header)));
            at += (int)size;
        }

        return boxes;
    }

    /// <summary>A piece's file name: the playlist's name, its number, <c>.mp4</c>. Not the init piece, <c>{name}-1.mp4</c>.</summary>
    private static bool IsPiece(string fileName, string name) =>
        fileName.Length > name.Length + 4
        && fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && fileName.AsSpan(name.Length, fileName.Length - name.Length - 4).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The start of a file, up to <paramref name="limit"/> bytes: an init piece, or a piece's boxes before its media.</summary>
    private static byte[] ReadHead(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(limit, stream.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }
}
