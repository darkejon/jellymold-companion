using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using JellyMoldCompanion.Picture;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The black bars built into each file's picture (design §16 idea 11), read before playback so an app opens a widescreen
/// film already cropped, and reported by an app that has settled a crop itself. Same signing-in rules as the rest of the
/// API; the bars are the household's, like the files.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class PictureCropController : CompanionControllerBase
{
    private readonly CompanionService _companion;
    private readonly PictureCropMeasurer _measurer;

    public PictureCropController(CompanionService companion, PictureCropMeasurer measurer)
    {
        _companion = companion;
        _measurer = measurer;
    }

    /// <summary>
    /// A file's bars: the plugin's measurement, or an app's report until the plugin has measured it. 404 when neither
    /// exists yet — the item is then measured ahead of everything else, so it is usually ready by the next play.
    /// </summary>
    [HttpGet("Items/{itemId}/PictureCrop")]
    public ActionResult<object> Get([FromRoute, Required] Guid itemId, [FromQuery] Guid? mediaSourceId)
    {
        if (_measurer.Target(itemId, mediaSourceId) is not { } target)
        {
            return Problem(StatusCodes.Status404NotFound, "There is no such film, episode or video with a file the plugin can measure.");
        }

        if (_measurer.Current(target) is { } crop)
        {
            return Body(crop);
        }

        return Problem(StatusCodes.Status404NotFound, _measurer.AskFor(itemId)
            ? "Not measured yet. It is next in line."
            : "Not measured: measuring black bars is switched off on this server.");
    }

    public sealed record ReportRequest
    {
        public Guid? MediaSourceId { get; init; }

        [Range(1, 16384)]
        public int Width { get; init; }

        [Range(1, 16384)]
        public int Height { get; init; }

        [Range(0, 16384)]
        public int Top { get; init; }

        [Range(0, 16384)]
        public int Bottom { get; init; }

        [Range(0, 16384)]
        public int Left { get; init; }

        [Range(0, 16384)]
        public int Right { get; init; }
    }

    /// <summary>
    /// An app's own finding, for a file the plugin has not measured yet. Kept beside the plugin's measurement, which always
    /// wins; two reports of a file keep the smaller bar on each side. Answers what an app is now given for the file.
    /// </summary>
    [HttpPost("Items/{itemId}/PictureCrop")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> Report([FromRoute, Required] Guid itemId, [FromBody, Required] ReportRequest request)
    {
        var bars = new Bars(request.Top, request.Bottom, request.Left, request.Right);
        if (CropRules.CheckReport(request.Width, request.Height, bars) is { } refusal)
        {
            return Problem(StatusCodes.Status400BadRequest, refusal);
        }

        if (_measurer.Target(itemId, request.MediaSourceId) is not { } target)
        {
            return Problem(StatusCodes.Status404NotFound, "There is no such film, episode or video with a file the plugin can measure.");
        }

        // Kept in the file's own frame, whatever frame the app measured in (a decoded picture, or shares of 10000), so
        // every answer's Width and Height are the file's.
        var report = new PictureCrop
        {
            ItemId = target.ItemId,
            MediaSourceId = target.MediaSourceId,
            Source = CropSources.App,
            Width = target.Width,
            Height = target.Height,
            Bars = CropRules.Tidy(CropRules.Scale(bars, request.Width, request.Height, target.Width, target.Height), target.Width, target.Height),
            File = target.File,
            ReportedBy = CallerId,
            MeasuredAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await _companion.Recording.Write(s => PictureCrops.Report(s, report)).ConfigureAwait(false);
        }
        catch (global::JellyMoldCompanion.Import.ImportException e)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, e.Message);
        }

        var current = _measurer.Current(target);
        if (current?.Source != CropSources.Server)
        {
            _measurer.AskFor(itemId);
        }

        return current is null ? Problem(StatusCodes.Status500InternalServerError, "The report was not kept.") : Body(current);
    }

    /// <summary>Admin only: forget what was measured and reported for an item, and measure it again.</summary>
    [HttpDelete("Items/{itemId}/PictureCrop")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Forget([FromRoute, Required] Guid itemId)
    {
        try
        {
            var forgotten = await _companion.Recording.Write(s => PictureCrops.Forget(s, itemId)).ConfigureAwait(false);
            _measurer.AskFor(itemId);
            return new { Forgotten = forgotten };
        }
        catch (global::JellyMoldCompanion.Import.ImportException e)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, e.Message);
        }
    }

    private static object Body(PictureCrop crop) => new
    {
        crop.ItemId,
        crop.MediaSourceId,
        crop.Width,
        crop.Height,
        crop.Bars.Top,
        crop.Bars.Bottom,
        crop.Bars.Left,
        crop.Bars.Right,
        MeasuredAt = crop.MeasuredAt.UtcDateTime,
        crop.Source,
    };
}
