using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The stats page on the web (design §16 idea 12): <c>/JellyMold/Stats</c>, each person's own history as the JellyMold
/// app shows it, for anyone with a browser. The page and its two files are open to anyone, as Jellyfin's own web client
/// is; they hold no data. Everything shown comes from the API by the viewer's own sign-in, so its rules hold here too:
/// a person reads their own history, and only an admin reads anyone else's.
///
/// Built into the plugin and served from Jellyfin's own address, so it needs nothing else installed and works behind
/// a reverse proxy or a base URL: the page finds the API from its own address. It loads nothing from anywhere else,
/// and its security policy forbids it to.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("JellyMold/Stats")]
public sealed class StatsPageController : ControllerBase
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stats.html"] = "text/html; charset=utf-8",
        ["stats.css"] = "text/css; charset=utf-8",
        ["stats.js"] = "text/javascript; charset=utf-8",
        ["jellymold.png"] = "image/png",
    };

    private static readonly Dictionary<string, (byte[] Bytes, string Tag)> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadLock = new();

    /// <summary>The page. With a slash after it, sent to the address without one, so the page's own relative links hold.</summary>
    [HttpGet("")]
    public ActionResult Page()
    {
        if (Request.Path.Value is { } path && path.EndsWith('/'))
        {
            return Redirect("../Stats" + Request.QueryString);
        }

        return Serve("stats.html");
    }

    /// <summary>The page's style sheet, its script, and the JellyMold app's artwork for the banner at its top.</summary>
    [HttpGet("{file}")]
    public ActionResult Asset([FromRoute] string file) =>
        file is "stats.css" or "stats.js" or "jellymold.png" ? Serve(file) : NotFound();

    private ActionResult Serve(string name)
    {
        var (bytes, tag) = Load(name);
        var headers = Response.Headers;
        // Asked again each time, answered with "not modified" while it is the same: a new release shows at once.
        headers.CacheControl = "no-cache";
        headers.ETag = tag;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'self'";

        if (Request.Headers.IfNoneMatch.Contains(tag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(bytes, Types[name]);
    }

    private static (byte[] Bytes, string Tag) Load(string name)
    {
        lock (LoadLock)
        {
            if (!Loaded.TryGetValue(name, out var found))
            {
                var resource = $"{typeof(StatsPageController).Namespace![..^".Api".Length]}.Web.{name}";
                using var stream = typeof(StatsPageController).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"The stats page's {name} is missing from the plugin.");
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                found = (bytes, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"");
                Loaded[name] = found;
            }

            return found;
        }
    }
}
