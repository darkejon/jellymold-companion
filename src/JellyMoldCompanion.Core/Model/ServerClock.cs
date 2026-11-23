namespace JellyMoldCompanion.Model;

/// <summary>
/// The server's time zone, as an app can use it (§16 idea 15). Jellyfin keeps an account's allowed hours (Parental
/// control › Access schedule) in the server's local time and never says which zone that is, so an app cannot warn
/// before the time is up, or say when it opens again, without guessing. The plugin runs on that clock and says.
/// </summary>
public static class ServerClock
{
    /// <summary>
    /// The zone as an IANA name (<c>Europe/London</c>), which every app platform reads. Linux and macOS already name
    /// their zones that way; Windows names them its own way (<c>GMT Standard Time</c>), converted here. Null when there
    /// is no IANA name for it, and an app then has <see cref="OffsetSeconds"/>.
    /// </summary>
    public static string? IanaZone(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    /// <summary>The zone's offset from UTC at <paramref name="at"/>, in seconds: summer time included.</summary>
    public static int OffsetSeconds(TimeZoneInfo zone, DateTimeOffset at) => (int)zone.GetUtcOffset(at).TotalSeconds;
}
