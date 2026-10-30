using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// Who is asking (design §9.1), shared by the plugin's controllers: the person is the caller's own sign-in, read from
/// its claim and never from a path or a body; a <c>UserId</c> is honoured only for admins; an API key signs in as an
/// admin with no person, so it must name one.
/// </summary>
public abstract class CompanionControllerBase : ControllerBase
{
    protected Guid? CallerId =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) && id != Guid.Empty ? id : null;

    protected bool CallerIsAdmin => User.IsInRole("Administrator");

    /// <summary>The person a request is about: the caller, or — for an admin — whoever they name.</summary>
    protected bool TryResolve(Guid? requested, out Guid userId, out ActionResult? refusal)
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
            refusal = Problem(StatusCodes.Status400BadRequest, "An API key must name a UserId.");
            return false;
        }

        userId = self;
        return true;
    }

    protected ObjectResult Problem(int status, string detail) => StatusCode(status, new { Status = status, Detail = detail });
}
