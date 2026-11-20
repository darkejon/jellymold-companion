using System.Text;
using JellyMoldCompanion.Hls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyMoldCompanion.Api;

/// <summary>
/// <c>GET /JellyMold/v1/Videos/{itemId}/master.m3u8?…</c> — Jellyfin's own HLS master playlist with subtitles that keep
/// time (design §16 idea 14, <see cref="HlsSubtitles"/>). It takes exactly the query of Jellyfin's <c>TranscodingUrl</c>.
///
/// The request is handed to Jellyfin as if it had been made to Jellyfin's own <c>/videos/{itemId}/master.m3u8</c>, the
/// same query and all, inside the same request: Jellyfin signs the caller in from the address's token, applies its own
/// checks, sees the caller's own address (which decides its extra variants for remote players), and writes its own
/// master. Only the answer is changed — each subtitle rendition pointed at the plugin's subtitle playlist
/// (<see cref="HlsSubtitlesController"/>), every other address pointed back at Jellyfin's own. Placed in front of
/// Jellyfin's own handling of every request, as <c>StatsMenuLink</c> is; it touches nothing but that one path.
/// </summary>
internal sealed class HlsMasterPlaylist : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(Serve);
        next(app);
    };

    private static async Task Serve(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || HlsSubtitles.MasterRequest(request.Path.Value) is not { } master)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Jellyfin's answer whole and uncompressed, so it can be read; a few hundred bytes.
        var path = request.Path;
        request.Path = new PathString(master.JellyfinPath);
        request.Headers.Remove("Accept-Encoding");

        var response = context.Response;
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
            request.Path = path;
        }

        var bytes = buffer.ToArray();
        if (response.StatusCode == StatusCodes.Status200OK && bytes.Length > 0)
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                bytes = Encoding.UTF8.GetBytes(HlsSubtitles.RewriteMaster(
                    text, master.ItemId, request.Query["SegmentContainer"].FirstOrDefault(), request.Query["PlaySessionId"].FirstOrDefault()));
                response.ContentLength = bytes.Length;
            }
        }

        await body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
