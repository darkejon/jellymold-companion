using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// A film's or series' ratings from other sites (design §16 idea 13), fetched from MDBList with the server owner's key.
/// Offered only while <c>/Info</c> lists the <c>ratings</c> feature. Same signing-in rules as the rest of the API.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class RatingsController : CompanionControllerBase
{
    private readonly RatingsFetcher _fetcher;
    private readonly ILibraryManager _library;

    public RatingsController(RatingsFetcher fetcher, ILibraryManager library)
    {
        _fetcher = fetcher;
        _library = library;
    }

    /// <summary>
    /// A title's ratings, each on its source's own scale. 404 when it has not been fetched yet: it is then fetched ahead
    /// of everything else, so it is usually there a few seconds later.
    /// </summary>
    [HttpGet("Items/{itemId}/Ratings")]
    public ActionResult<object> Get([FromRoute, Required] Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (!RatingsFetcher.IsRateable(item))
        {
            return Problem(StatusCodes.Status404NotFound, "Ratings are kept for films and series. There is no such film or series.");
        }

        if (_fetcher.Queries.ForItem(itemId) is { } found)
        {
            return new
            {
                found.ItemId,
                Ratings = found.Ratings.Select(r => new { r.Source, r.Value, r.Votes }),
                FetchedAt = found.FetchedAt.UtcDateTime,
            };
        }

        if (RatingsFetcher.Lookup(item!) is null)
        {
            return Problem(StatusCodes.Status404NotFound,
                "This title has no TMDb, TheTVDB or IMDb id, so MDBList cannot be asked about it. Identifying it in Jellyfin gives it one.");
        }

        return Problem(StatusCodes.Status404NotFound, _fetcher.AskFor(itemId)
            ? "Not fetched yet. It is next in line."
            : "Not fetched: this server has no working MDBList key, or fetching ratings is switched off.");
    }
}
