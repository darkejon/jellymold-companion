using System.Globalization;

namespace JellyMoldCompanion.Model;

/// <summary>
/// Every cross-server name an item answers to (design §3, <c>media_keys</c>). Used to recognise the same media under
/// a new item id — a better rip, a rebuilt library — so its history carries on rather than starting again, and to
/// match imports. The app does its own merging from the provider ids every record carries; these keys are the
/// plugin's own.
/// </summary>
public static class MediaKeys
{
    /// <summary>
    /// The providers that identify films and series. Every one present becomes a key, and any one shared key makes
    /// two items the same media.
    /// </summary>
    private static readonly string[] Providers = ["Tmdb", "Imdb", "Tvdb", "TvRage", "Zap2It"];

    private static readonly string[] MusicProviders = ["MusicBrainzTrack", "MusicBrainzRecording"];

    public static IReadOnlyList<string> For(MediaSnapshot item)
    {
        var keys = new List<string>();
        switch (item.Kind)
        {
            case MediaKind.Episode:
                // An episode is its series and its numbers. Without both numbers there is nothing stable to say.
                if (item.SeasonNumber is int season && item.EpisodeNumber is int episode)
                {
                    foreach (var (provider, id) in Present(item.SeriesProviderIds, Providers))
                    {
                        keys.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{provider}-series:{id}|s{season:00}e{episode:00}"));
                    }
                }

                // Some episodes carry ids of their own (a TVDB or TMDb episode id).
                foreach (var (provider, id) in Present(item.ProviderIds, Providers))
                {
                    keys.Add($"{provider}-episode:{id}");
                }

                break;

            case MediaKind.Audio:
                foreach (var (provider, id) in Present(item.ProviderIds, MusicProviders))
                {
                    keys.Add($"{provider}:{id}");
                }

                // No MusicBrainz id: artist, album and title together are as close to an identity as a track has.
                if (keys.Count == 0 && item.Artists.Count > 0)
                {
                    keys.Add($"t:{Normalise(item.Artists[0])}|{Normalise(item.AlbumName)}|{Normalise(item.Name)}");
                }

                break;

            default:
                foreach (var (provider, id) in Present(item.ProviderIds, Providers))
                {
                    keys.Add($"{provider}:{id}");
                }

                break;
        }

        return keys;
    }

    private static IEnumerable<(string Provider, string Id)> Present(
        IReadOnlyDictionary<string, string> ids, string[] providers)
    {
        foreach (var provider in providers)
        {
            // Case-insensitively: Jellyfin has shipped both `Tmdb` and `TMDB` over the years.
            foreach (var (key, value) in ids)
            {
                if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    yield return (provider.ToLowerInvariant(), value.Trim());
                    break;
                }
            }
        }
    }

    private static string Normalise(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant();
}
