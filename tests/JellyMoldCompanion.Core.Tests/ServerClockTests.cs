using JellyMoldCompanion.Model;

namespace JellyMoldCompanion.Tests;

/// <summary>The server's time zone, for an account's allowed hours (§16 idea 15).</summary>
public class ServerClockTests
{
    [Fact]
    public void AnIanaZoneIsGivenAsItIs()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.Equal("Europe/London", ServerClock.IanaZone(london));
    }

    [Fact]
    public void AWindowsNameBecomesItsIanaName()
    {
        var windows = TimeZoneInfo.CreateCustomTimeZone("GMT Standard Time", TimeSpan.Zero, "GMT Standard Time", "GMT");
        Assert.Equal("Europe/London", ServerClock.IanaZone(windows));
    }

    [Fact]
    public void ANameWithNoIanaEquivalentIsNull()
    {
        var odd = TimeZoneInfo.CreateCustomTimeZone("Not A Zone", TimeSpan.FromHours(3), "Not A Zone", "Not A Zone");
        Assert.Null(ServerClock.IanaZone(odd));
    }

    [Fact]
    public void TheOffsetIncludesSummerTime()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.Equal(0, ServerClock.OffsetSeconds(london, new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(3600, ServerClock.OffsetSeconds(london, new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)));
    }
}
