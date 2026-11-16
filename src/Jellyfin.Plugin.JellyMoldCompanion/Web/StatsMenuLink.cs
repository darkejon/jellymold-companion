using System.Text;
using JellyMoldCompanion.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyMoldCompanion.Web;

/// <summary>
/// Puts JellyMold Stats in everyone's Jellyfin menu while the setting is on (design §11): the link is added to the web
/// client's <c>config.json</c> as Jellyfin sends it (<see cref="WebMenuLink"/>), never to the file. Placed in front of
/// Jellyfin's own handling of every request; it touches nothing but that one file, and only while the setting is on.
/// </summary>
internal sealed class StatsMenuLink : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(AddLink);
        next(app);
    };

    private static async Task AddLink(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method)
            || request.Path.Value?.EndsWith("/web/config.json", StringComparison.OrdinalIgnoreCase) != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Jellyfin sends the file with no word on keeping it, so a browser keeps it for weeks (a tenth of its age) and the
        // web client never asks again. Asked again each time instead, link or not, so turning the link on or off shows
        // the next time Jellyfin is opened; answered "not modified" while it is the same.
        var response = context.Response;
        response.OnStarting(() =>
        {
            response.Headers.CacheControl = "no-cache";
            return Task.CompletedTask;
        });

        if (Plugin.Instance?.Configuration.StatsInEveryonesMenu != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // The file whole and uncompressed, so it can be read; small, and asked for once each time the web client loads.
        request.Headers.Remove("Accept-Encoding");
        request.Headers.Remove("If-None-Match");
        request.Headers.Remove("If-Modified-Since");

        var body = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = body;
        }

        var bytes = buffer.ToArray();
        if (response.StatusCode == StatusCodes.Status200OK)
        {
            bytes = Encoding.UTF8.GetBytes(WebMenuLink.AddTo(Encoding.UTF8.GetString(bytes)));
            response.Headers.Remove("ETag");
            response.Headers.Remove("Last-Modified");
            response.ContentLength = bytes.Length;
        }

        await body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
