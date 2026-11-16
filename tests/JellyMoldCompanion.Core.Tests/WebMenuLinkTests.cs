using System.Text.Json.Nodes;
using JellyMoldCompanion.Web;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §11: the JellyMold Stats link in everyone's Jellyfin menu, added to config.json as it is served.</summary>
public class WebMenuLinkTests
{
    // As Jellyfin 10.10.7 ships it, shortened.
    private const string Shipped =
        """{"includeCorsCredentials":false,"multiserver":false,"themes":[{"name":"Dark","id":"dark","color":"#202020","default":true}],"menuLinks":[],"servers":[],"plugins":["htmlVideoPlayer/plugin"]}""";

    private static JsonArray Links(string json) => (JsonArray)JsonNode.Parse(json)!["menuLinks"]!;

    [Fact]
    public void TheLinkIsAddedToAnEmptyMenu()
    {
        var link = Assert.Single(Links(WebMenuLink.AddTo(Shipped)));
        Assert.Equal("JellyMold Stats", (string?)link!["name"]);
        Assert.Equal("../JellyMold/Stats", (string?)link["url"]);
        Assert.Equal("bar_chart", (string?)link["icon"]);
    }

    [Fact]
    public void EverythingElseInTheConfigIsKept()
    {
        var before = JsonNode.Parse(Shipped)!.AsObject();
        var after = JsonNode.Parse(WebMenuLink.AddTo(Shipped))!.AsObject();
        foreach (var (key, value) in before)
        {
            if (key != "menuLinks")
            {
                Assert.True(JsonNode.DeepEquals(value, after[key]), key);
            }
        }
    }

    [Fact]
    public void AnAdminsOwnLinksComeFirst()
    {
        var config = Shipped.Replace("\"menuLinks\":[]", "\"menuLinks\":[{\"name\":\"Requests\",\"url\":\"https://requests.example\"}]");
        var links = Links(WebMenuLink.AddTo(config));
        Assert.Equal(["Requests", "JellyMold Stats"], links.Select(l => (string?)l!["name"]));
    }

    [Fact]
    public void ALinkToTheStatsPageAlreadyThereIsNotDoubled()
    {
        var config = Shipped.Replace("\"menuLinks\":[]", "\"menuLinks\":[{\"name\":\"Our stats\",\"url\":\"https://jf.example/JellyMold/Stats\"}]");
        Assert.Equal(config, WebMenuLink.AddTo(config));
        Assert.Single(Links(WebMenuLink.AddTo(WebMenuLink.AddTo(Shipped))));
    }

    [Fact]
    public void AConfigWithNoMenuGetsOne()
    {
        Assert.Single(Links(WebMenuLink.AddTo("""{"themes":[]}""")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"menuLinks":"something else"}""")]
    public void AnythingUnexpectedIsLeftAsItCame(string config)
    {
        Assert.Equal(config, WebMenuLink.AddTo(config));
    }
}
