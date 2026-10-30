using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;
#if JELLYFIN_12
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;
#else
using SortOrder = Jellyfin.Data.Enums.SortOrder;
#endif

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// The home screen in one request (design §16 idea 8): Continue Watching, Next Up and the newest in each library, which
/// an app otherwise asks for in several requests a server. Items come back as Jellyfin's own item objects, exactly as
/// its endpoints give them, so an app reads them with the code it already has.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyMold/v1")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class HomeController : CompanionControllerBase
{
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly ITVSeriesManager _series;
    private readonly IUserViewManager _views;
    private readonly IDtoService _dto;

    public HomeController(IUserManager users, ILibraryManager library, ITVSeriesManager series, IUserViewManager views, IDtoService dto)
    {
        _users = users;
        _library = library;
        _series = series;
        _views = views;
        _dto = dto;
    }

    public sealed record LibraryShelf(BaseItemDto Library, IReadOnlyList<BaseItemDto> Items);

    public sealed record Home(IReadOnlyList<BaseItemDto> ContinueWatching, IReadOnlyList<BaseItemDto> NextUp, IReadOnlyList<LibraryShelf> Latest);

    [HttpGet("Home")]
    public ActionResult<Home> Get([FromQuery, Range(1, 50)] int limit = 16, [FromQuery] Guid? userId = null)
    {
        if (!TryResolve(userId, out var id, out var refusal))
        {
            return refusal!;
        }

        if (_users.GetUserById(id) is not { } user)
        {
            return Problem(StatusCodes.Status404NotFound, "No such person.");
        }

        // What the app's shelves read: provider ids to merge servers, user data for the progress bar, one image of each kind.
        var options = new DtoOptions
        {
            Fields = [ItemFields.ProviderIds, ItemFields.PrimaryImageAspectRatio, ItemFields.DateCreated],
            EnableImages = true,
            ImageTypeLimit = 1,
            EnableUserData = true,
        };

        var resume = _library.GetItemList(new InternalItemsQuery(user)
        {
            IsResumable = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = limit,
        });

        var nextUp = _series.GetNextUp(new NextUpQuery { User = user, Limit = limit }, options).Items;

        var shelves = new List<LibraryShelf>();
        foreach (var view in _views.GetUserViews(new UserViewQuery { User = user }))
        {
            // Every list filled in, empty or not: Jellyfin reads IncludeItemTypes without checking for null (10.10.7).
            var query = new LatestItemsQuery
            {
                User = user,
                ParentId = view.Id,
                Limit = limit,
                GroupItems = true,
                IncludeItemTypes = [],
                Fields = [],
                EnableImageTypes = [],
            };
            var latest = _views.GetLatestItems(query, options)
                .Select(group => group.Item2.Count > 1 && group.Item1 is not null ? group.Item1 : group.Item2[0])
                .ToList();
            if (latest.Count > 0)
            {
                shelves.Add(new LibraryShelf(_dto.GetBaseItemDto(view, options, user), _dto.GetBaseItemDtos(latest, options, user)));
            }
        }

        return new Home(_dto.GetBaseItemDtos(resume.ToList(), options, user), _dto.GetBaseItemDtos(nextUp.ToList(), options, user), shelves);
    }
}
