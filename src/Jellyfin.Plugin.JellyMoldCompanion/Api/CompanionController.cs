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
    private readonly Imports.ImportService _imports;

    public CompanionController(CompanionService companion, Imports.ImportService imports)
    {
        _companion = companion;
        _imports = imports;
    }

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
            Features = new[] { "history", "summary", "changes", "clientSessions", "export", "sessions", "imports" },
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

    // — Imports (§8) —

    /// <summary>The sources, and whether each can be used here and now.</summary>
    [HttpGet("Imports/Sources")]
    public ActionResult<object> ImportSourcesAvailable() => new[]
    {
        new { Source = ImportSourceNames.Jellyfin, Available = true, ServerWide = false, NeedsFile = false,
            Description = "Jellyfin's own watch state: everything already watched, as the baseline. Taken for everyone automatically." },
        new { Source = ImportSourceNames.PlaybackReporting, Available = CallerIsAdmin && _companion.Host.PlaybackReportingDatabase is not null,
            ServerWide = true, NeedsFile = false,
            Description = "Playback Reporting's database on this server: real dates and seconds for everyone's plays. Admin only." },
        new { Source = ImportSourceNames.TraktExport, Available = true, ServerWide = false, NeedsFile = true,
            Description = "A Trakt export (the history or watched JSON files)." },
        new { Source = ImportSourceNames.Letterboxd, Available = true, ServerWide = false, NeedsFile = true,
            Description = "Letterboxd's diary.csv or watched.csv. Films only, matched by title and year." },
        new { Source = ImportSourceNames.Csv, Available = true, ServerWide = false, NeedsFile = true,
            Description = "A CSV with the columns in the API document." },
    };

    /// <summary>Imports, newest first: the caller's own, or — for an admin passing <c>all=true</c> — every one, server-wide ones included.</summary>
    [HttpGet("Imports")]
    public ActionResult<object> ImportList([FromQuery] Guid? userId = null, [FromQuery] bool all = false)
    {
        if (all)
        {
            return CallerIsAdmin
                ? new { Items = _companion.Queries.Imports(null) }
                : Problem(StatusCodes.Status403Forbidden, "Only an administrator can list everyone's imports.");
        }

        return TryResolve(userId, out var user, out var refusal) ? new { Items = _companion.Queries.Imports(user) } : refusal!;
    }

    /// <summary>
    /// Reads a source and reports what importing it would add — nothing is written until it is committed. For the file
    /// sources, the request body is the file itself (at most 20 MB).
    /// </summary>
    [HttpPost("Imports/{source}/Preview")]
    [RequestSizeLimit(Imports.ImportService.MaxFileBytes)]
    public async Task<ActionResult<object>> PreviewImport([FromRoute, Required] string source, [FromQuery] Guid? userId = null)
    {
        Guid? person = null;
        if (global::JellyMoldCompanion.Import.ImportSources.IsServerWide(source))
        {
            if (!CallerIsAdmin)
            {
                return Problem(StatusCodes.Status403Forbidden, "Only an administrator can import a server-wide source.");
            }
        }
        else if (TryResolve(userId, out var user, out var refusal))
        {
            person = user;
        }
        else
        {
            return refusal!;
        }

        // The file as the request body (Content-Type text/csv or application/json), or as a multipart form's file.
        using var file = new MemoryStream();
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync().ConfigureAwait(false);
            if (form.Files.FirstOrDefault() is { } upload)
            {
                await upload.CopyToAsync(file).ConfigureAwait(false);
            }
            else if (!global::JellyMoldCompanion.Import.ImportSources.IsServerWide(source) && source != global::JellyMoldCompanion.Import.ImportSources.Jellyfin)
            {
                return Problem(StatusCodes.Status400BadRequest,
                    "Send the file as the request body (Content-Type text/csv or application/json), or as a file in a multipart form.");
            }
        }
        else
        {
            await Request.Body.CopyToAsync(file).ConfigureAwait(false);
        }

        file.Position = 0;
        try
        {
            return await _imports.Preview(source, person, file.Length > 0 ? file : null).ConfigureAwait(false);
        }
        catch (global::JellyMoldCompanion.Import.ImportException e)
        {
            return Problem(StatusCodes.Status400BadRequest, e.Message);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "That file could not be read: " + e.Message);
        }
    }

    [HttpPost("Imports/{importId}/Commit")]
    public async Task<ActionResult<object>> CommitImport([FromRoute, Required] Guid importId) =>
        await OnImport(importId, async () => await _imports.Commit(importId).ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>Undoes an import: its entries stop counting (they are kept, marked undone), and whatever it replaced counts again.</summary>
    [HttpPost("Imports/{importId}/Undo")]
    public async Task<ActionResult<object>> UndoImport([FromRoute, Required] Guid importId) =>
        await OnImport(importId, async () => new { Undone = await _imports.Undo(importId).ConfigureAwait(false) }).ConfigureAwait(false);

    /// <summary>Something done to an existing import: allowed to its own person and to admins.</summary>
    private async Task<ActionResult<object>> OnImport(Guid importId, Func<Task<object>> action)
    {
        var import = _companion.Queries.ImportById(importId);
        if (import is null)
        {
            return NotFound();
        }

        if (!CallerIsAdmin && (import.UserId is null || import.UserId != CallerId))
        {
            return Problem(StatusCodes.Status403Forbidden, "That import is not yours.");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (global::JellyMoldCompanion.Import.ImportException e)
        {
            return Problem(StatusCodes.Status409Conflict, e.Message);
        }
    }

    private static class ImportSourceNames
    {
        public const string Jellyfin = global::JellyMoldCompanion.Import.ImportSources.Jellyfin;
        public const string PlaybackReporting = global::JellyMoldCompanion.Import.ImportSources.PlaybackReporting;
        public const string TraktExport = global::JellyMoldCompanion.Import.ImportSources.TraktExport;
        public const string Letterboxd = global::JellyMoldCompanion.Import.ImportSources.Letterboxd;
        public const string Csv = global::JellyMoldCompanion.Import.ImportSources.Csv;
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
