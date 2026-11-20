using System.ComponentModel.DataAnnotations;
using System.Text;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using JellyMoldCompanion.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The subtitle playlists the plugin's HLS master points at (design §16 idea 14, <see cref="HlsMasterPlaylist"/>):
/// 6-second pieces whose timing line is right for the video's segment container — and, on Jellyfin 10.10, for where the
/// session's fMP4 video job began (design §19 #20, <see cref="VideoJobClock"/>).
///
/// Asked for by players that send no header (CarPlay, HomeView, an Apple TV under AirPlay), so the caller is signed in
/// from the address's own <c>ApiKey</c>, as Jellyfin's HLS addresses are, and every address written here carries it on.
/// The item must be one the caller can see — the check Jellyfin makes on its own subtitle playlist, and here on every
/// piece too, where Jellyfin's pieces ask for no sign-in at all.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1/Videos/{itemId}/{mediaSourceId}/Subtitles/{index}")]
public sealed class HlsSubtitlesController : CompanionControllerBase
{
    /// <summary>The longest piece asked for: far more than the plugin's playlists ask, so any input stays bounded.</summary>
    private static readonly long MaxPieceTicks = TimeSpan.FromMinutes(10).Ticks;

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ISubtitleEncoder _subtitles;
    private readonly VideoJobClock _clock;

    public HlsSubtitlesController(ILibraryManager library, IMediaSourceManager mediaSources, ISubtitleEncoder subtitles, VideoJobClock clock)
    {
        _library = library;
        _mediaSources = mediaSources;
        _subtitles = subtitles;
        _clock = clock;
    }

    /// <summary>The subtitle track in 6-second pieces, each at <c>stream.vtt</c> beside this playlist.</summary>
    [HttpGet("subtitles.m3u8")]
    public async Task<ActionResult> Playlist(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string mediaSourceId,
        [FromRoute, Required, Range(0, 10_000)] int index,
        [FromQuery] string? segmentContainer,
        [FromQuery] string? playSessionId,
        CancellationToken cancellationToken)
    {
        var (_, source, refusal) = await Find(itemId, mediaSourceId, index, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (source!.RunTimeTicks is not > 0)
        {
            return Problem(StatusCodes.Status404NotFound, "This video has no running time, so its subtitles cannot be cut into pieces.");
        }

        var playlist = HlsSubtitles.SubtitlePlaylist(
            source.RunTimeTicks.Value, HlsSubtitles.PieceQuery(Container(segmentContainer), PlaySession(playSessionId), Token));
        return File(Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// One piece: the cues from its start to its end at their times in the film, with the timing line set — for fMP4 on
    /// Jellyfin 10.10, from where the play session's video job began.
    /// </summary>
    [HttpGet("stream.vtt")]
    public async Task<ActionResult> Piece(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string mediaSourceId,
        [FromRoute, Required, Range(0, 10_000)] int index,
        [FromQuery, Required, Range(0, long.MaxValue)] long startPositionTicks,
        [FromQuery, Required, Range(1, long.MaxValue)] long endPositionTicks,
        [FromQuery] string? segmentContainer,
        [FromQuery] string? playSessionId,
        CancellationToken cancellationToken)
    {
        if (endPositionTicks <= startPositionTicks || endPositionTicks - startPositionTicks > MaxPieceTicks)
        {
            return Problem(StatusCodes.Status400BadRequest, "A piece ends after it starts, and lasts ten minutes at most.");
        }

        var (item, _, refusal) = await Find(itemId, mediaSourceId, index, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        // Where the video's clock reads zero, found while the cues are read: it can wait on a jump's new video job.
        var container = Container(segmentContainer);
        var videoStart = string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase) && PlaySession(playSessionId) is { } session
            ? _clock.StartFor(session, mediaSourceId, startPositionTicks, endPositionTicks, cancellationToken)
            : Task.FromResult<double?>(null);

        // Times kept as they are in the film (Jellyfin's CopyTimestamps), which the timing line then places.
        var stream = await _subtitles.GetSubtitles(item!, mediaSourceId, index, "vtt", startPositionTicks, endPositionTicks, true, cancellationToken)
            .ConfigureAwait(false);
        string vtt;
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            vtt = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var start = await videoStart.ConfigureAwait(false);
        return File(Encoding.UTF8.GetBytes(HlsSubtitles.SetTimestampMap(vtt, container, start)), "text/vtt");
    }

    /// <summary>The token this request was signed in with, for the addresses it writes: Jellyfin keeps it as a claim.</summary>
    private string? Token => User.FindFirst("Jellyfin-Token")?.Value;

    /// <summary>A container name as given, if it is one: letters and digits, a few at most.</summary>
    private static string? Container(string? given) =>
        given is { Length: > 0 and <= 8 } && given.All(char.IsAsciiLetterOrDigit) ? given : null;

    /// <summary>A play session id as given, if it is one: Jellyfin's are 32 hex digits.</summary>
    private static string? PlaySession(string? given) =>
        given is { Length: > 0 and <= 64 } && given.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? given : null;

    /// <summary>
    /// The video, if the caller can see it (Jellyfin's own check, by the caller's account; an API key sees everything, as
    /// in Jellyfin), its version, and the text subtitle track asked for.
    /// </summary>
    private async Task<(Video? Item, MediaSourceInfo? Source, ActionResult? Refusal)> Find(
        Guid itemId, string mediaSourceId, int index, CancellationToken cancellationToken)
    {
        if (_library.GetItemById<Video>(itemId, CallerId ?? Guid.Empty) is not { } item)
        {
            return (null, null, Problem(StatusCodes.Status404NotFound, "No such video."));
        }

        // As Jellyfin's own subtitle playlist finds it: null when the item has no version by that id.
        var source = await _mediaSources.GetMediaSource(item, mediaSourceId, null, false, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return (null, null, Problem(StatusCodes.Status404NotFound, "No such version of this video."));
        }

        var track = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);
        if (track is null || !track.IsTextSubtitleStream)
        {
            return (null, null, Problem(StatusCodes.Status404NotFound, "This video has no text subtitle track with that index."));
        }

        return (item, source, null);
    }
}
