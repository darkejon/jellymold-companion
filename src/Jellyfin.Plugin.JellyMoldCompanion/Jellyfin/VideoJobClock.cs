#if JELLYFIN_10_10
using JellyMoldCompanion.Hls;
using MediaBrowser.Controller.MediaEncoding;
#endif

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Where in the film the clock of a play session's fMP4 video reads zero, for the plugin's subtitle pieces (design §19
/// #20, <see cref="JellyMoldCompanion.Hls.VideoJob"/>).
///
/// Only Jellyfin 10.10 needs it: a video job it begins mid-film starts its clock there. Jellyfin 12 writes the film's own
/// time into its pieces, so its subtitle pieces need nothing (measured on 12.1, 2026-11-19), and this answers nothing.
/// </summary>
public sealed class VideoJobClock
{
#if JELLYFIN_10_10
    /// <summary>The longest a piece waits for the job that gives its video: a jump's new job, which Jellyfin starts when
    /// the player asks for the video, often just after it asks for the subtitles.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private readonly ITranscodeManager _transcodes;

    public VideoJobClock(ITranscodeManager transcodes) => _transcodes = transcodes;

    /// <summary>
    /// Where the video job of <paramref name="playSessionId"/>, for that version of the video, began: the job that gives
    /// the video for the piece from <paramref name="startTicks"/> to <paramref name="endTicks"/>. If none does within a few
    /// seconds, the session's job as it then is; null with no job to read, which leaves the timing line as it would be
    /// for a job begun at the start.
    /// </summary>
    public async Task<double?> StartFor(string playSessionId, string mediaSourceId, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Wait;
        VideoJob? last = null;
        while (true)
        {
            var job = _transcodes.GetTranscodingJob(playSessionId);
            if (job is { Type: TranscodingJobType.Hls, Path: { } path } && SameVersion(job, mediaSourceId)
                && VideoJob.Read(path) is { } read)
            {
                if (read.Gives(startTicks, endTicks))
                {
                    return read.Start;
                }

                last = read;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return last?.Start;
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The job is for the version of the video the subtitles are for: a play session is one item's.</summary>
    private static bool SameVersion(TranscodingJob job, string mediaSourceId) =>
        job.MediaSource?.Id is not { } id
        || (Guid.TryParse(id, out var a) && Guid.TryParse(mediaSourceId, out var b) ? a == b : string.Equals(id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
#else
    /// <summary>Nothing to set on Jellyfin 12, which keeps the film's own time in its fMP4 pieces.</summary>
    public Task<double?> StartFor(string playSessionId, string mediaSourceId, long startTicks, long endTicks, CancellationToken cancellationToken) =>
        Task.FromResult<double?>(null);
#endif
}
