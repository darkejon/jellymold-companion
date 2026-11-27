using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JellyMoldCompanion.Notifications;

/// <summary>The plugin's own instance at the relay: an id, and the secret that signs its requests.</summary>
public sealed record RelayInstance(string InstanceId, string Secret)
{
    /// <summary>Never the secret.</summary>
    public override string ToString() => $"RelayInstance {InstanceId}";
}

/// <summary>The relay did not take the request. Its message never holds the secret.</summary>
public class RelayException(string message) : Exception(message);

/// <summary>The relay no longer knows this instance (its storage was reset): make a new one.</summary>
public sealed class RelayUnauthorizedException() : RelayException("The relay did not recognise this server's instance");

/// <summary>
/// JellyMold's notification relay (design §16 idea 16), for the three calls the plugin makes: an instance of its own,
/// the shows anyone wants, and new episodes. The secret travels only in the <c>Authorization</c> header.
/// </summary>
public sealed partial class RelayClient(HttpClient http, Uri relay)
{
    public const string DefaultRelay = "https://jellymold-push.virtueapps26.workers.dev";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ServerIdPattern();

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex InstancePattern();

    [GeneratedRegex("^[a-f0-9]{64,200}$")]
    private static partial Regex SecretPattern();

    /// <summary>Whether the relay will take this server id (Jellyfin's are 32 hex digits).</summary>
    public static bool IsServerId(string? serverId) => serverId is not null && ServerIdPattern().IsMatch(serverId);

    public async Task<RelayInstance> CreateInstanceAsync(string serverId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(Address($"v1/servers/{serverId}/instances"), content: null, cancellationToken)
            .ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.TryGetProperty("error", out var error))
        {
            throw new RelayException($"The relay made no instance: {error.GetString()}");
        }

        var instance = new RelayInstance(Text(json, "instanceId"), Text(json, "secret"));
        return InstancePattern().IsMatch(instance.InstanceId) && SecretPattern().IsMatch(instance.Secret)
            ? instance
            : throw new RelayException("The relay's answer had no usable instance");
    }

    public async Task<WantedShows> ShowsAsync(string serverId, RelayInstance instance, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address($"v1/hook/{serverId}/{instance.InstanceId}/shows"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Secret);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return new WantedShows(Ids(json, "tmdb"), Ids(json, "tvdb"));
    }

    public async Task<(int Sent, int Failed)> SendAsync(string serverId, RelayInstance instance, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Address($"v1/hook/{serverId}/{instance.InstanceId}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Secret);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return (Number(json, "sent"), Number(json, "failed"));
    }

    private Uri Address(string path) => new(new Uri(relay.AbsoluteUri.TrimEnd('/') + "/"), path);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new RelayUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RelayException($"The relay answered {(int)response.StatusCode}");
        }

        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : throw new RelayException("The relay's answer was not what it should be");
        }
        catch (JsonException)
        {
            throw new RelayException("The relay's answer was not JSON");
        }
    }

    private static string Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Number(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;

    private static HashSet<string> Ids(JsonElement json, string name) =>
        json.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(e => e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.Number => e.GetRawText(),
                    _ => null,
                })
                .Select(EpisodeNotice.Id)
                .OfType<string>()
                .ToHashSet()
            : [];
}

/// <summary>Where the plugin keeps its relay instance: never in its settings, which an admin's browser reads.</summary>
public interface IRelayInstanceStore
{
    RelayInstance? Load();

    void Save(RelayInstance instance);
}

/// <summary>A small file beside the history database, readable by the server's account alone.</summary>
public sealed class RelayInstanceFile(string path) : IRelayInstanceStore
{
    public RelayInstance? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.TryGetProperty("instanceId", out var id) && root.TryGetProperty("secret", out var secret)
                && id.GetString() is { Length: > 0 } i && secret.GetString() is { Length: > 0 } s
                ? new RelayInstance(i, s)
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Save(RelayInstance instance)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { instanceId = instance.InstanceId, secret = instance.Secret }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>
/// The plugin's conversation with the relay: its instance, made the first time it is needed and kept; the wanted
/// shows, asked for at most every <see cref="ShowsFor"/>; and on a 401 (the relay's storage was reset) a new instance
/// and one retry. Remade after a refusal at most every <see cref="NewInstanceEvery"/>, since the relay keeps twenty a
/// server and a relay that refuses every instance should not be handed one after another.
/// </summary>
public sealed class RelaySession(RelayClient client, string serverId, IRelayInstanceStore store, Func<DateTimeOffset> clock)
{
    public static readonly TimeSpan ShowsFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NewInstanceEvery = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _one = new(1, 1);
    private RelayInstance? _instance;
    private WantedShows? _shows;
    private DateTimeOffset _showsAt;
    private DateTimeOffset? _remadeAt;

    public async Task<WantedShows> WantedAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shows is not null && clock() - _showsAt < ShowsFor)
            {
                return _shows;
            }

            _shows = await WithInstanceAsync(i => client.ShowsAsync(serverId, i, cancellationToken), cancellationToken).ConfigureAwait(false);
            _showsAt = clock();
            return _shows;
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<(int Sent, int Failed)> SendAsync(string body, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WithInstanceAsync(i => client.SendAsync(serverId, i, body, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<T> WithInstanceAsync<T>(Func<RelayInstance, Task<T>> call, CancellationToken cancellationToken)
    {
        var instance = _instance ??= store.Load() ?? await NewInstanceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(instance).ConfigureAwait(false);
        }
        catch (RelayUnauthorizedException)
        {
            if (_remadeAt is { } remade && clock() - remade < NewInstanceEvery)
            {
                throw new RelayException("The relay refused this server's new instance too; not making another yet");
            }

            _remadeAt = clock();
            _instance = await NewInstanceAsync(cancellationToken).ConfigureAwait(false);
            return await call(_instance).ConfigureAwait(false);
        }
    }

    private async Task<RelayInstance> NewInstanceAsync(CancellationToken cancellationToken)
    {
        var instance = await client.CreateInstanceAsync(serverId, cancellationToken).ConfigureAwait(false);
        store.Save(instance);
        return instance;
    }
}
