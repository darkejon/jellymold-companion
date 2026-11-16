using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellyMoldCompanion.Web;

/// <summary>
/// The JellyMold Stats link in everyone's Jellyfin menu (design §11). Jellyfin's web client reads extra links for its side
/// menu from its <c>config.json</c>, fresh each time it loads, and opens each in a new tab. The plugin adds this link to
/// that file as Jellyfin sends it, never to the file itself: it needs no permission to Jellyfin's folders, and outlasts
/// Jellyfin's updates, which replace the file.
/// </summary>
public static class WebMenuLink
{
    public const string Name = "JellyMold Stats";

    /// <summary>Relative to the web client at <c>&lt;root&gt;/web/</c>, so it holds behind a base URL or a reverse proxy.</summary>
    public const string Url = "../JellyMold/Stats";

    /// <summary>A Material icon's name, as the web client's own links use.</summary>
    public const string Icon = "bar_chart";

    /// <summary>
    /// The web client's <c>config.json</c> with the link in its menu, after any links an admin put there by hand. Given
    /// as it came when the link is already there (by hand or otherwise), or when it is not the config the web client
    /// expects: the web client is never handed something it could not read.
    /// </summary>
    public static string AddTo(string configJson)
    {
        JsonObject? config;
        try
        {
            config = JsonNode.Parse(configJson) as JsonObject;
        }
        catch (JsonException)
        {
            return configJson;
        }

        if (config is null)
        {
            return configJson;
        }

        var links = config["menuLinks"] as JsonArray;
        if (links is null)
        {
            if (config.ContainsKey("menuLinks"))
            {
                return configJson;   // something there that is not a list: not ours to replace
            }

            links = [];
            config["menuLinks"] = links;
        }

        if (links.Any(link => link is JsonObject entry && LinksToStats(entry)))
        {
            return configJson;
        }

        links.Add(new JsonObject { ["name"] = Name, ["icon"] = Icon, ["url"] = Url });
        return config.ToJsonString();
    }

    private static bool LinksToStats(JsonObject entry) =>
        entry["url"] is JsonValue value && value.TryGetValue<string>(out var url) &&
        url.Contains("JellyMold/Stats", StringComparison.OrdinalIgnoreCase);
}
