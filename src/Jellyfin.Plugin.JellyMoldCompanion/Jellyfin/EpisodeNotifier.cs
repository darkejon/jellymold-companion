using System.Reflection;
using JellyMoldCompanion.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Tells JellyMold apps about new episodes (design §16 idea 16), through JellyMold's notification relay: Seerr says only
/// that a requested show is complete, never that an episode arrived. New episodes are gathered per series
/// (<see cref="EpisodeBatches"/>), read again from the library when their batch is ready, and sent only for shows
/// somebody on this server asked to hear about (<see cref="WantedShows"/>), so nothing else in the library leaves the
/// server. The relay is not contacted until an episode arrives.
/// </summary>
public sealed class EpisodeNotifier : IHostedService, IDisposable
{
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(15);

    /// <summary>After the relay could not be reached, leave it alone this long.</summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _library;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<EpisodeNotifier> _logger;
    private readonly HttpClient _http;
    private readonly IRelayInstanceStore _store;
    private readonly EpisodeBatches _batches = new();
    private readonly CancellationTokenSource _stopping = new();
    private RelaySession? _session;
    private string? _sessionRelay;
    private DateTimeOffset _quietUntil;
    private Task? _worker;

    public EpisodeNotifier(ILibraryManager library, IServerApplicationHost host, IApplicationPaths paths, ILogger<EpisodeNotifier> logger)
    {
        _library = library;
        _host = host;
        _logger = logger;

        // The plugin's own client, as for ratings: nothing of Jellyfin's handlers between it and the relay.
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var version = typeof(EpisodeNotifier).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"JellyMoldCompanion/{version.Split('+')[0]}");

        // Beside the history database, never in the settings, which an admin's browser reads and writes back whole.
        _store = new RelayInstanceFile(Path.Combine(paths.DataPath, "jellymold", "relay.json"));
    }

    /// <summary>The setting. While it is on, <c>/Info</c> lists <c>episodeNotifications</c> for the app.</summary>
    public static bool On => CompanionService.Settings.NotifyNewEpisodes;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _http.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Missing-episode placeholders are virtual: they arrive with the series' metadata, not as files.
        if (!On || e.Item is not Episode { IsVirtualItem: false } episode)
        {
            return;
        }

        try
        {
            // A scan adds an episode before it has written the series' id on it, so it is found through its folders
            // (Episode.Series does that when the id is empty).
            var series = episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.Series?.Id ?? Guid.Empty;
            if (series != Guid.Empty)
            {
                _batches.Add(series, episode.Id, DateTimeOffset.UtcNow);
            }
            else
            {
                _logger.LogDebug("JellyMold Companion found no series for the new episode {Path}", episode.Path);
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "JellyMold Companion could not note a new episode for JellyMold apps");
        }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(LookEvery, stopping).ConfigureAwait(false);
                if (!On)
                {
                    _batches.Clear();
                    continue;
                }

                foreach (var batch in _batches.Ready(DateTimeOffset.UtcNow))
                {
                    if (DateTimeOffset.UtcNow < _quietUntil)
                    {
                        break;
                    }

                    await TellAsync(batch, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server is stopping.
        }
    }

    private async Task TellAsync(EpisodeBatch batch, CancellationToken stopping)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            // Read now, not when they arrived: the scan makes an item before its metadata, so an episode's name may
            // still be its file's, and a new series may have no ids yet. Anything deleted meanwhile is left out.
            var series = _library.GetItemById(batch.Series) as Series;
            var episodes = batch.Episodes.Keys
                .Select(id => _library.GetItemById(id) as Episode)
                .Where(e => e is { IsVirtualItem: false })
                .Select(e => new EpisodeFacts(e!.Id, e.ParentIndexNumber, e.IndexNumber, e.Name ?? string.Empty))
                .ToList();
            var facts = series is null ? null : Facts(series);

            switch (EpisodeNotice.Decide(facts, episodes, batch.PastLongest(now)))
            {
                case BatchVerdict.Hold:
                    return;
                case BatchVerdict.Drop:
                    if (facts is not null && episodes.Count > 0)
                    {
                        _logger.LogInformation("JellyMold Companion did not tell JellyMold apps about new episodes of {Series}: it still has no TMDB or TVDB id",
                            facts.Name);
                    }

                    _batches.Done(batch.Series, batch.Episodes.Keys);
                    return;
            }

            var session = Session();
            if (session is null)
            {
                _batches.Done(batch.Series, batch.Episodes.Keys);
                return;
            }

            var wanted = await session.WantedAsync(stopping).ConfigureAwait(false);
            if (!wanted.Wants(facts!))
            {
                // Nobody asked to hear about this show: nothing about it leaves the server.
                _batches.Done(batch.Series, batch.Episodes.Keys);
                return;
            }

            var (sent, failed) = await session.SendAsync(EpisodeNotice.Body(facts!, episodes), stopping).ConfigureAwait(false);
            _logger.LogInformation(
                "JellyMold Companion told JellyMold apps about {Count} new {Episodes} of {Series}: {Sent} notifications sent, {Failed} failed",
                episodes.Count, episodes.Count == 1 ? "episode" : "episodes", facts!.Name, sent, failed);
            _batches.Done(batch.Series, batch.Episodes.Keys);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is RelayException or HttpRequestException or TaskCanceledException or IOException)
        {
            // Never the exception itself in full: a relay error says only what went wrong, never the secret.
            _quietUntil = now + AfterFailure;
            var givingUp = now - batch.First >= EpisodeBatches.GiveUp;
            _logger.LogWarning("JellyMold Companion could not tell JellyMold apps about new episodes ({Reason}){Then}",
                e is RelayException ? e.Message : e.GetType().Name, givingUp ? "; giving up on them" : "; it will try again");
            if (givingUp)
            {
                _batches.Done(batch.Series, batch.Episodes.Keys);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "JellyMold Companion could not prepare new episodes for JellyMold apps");
            _batches.Done(batch.Series, batch.Episodes.Keys);
        }
    }

    private static SeriesFacts Facts(Series series)
    {
        var ids = new Dictionary<string, string>(series.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        return new SeriesFacts(series.Id, series.Name ?? string.Empty,
            ids.GetValueOrDefault("Tmdb"), ids.GetValueOrDefault("Tvdb"));
    }

    /// <summary>The relay conversation, made the first time it is needed (and again if the relay's address is changed).</summary>
    private RelaySession? Session()
    {
        var relay = CompanionService.Settings.NotificationRelay is { Length: > 0 } custom ? custom : RelayClient.DefaultRelay;
        if (_session is not null && _sessionRelay == relay)
        {
            return _session;
        }

        var serverId = _host.SystemId;
        if (!RelayClient.IsServerId(serverId) || !Uri.TryCreate(relay, UriKind.Absolute, out var address))
        {
            _logger.LogWarning("JellyMold Companion cannot tell JellyMold apps about new episodes: this server's id or the relay's address is not usable");
            return null;
        }

        _sessionRelay = relay;
        return _session = new RelaySession(new RelayClient(_http, address), serverId, _store, () => DateTimeOffset.UtcNow);
    }
}
