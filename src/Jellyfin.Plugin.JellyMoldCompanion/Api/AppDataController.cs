using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using JellyMoldCompanion.AppData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The app's own data on the server (design §16): a store for each person and one for the household, quiz results for a
/// household leaderboard, and learned skip segments. Same signing-in rules as the rest of the API.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class AppDataController : CompanionControllerBase
{
    private readonly CompanionService _companion;
    private readonly Segments.SegmentRefresher _segments;

    public AppDataController(CompanionService companion, Segments.SegmentRefresher segments)
    {
        _companion = companion;
        _segments = segments;
    }

    private AppDataQueries Queries => new(_companion.Database);

    // — One person's documents —

    [HttpGet("Store")]
    public ActionResult<object> ListPersonal([FromQuery] string? prefix, [FromQuery] Guid? userId = null) =>
        TryResolve(userId, out var user, out var refusal) ? new { Items = Queries.List(Documents.Owner(user), prefix) } : refusal!;

    [HttpGet("Store/Changes")]
    public ActionResult<object> PersonalChanges([FromQuery] string? since, [FromQuery, Range(1, 500)] int limit = 500, [FromQuery] Guid? userId = null) =>
        TryResolve(userId, out var user, out var refusal) ? Changes(Documents.Owner(user), since, limit) : refusal!;

    [HttpGet("Store/{key}")]
    public ActionResult<object> GetPersonal([FromRoute, Required] string key, [FromQuery] Guid? userId = null) =>
        TryResolve(userId, out var user, out var refusal) ? Get(Documents.Owner(user), key) : refusal!;

    /// <summary>
    /// Writes a document, if it is still the version read: <c>ifVersion</c> (or an <c>If-Match</c> header), 0 to create.
    /// A clash answers 412 with the current document, to merge and try again.
    /// </summary>
    [HttpPut("Store/{key}")]
    [RequestSizeLimit(Documents.MaxValueBytes + 1024)]
    public async Task<ActionResult<object>> PutPersonal([FromRoute, Required] string key, [FromQuery] long? ifVersion, [FromQuery] Guid? userId = null) =>
        TryResolve(userId, out var user, out var refusal) ? await Put(Documents.Owner(user), key, ifVersion).ConfigureAwait(false) : refusal!;

    [HttpDelete("Store/{key}")]
    public async Task<ActionResult<object>> DeletePersonal([FromRoute, Required] string key, [FromQuery] long? ifVersion, [FromQuery] Guid? userId = null) =>
        TryResolve(userId, out var user, out var refusal) ? await Delete(Documents.Owner(user), key, ifVersion).ConfigureAwait(false) : refusal!;

    // — The household's documents: quiz rounds, chapter names —

    [HttpGet("Household")]
    public ActionResult<object> ListHousehold([FromQuery] string? prefix) => new { Items = Queries.List(Documents.Household, prefix) };

    [HttpGet("Household/Changes")]
    public ActionResult<object> HouseholdChanges([FromQuery] string? since, [FromQuery, Range(1, 500)] int limit = 500) =>
        Changes(Documents.Household, since, limit);

    [HttpGet("Household/{key}")]
    public ActionResult<object> GetHousehold([FromRoute, Required] string key) => Get(Documents.Household, key);

    [HttpPut("Household/{key}")]
    [RequestSizeLimit(Documents.MaxValueBytes + 1024)]
    public async Task<ActionResult<object>> PutHousehold([FromRoute, Required] string key, [FromQuery] long? ifVersion) =>
        await Put(Documents.Household, key, ifVersion).ConfigureAwait(false);

    [HttpDelete("Household/{key}")]
    public async Task<ActionResult<object>> DeleteHousehold([FromRoute, Required] string key, [FromQuery] long? ifVersion) =>
        await Delete(Documents.Household, key, ifVersion).ConfigureAwait(false);

    private ActionResult<object> Get(string owner, string key)
    {
        var document = Queries.Get(owner, key);
        if (document is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = $"\"{document.Version.ToString(CultureInfo.InvariantCulture)}\"";
        return document;
    }

    private async Task<ActionResult<object>> Put(string owner, string key, long? ifVersion)
    {
        if (ReadVersion(ifVersion) is not { } version)
        {
            return Problem(StatusCodes.Status428PreconditionRequired, "Say which version you read: ifVersion=… (0 to create), or If-Match.");
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        var by = CallerId ?? Guid.Empty;
        return await Answer(() => _companion.Recording.Write(s => Documents.Put(s, owner, key, json, version, by, DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    private async Task<ActionResult<object>> Delete(string owner, string key, long? ifVersion)
    {
        if (ReadVersion(ifVersion) is not { } version)
        {
            return Problem(StatusCodes.Status428PreconditionRequired, "Say which version you read: ifVersion=…, or If-Match.");
        }

        var by = CallerId ?? Guid.Empty;
        return await Answer(() => _companion.Recording.Write(s => Documents.Delete(s, owner, key, version, by, DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    private async Task<ActionResult<object>> Answer(Func<Task<PutOutcome>> write)
    {
        try
        {
            var outcome = await write().ConfigureAwait(false);
            if (!outcome.Saved)
            {
                return StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    Status = 412,
                    Detail = outcome.Document is null ? "There is no such document." : "The document has changed since it was read.",
                    Current = outcome.Document,
                });
            }

            Response.Headers.ETag = $"\"{outcome.Document!.Version.ToString(CultureInfo.InvariantCulture)}\"";
            return outcome.Document;
        }
        catch (AppDataException e)
        {
            return Problem(StatusCodes.Status400BadRequest, e.Message);
        }
    }

    private long? ReadVersion(long? query)
    {
        if (query is { } version)
        {
            return version;
        }

        var header = Request.Headers.IfMatch.ToString().Trim().Trim('"');
        return long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>Documents changed since a token; a token from another database (a restore) starts again, as the history's feed does.</summary>
    private ActionResult<object> Changes(string owner, string? since, int limit)
    {
        var databaseId = _companion.Database.DatabaseId;
        long from = 0;
        var resync = false;
        if (!string.IsNullOrEmpty(since))
        {
            var parts = since.Split('.');
            if (parts.Length == 2 && parts[0] == databaseId && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            {
                from = seq;
            }
            else
            {
                resync = true;
            }
        }

        var (changed, next) = Queries.Changes(owner, from, limit);
        return new
        {
            Changed = changed,
            Next = databaseId + "." + next.ToString(CultureInfo.InvariantCulture),
            More = changed.Count == limit,
            Resync = resync,
        };
    }

    // — Quiz results and the household leaderboard —

    public sealed record ScoresRequest([Required, MaxLength(500)] IReadOnlyList<QuizScoreInput> Scores);

    public sealed record SharingRequest(bool Shares);

    [HttpPost("Quiz/Scores")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> PostScores([FromBody, Required] ScoresRequest request)
    {
        if (CallerId is not { } user)
        {
            return Problem(StatusCodes.Status400BadRequest, "Quiz results belong to a person; an API key has none.");
        }

        try
        {
            var added = await _companion.Recording.Write(s => Quiz.Record(s, user, request.Scores)).ConfigureAwait(false);
            return new { Added = added };
        }
        catch (AppDataException e)
        {
            return Problem(StatusCodes.Status400BadRequest, e.Message);
        }
    }

    [HttpGet("Quiz/Sharing")]
    public ActionResult<object> GetSharing() =>
        CallerId is { } user ? new { Shares = Queries.Shares(user) } : Problem(StatusCodes.Status400BadRequest, "No person signed in.");

    /// <summary>Whether this person's quiz results may be shown to the rest of the household. Off until they say so.</summary>
    [HttpPut("Quiz/Sharing")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> PutSharing([FromBody, Required] SharingRequest request)
    {
        if (CallerId is not { } user)
        {
            return Problem(StatusCodes.Status400BadRequest, "No person signed in.");
        }

        await _companion.Recording.Write(s =>
        {
            s.UpsertUser(new global::JellyMoldCompanion.Recording.RecordedUser(user, _companion.Host.Person(user)?.Name ?? "Someone"), DateTimeOffset.UtcNow);
            Quiz.SetSharing(s, user, request.Shares, DateTimeOffset.UtcNow);
            return true;
        }).ConfigureAwait(false);
        return new { request.Shares };
    }

    /// <summary>The caller's results and those of everyone who shares theirs — for one series or film, or everything.</summary>
    [HttpGet("Quiz/Leaderboard")]
    public ActionResult<object> Leaderboard([FromQuery] Guid? seriesId, [FromQuery] Guid? itemId) =>
        CallerId is { } user
            ? new { Items = Queries.Leaderboard(user, seriesId, itemId) }
            : Problem(StatusCodes.Status400BadRequest, "No person signed in.");

    // — Learned skip segments —

    public sealed record SegmentsRequest([Required, MaxLength(500)] IReadOnlyList<SegmentInput> Segments);

    /// <summary>
    /// Segments an app has learned — an intro found across a season — published to Jellyfin as this plugin's segments, so
    /// every app on the server shows the Skip button. Jellyfin only adds them to an item that has none from another
    /// provider.
    /// </summary>
    [HttpPost("Segments")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> PostSegments([FromBody, Required] SegmentsRequest request)
    {
        try
        {
            var by = CallerId ?? Guid.Empty;
            var saved = await _companion.Recording.Write(s =>
                global::JellyMoldCompanion.AppData.Segments.Put(s, request.Segments, id => _companion.Host.SnapshotById(id)?.RuntimeTicks, by, DateTimeOffset.UtcNow)).ConfigureAwait(false);
            var published = await _segments.Publish(request.Segments.Select(s => s.ItemId).Distinct().ToList()).ConfigureAwait(false);
            return new { Saved = saved, Published = published };
        }
        catch (AppDataException e)
        {
            return Problem(StatusCodes.Status400BadRequest, e.Message);
        }
    }

    [HttpGet("Items/{itemId}/Segments")]
    public ActionResult<object> GetSegments([FromRoute, Required] Guid itemId) => new { Items = Queries.SegmentsFor(itemId) };

    [HttpDelete("Items/{itemId}/Segments")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    public async Task<ActionResult<object>> DeleteSegments([FromRoute, Required] Guid itemId, [FromQuery] string? kind)
    {
        var deleted = await _companion.Recording.Write(s => global::JellyMoldCompanion.AppData.Segments.Delete(s, itemId, kind)).ConfigureAwait(false);
        // Jellyfin keeps its own copy; have it ask its providers again, so ours is gone from it too.
        await _segments.Publish([itemId], overwrite: true).ConfigureAwait(false);
        return new { Deleted = deleted };
    }
}
