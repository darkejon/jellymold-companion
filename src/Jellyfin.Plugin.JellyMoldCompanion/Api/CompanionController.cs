using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using JellyMoldCompanion.Api;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The API for apps (design §9), under <c>/JellyMold/v1</c>.
///
/// Every endpoint needs a signed-in user under Jellyfin's ordinary policy, never the admin one; the user is the
/// caller's own sign-in, read from its claim and never from a path or a body. A <c>UserId</c> is honoured only for
/// admins. No GET changes anything, and every input is bounded.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class CompanionController : ControllerBase
{
    public const int ApiVersion = 1;
    private const int MaxPage = 500;

    private readonly CompanionService _companion;

    public CompanionController(CompanionService companion) => _companion = companion;

    // — Who is asking (§9.1) —

    private Guid? CallerId =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) && id != Guid.Empty ? id : null;

    private bool CallerIsAdmin => User.IsInRole("Administrator");

    /// <summary>The person a request is about: the caller, or — for an admin — whoever they name.</summary>
    private bool TryResolve(Guid? requested, out Guid userId, out ActionResult? refusal)
    {
        refusal = null;
        userId = Guid.Empty;
        var caller = CallerId;
        if (requested is { } named && named != Guid.Empty && named != caller)
        {
            if (!CallerIsAdmin)
            {
                refusal = Problem(StatusCodes.Status403Forbidden, "Only an administrator can read another person's history.");
                return false;
            }

            userId = named;
            return true;
        }

        if (caller is not { } self)
        {
            // An API key signs in as an admin with no user.
            refusal = Problem(StatusCodes.Status400BadRequest, "An API key must name a UserId.");
            return false;
        }

        userId = self;
        return true;
    }

    private ObjectResult Problem(int status, string detail) => StatusCode(status, new { Status = status, Detail = detail });

    // — Discovery (§9.2) —

    /// <summary>Whether the plugin is here, what it can do, and the rules it counts by. 404 means no plugin.</summary>
    [HttpGet("Info")]
    public ActionResult<object> Info()
    {
        var caller = CallerId;
        var user = caller is { } id ? _companion.Queries.User(id) : null;
        var settings = CompanionService.Settings;
        var rules = _companion.Host.Rules(settings);
        var scope = global::Jellyfin.Plugin.JellyMoldCompanion.Jellyfin.JellyfinHost.Scope(settings);
        return new
        {
            Name = "JellyMold Companion",
            PluginVersion = typeof(CompanionController).Assembly.GetName().Version?.ToString(),
            Release = typeof(CompanionController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ApiVersion,
            Features = new[] { "history", "summary", "changes", "clientSessions", "export", "sessions" },
            Recording = _companion.Recording.IsRecording,
            Rules = new
            {
                CompletionPercent = rules.MaxResumePercent,
                AudioBookCompletionMinutesLeft = rules.MaxAudiobookResumeMinutes,
                MusicListenedFraction = rules.MusicListenedFraction,
                MusicListenedSeconds = rules.MusicListenedCap.TotalSeconds,
                MinimumVideoSeconds = rules.MinimumVideo.TotalSeconds,
                MinimumMusicSeconds = rules.MinimumMusic.TotalSeconds,
                MinimumAudioBookSeconds = rules.MinimumAudioBook.TotalSeconds,
                RecordedKinds = scope.Kinds.Select(k => k.ToString()).OrderBy(k => k).ToList(),
            },
            You = new
            {
                UserId = caller,
                IsAdmin = CallerIsAdmin,
                Recorded = caller is { } c && scope.Records(c),
                RecordingSince = user?.RecordingSince?.UtcDateTime,
            },
        };
    }

    // — The stats page in one request —

    [HttpGet("Summary")]
    public ActionResult<Summary> Summary([FromQuery] string? tz, [FromQuery, Range(1, 50)] int recent = 5, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        return _companion.Queries.Summary(user, Zone(tz), recent, _companion.Host, DateTimeOffset.UtcNow);
    }

    [HttpGet("Films")]
    public ActionResult<Page<FilmRow>> Films([FromQuery] string order = "recent", [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, MaxPage)] int limit = 100, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        return _companion.Queries.Films(user, order, startIndex, limit);
    }

    [HttpGet("Series")]
    public ActionResult<Page<SeriesRow>> Series([FromQuery] string order = "recent", [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, MaxPage)] int limit = 100, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        return _companion.Queries.Series(user, order, startIndex, limit, _companion.Host);
    }

    // — History —

    [HttpGet("History")]
    public ActionResult<Page<HistoryEntry>> History([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? kind,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0, [FromQuery, Range(1, MaxPage)] int limit = 100, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        var kinds = new List<MediaKind>();
        foreach (var name in (kind ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<MediaKind>(name, ignoreCase: true, out var parsed))
            {
                return Problem(StatusCodes.Status400BadRequest, $"Unknown kind '{name}'. Use Movie, Episode, Video, Audio or AudioBook.");
            }

            kinds.Add(parsed);
        }

        return _companion.Queries.History(user, Utc(from), Utc(to), kinds, startIndex, limit);
    }

    [HttpGet("Items/{itemId}/History")]
    public ActionResult<object> ItemHistory([FromRoute, Required] Guid itemId, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        var (entries, sessions) = _companion.Queries.ItemHistory(user, itemId);
        return new { Entries = entries, Sessions = sessions };
    }

    /// <summary>The change feed (§9.4): everything changed since the token given, and a token for next time.</summary>
    [HttpGet("Changes")]
    public ActionResult<ChangeFeed> Changes([FromQuery] string? since, [FromQuery, Range(1, MaxPage)] int limit = MaxPage, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        if (since is { Length: > 200 })
        {
            return Problem(StatusCodes.Status400BadRequest, "That is not a change token.");
        }

        return _companion.Queries.Changes(user, since, limit);
    }

    // — Sessions: the diagnostics —

    [HttpGet("Sessions")]
    public ActionResult<Page<SessionEntry>> Sessions([FromQuery] Guid? itemId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0, [FromQuery, Range(1, MaxPage)] int limit = 100, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        return _companion.Queries.Sessions(user, itemId, Utc(from), Utc(to), startIndex, limit);
    }

    [HttpGet("Sessions/{sessionId}")]
    public ActionResult<object> Session([FromRoute, Required] Guid sessionId, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        return _companion.Queries.Session(user, sessionId) is { } found
            ? new { found.Session, found.Events }
            : NotFound();
    }

    // — Watching done offline (§9.3) —

    public sealed record ClientSessionsRequest
    {
        [Required]
        [MaxLength(200)]
        public required IReadOnlyList<ClientSessionInput> Sessions { get; init; }
    }

    [HttpPost("ClientSessions")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> ClientSessions([FromBody, Required] ClientSessionsRequest request, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var user, out var refusal))
        {
            return refusal!;
        }

        if (request.Sessions.Count > 200)
        {
            return Problem(StatusCodes.Status400BadRequest, "At most 200 sessions a request.");
        }

        if (_companion.Host.Person(user) is not { } person)
        {
            return Problem(StatusCodes.Status400BadRequest, "No such user.");
        }

        var submission = new ClientSessionSubmission(DateTimeOffset.UtcNow)
        {
            User = person,
            Sessions = request.Sessions.Select(s => (s, _companion.Host.SnapshotById(s.ItemId))).ToList(),
        };
        var outcomes = await _companion.Recording.SubmitClientSessions(submission).ConfigureAwait(false);
        return new { Results = outcomes };
    }

    // — Export —

    /// <summary>The caller's whole history as JSON or CSV. An admin may pass a <c>UserId</c>, or <c>all=true</c> for everyone.</summary>
    [HttpGet("Export")]
    public ActionResult Export([FromQuery] string format = "json", [FromQuery] Guid? userId = null, [FromQuery] bool all = false)
    {
        IReadOnlyList<Guid> people;
        if (all)
        {
            if (!CallerIsAdmin)
            {
                return Problem(StatusCodes.Status403Forbidden, "Only an administrator can export everyone's history.");
            }

            people = _companion.Queries.Users().Select(u => u.UserId).ToList();
        }
        else if (TryResolve(userId, out var user, out var refusal))
        {
            people = [user];
        }
        else
        {
            return refusal!;
        }

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var stream = new MemoryStream();
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                _companion.Queries.ExportCsv(writer, people);
            }

            stream.Position = 0;
            return File(stream, "text/csv", $"jellymold-history-{stamp}.csv");
        }

        if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status400BadRequest, "Format is json or csv.");
        }

        _companion.Queries.ExportJson(stream, people);
        stream.Position = 0;
        return File(stream, MediaTypeNames.Application.Json, $"jellymold-history-{stamp}.json");
    }

    // — Admin (§11) —

    [HttpGet("Admin/Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> Status()
    {
        var database = _companion.Database;
        return new
        {
            Recording = _companion.Recording.IsRecording,
            Problem = _companion.StartupError ?? database.Problem,
            database.FilePath,
            database.IsReadOnly,
            SchemaVersion = global::JellyMoldCompanion.Storage.HistoryDatabase.SchemaVersion,
            QueueLength = _companion.Recording.Pending,
            OpenSessions = _companion.Recording.OpenSessions,
            Counts = SafeStatus(),
            Backups = database.ListBackups().Select(b => new { Name = Path.GetFileName(b.Path), TakenAt = b.TakenAt.UtcDateTime, b.Bytes }),
            People = _companion.Queries.Users().Select(u => new
            {
                u.UserId,
                u.Name,
                RecordingSince = u.RecordingSince?.UtcDateTime,
                DeletedAt = u.DeletedAt?.UtcDateTime,
            }),
        };
    }

    [HttpPost("Admin/Backups")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> BackUpNow()
    {
        var path = _companion.Database.Backup();
        return new { Name = Path.GetFileName(path) };
    }

    private object? SafeStatus()
    {
        try
        {
            return _companion.Queries.Status();
        }
        catch (Exception e)
        {
            return new { Error = e.Message };
        }
    }

    // — Inputs —

    private static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id.Length <= 64)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static DateTimeOffset? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)),
        { } v => new DateTimeOffset(v.ToUniversalTime()),
    };
}
