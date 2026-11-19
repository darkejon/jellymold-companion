using System.Net;
using System.Text;
using System.Text.Json;

namespace JellyMoldCompanion.Ratings;

/// <summary>What MDBList said about a key.</summary>
public enum KeyState
{
    /// <summary>Not checked yet, or no key.</summary>
    Unknown,
    Valid,
    Invalid,

    /// <summary>MDBList could not be reached. The key may be fine.</summary>
    Unreachable,
}

/// <summary>A key check: whether it works, and the account's allowance today.</summary>
public sealed record KeyCheck(KeyState State, int DailyLimit = 0, int UsedToday = 0);

/// <summary>How a batch request went.</summary>
public enum BatchState
{
    Ok,

    /// <summary>Too many requests too quickly. Wait <see cref="BatchResult.RetryAfter"/> and carry on.</summary>
    Slowed,

    /// <summary>The day's allowance is spent. Stop until tomorrow.</summary>
    DailyLimit,
    InvalidKey,
    Failed,
}

public sealed record BatchResult(BatchState State, Dictionary<string, IReadOnlyList<TitleRating>>? Found = null, TimeSpan RetryAfter = default,
    string? Reason = null);

/// <summary>
/// MDBList's API, for the two calls the plugin makes: checking a key, and looking up a batch of titles.
///
/// <b>The key is the server owner's and never leaves this class in anything written down.</b> MDBList takes it as a
/// query parameter, so a request's address contains it: no address is ever logged, put in an exception or returned. The
/// HttpClient given here must be one of the plugin's own, never Jellyfin's factory client, whose handlers log addresses.
/// </summary>
public sealed class MdbListClient(HttpClient http)
{
    public static readonly Uri Base = new("https://api.mdblist.com/");

    /// <summary>A check of the key, which MDBList does not count against the allowance (measured 2026-11-19).</summary>
    public async Task<KeyCheck> CheckKey(string? key, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new KeyCheck(KeyState.Unknown);
        }

        try
        {
            using var response = await http.GetAsync(Address("user", key), token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new KeyCheck(KeyState.Invalid);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new KeyCheck(KeyState.Unreachable);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = document.RootElement;
            return new KeyCheck(KeyState.Valid, Int(root, "api_requests"), Int(root, "api_requests_count"));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return new KeyCheck(KeyState.Unreachable);
        }
    }

    /// <summary>Ratings for up to <see cref="RatingRules.BatchSize"/> titles, all looked up by one provider as one kind.</summary>
    public async Task<BatchResult> Lookup(string key, string provider, string kind, IReadOnlyList<string> ids, CancellationToken token)
    {
        try
        {
            using var body = new StringContent(RatingRules.BatchBody(provider, ids), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(Address($"{provider}/{kind}/", key), body, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new BatchResult(BatchState.Ok, RatingRules.ParseBatch(text, provider)),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new BatchResult(BatchState.InvalidKey),
                HttpStatusCode.TooManyRequests when text.Contains("Daily", StringComparison.OrdinalIgnoreCase) =>
                    new BatchResult(BatchState.DailyLimit),
                HttpStatusCode.TooManyRequests => new BatchResult(BatchState.Slowed, RetryAfter: RetryAfter(response)),
                _ => new BatchResult(BatchState.Failed, Reason: $"MDBList answered {(int)response.StatusCode}"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            // An HttpRequestException's message names the host, never the address; the type alone is enough here.
            return new BatchResult(BatchState.Failed, Reason: e is TaskCanceledException ? "MDBList took too long" : "MDBList could not be reached");
        }
    }

    /// <summary>How long MDBList asked to wait, within reason: a minute when it does not say.</summary>
    internal static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var said = response.Headers.RetryAfter is { } r
            ? r.Delta ?? (r.Date is { } d ? d - DateTimeOffset.UtcNow : null)
            : null;
        return said is { } wait && wait > TimeSpan.Zero ? (wait > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : wait) : TimeSpan.FromMinutes(1);
    }

    private static Uri Address(string path, string key) => new(Base, $"{path}?apikey={Uri.EscapeDataString(key.Trim())}");

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
