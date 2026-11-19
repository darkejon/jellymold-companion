using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using JellyMoldCompanion.Ratings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Fetches films' and series' ratings from MDBList with the server owner's own key (design §16 idea 13). A title is
/// fetched a few minutes after it is added, at once when an app asks for one not fetched yet, and by the nightly task
/// for the rest of the library; then again a month later. Never more than half the key's daily allowance.
///
/// <b>The key is never written to a log, an error or an answer.</b> It is checked against MDBList when it is first
/// seen and whenever it changes; the app is told ratings are on offer (<see cref="Offering"/>) only once a check has
/// passed, so a mistyped key offers nothing.
/// </summary>
public sealed class RatingsFetcher : IHostedService, IDisposable
{
    public static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series];

    /// <summary>Jellyfin fetches a new title's metadata, and with it the ids MDBList is asked by, first.</summary>
    private static readonly TimeSpan AfterAdding = TimeSpan.FromMinutes(5);

    /// <summary>Asks arriving together go in one request: an app opening a page asks for one title, a home screen for several.</summary>
    private static readonly TimeSpan GatherAsks = TimeSpan.FromSeconds(2);

    /// <summary>A key that passed is checked again this often, for the allowance used and in case it was revoked.</summary>
    private static readonly TimeSpan RecheckValidKey = TimeSpan.FromHours(1);

    /// <summary>MDBList out of reach: try the key again after this long.</summary>
    private static readonly TimeSpan RecheckUnreachable = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _library;
    private readonly CompanionService _companion;
    private readonly ILogger<RatingsFetcher> _logger;
    private readonly HttpClient _http;
    private readonly MdbListClient _client;
    private readonly Channel<Guid> _asked = Channel.CreateUnbounded<Guid>();
    private readonly Channel<Guid> _added = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _waiting = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();

    private string? _checkedKey;
    private KeyCheck _check = new(KeyState.Unknown);
    private DateTimeOffset _checkedAt;
    private DateOnly _day;
    private int _usedByUs;
    private int _usedSinceCheck;
    private bool _spentToday;
    private Task? _worker;

    public RatingsFetcher(ILibraryManager library, CompanionService companion, ILogger<RatingsFetcher> logger)
    {
        _library = library;
        _companion = companion;
        _logger = logger;

        // The plugin's own client, never Jellyfin's factory one: the key is in the address, and the factory's handlers
        // log addresses.
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var version = typeof(RatingsFetcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"JellyMoldCompanion/{version.Split('+')[0]}");
        _client = new MdbListClient(_http);
    }

    public RatingQueries Queries => new(_companion.Database);

    /// <summary>Whether this server gives ratings: switched on, and a key that has passed a check.</summary>
    public bool Offering => On && KeyState == KeyState.Valid;

    public KeyState KeyState
    {
        get
        {
            lock (_lock)
            {
                return _checkedKey == Key ? _check.State : KeyState.Unknown;
            }
        }
    }

    /// <summary>Requests this plugin has made today (UTC, as MDBList counts), and the day's allowance.</summary>
    public (int Used, int Limit, bool Spent) Today
    {
        get
        {
            lock (_lock)
            {
                RollDay();
                return (_usedByUs, _check.DailyLimit, _spentToday);
            }
        }
    }

    public int Waiting => _waiting.Count;

    private static bool On => CompanionService.Settings.FetchRatings && !string.IsNullOrWhiteSpace(Key);

    private static string? Key => CompanionService.Settings.MdbListApiKey?.Trim() is { Length: > 0 } key ? key : null;

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
        if (_worker is { } worker)
        {
            await Task.WhenAny(worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _oneAtATime.Dispose();
        _http.Dispose();
    }

    // — What can be rated —

    public static bool IsRateable(BaseItem? item) =>
        item is not null && !item.IsVirtualItem && item.ExtraType is null && Kinds.Contains(item.GetBaseItemKind());

    public static RatingLookup? Lookup(BaseItem item)
    {
        string? Id(string name) =>
            item.ProviderIds?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var kind = item.GetBaseItemKind() == BaseItemKind.Series ? RatingKinds.Show : RatingKinds.Movie;
        return RatingRules.Lookup(kind, Id("Tmdb"), Id("Tvdb"), Id("Imdb"));
    }

    // — The key —

    /// <summary>Checks the key if it is new, changed, or due. Answers whether ratings are on offer.</summary>
    public async Task<bool> EnsureKeyChecked(CancellationToken token, bool force = false)
    {
        var key = Key;
        if (!CompanionService.Settings.FetchRatings || key is null)
        {
            return false;
        }

        lock (_lock)
        {
            var due = _check.State switch
            {
                KeyState.Valid => RecheckValidKey,
                KeyState.Unreachable => RecheckUnreachable,
                _ => TimeSpan.MaxValue,
            };
            if (!force && _checkedKey == key && (due == TimeSpan.MaxValue || DateTimeOffset.UtcNow - _checkedAt < due))
            {
                return _check.State == KeyState.Valid;
            }
        }

        var check = await _client.CheckKey(key, token).ConfigureAwait(false);
        lock (_lock)
        {
            var changed = _checkedKey != key || _check.State != check.State;
            _checkedKey = key;
            _check = check;
            _checkedAt = DateTimeOffset.UtcNow;
            _usedSinceCheck = 0;
            RollDay();
            if (changed)
            {
                _logger.LogInformation("JellyMold Companion: the MDBList key {State:l}", check.State switch
                {
                    KeyState.Valid => $"works ({check.UsedToday} of {check.DailyLimit} requests used today)",
                    KeyState.Invalid => "was refused by MDBList",
                    _ => "could not be checked: MDBList could not be reached",
                });
            }
        }

        return check.State == KeyState.Valid;
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _day)
        {
            _day = today;
            _usedByUs = 0;
            _spentToday = false;
        }
    }

    private int Allowance()
    {
        lock (_lock)
        {
            RollDay();
            return _spentToday ? 0 : RatingRules.Allowance(_check.DailyLimit, _check.UsedToday + _usedSinceCheck, _usedByUs);
        }
    }

    // — The queue —

    /// <summary>An app asked for a title not fetched yet: fetch it next. False when ratings are not on offer.</summary>
    public bool AskFor(Guid itemId)
    {
        if (!Offering)
        {
            return false;
        }

        if (_waiting.TryAdd(itemId, 0))
        {
            _asked.Writer.TryWrite(itemId);
        }

        return true;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (On && IsRateable(e.Item))
        {
            _ = FetchSoon(e.Item.Id);
        }
    }

    private async Task FetchSoon(Guid itemId)
    {
        try
        {
            await Task.Delay(AfterAdding, _stopping.Token).ConfigureAwait(false);
            if (_waiting.TryAdd(itemId, 0))
            {
                _added.Writer.TryWrite(itemId);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EnsureKeyChecked(token).ConfigureAwait(false);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromMinutes(1));   // to notice a key entered or changed on the settings page
                try
                {
                    await Task.WhenAny(_asked.Reader.WaitToReadAsync(wait.Token).AsTask(), _added.Reader.WaitToReadAsync(wait.Token).AsTask())
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    continue;
                }

                await Task.Delay(GatherAsks, token).ConfigureAwait(false);
                var ids = new List<Guid>();
                while (ids.Count < RatingRules.BatchSize && (_asked.Reader.TryRead(out var id) || _added.Reader.TryRead(out id)))
                {
                    _waiting.TryRemove(id, out _);
                    ids.Add(id);
                }

                if (ids.Count == 0 || !Offering)
                {
                    continue;
                }

                var items = ids.Select(i => _library.GetItemById(i)).Where(IsRateable).Select(i => i!).ToList();
                await Fetch(items, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "JellyMold Companion could not fetch ratings");
            }
        }
    }

    // — Fetching —

    /// <summary>
    /// Fetches the given titles, a batch a request, within today's allowance. Titles with no id MDBList knows are left
    /// alone. Answers how many titles were fetched and whether the run stopped early (allowance spent, key refused).
    /// </summary>
    public async Task<(int Fetched, bool Stopped)> Fetch(IReadOnlyList<BaseItem> items, CancellationToken token)
    {
        if (!_companion.Recording.IsRecording || !await EnsureKeyChecked(token).ConfigureAwait(false))
        {
            // The history is open for reading only (the settings page says why), or there is no key that works.
            return (0, true);
        }

        var groups = items
            .Select(i => (Item: i, Lookup: Lookup(i)))
            .Where(t => t.Lookup is not null)
            .GroupBy(t => (t.Lookup!.Provider, t.Lookup.Kind));

        var fetched = 0;
        await _oneAtATime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var group in groups)
            {
                foreach (var batch in group.Chunk(RatingRules.BatchSize))
                {
                    var (done, stop) = await FetchBatch(group.Key.Provider, group.Key.Kind, batch, token).ConfigureAwait(false);
                    fetched += done;
                    if (stop)
                    {
                        return (fetched, true);
                    }
                }
            }
        }
        finally
        {
            _oneAtATime.Release();
        }

        return (fetched, false);
    }

    private async Task<(int Fetched, bool Stop)> FetchBatch(string provider, string kind, (BaseItem Item, RatingLookup? Lookup)[] batch,
        CancellationToken token)
    {
        // The same title can be in a library twice (two versions as separate items): ask once, keep it for both.
        var ids = batch.Select(b => b.Lookup!.Id).Distinct(StringComparer.Ordinal).ToList();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Allowance() <= 0)
            {
                return (0, true);
            }

            var key = Key;
            if (key is null)
            {
                return (0, true);
            }

            var result = await _client.Lookup(key, provider, kind, ids, token).ConfigureAwait(false);
            lock (_lock)
            {
                _usedByUs++;
                _usedSinceCheck++;
            }

            switch (result.State)
            {
                case BatchState.Ok:
                    var now = DateTimeOffset.UtcNow;
                    await _companion.Recording.Write(s =>
                    {
                        foreach (var (item, lookup) in batch)
                        {
                            var found = result.Found!.TryGetValue(lookup!.Id, out var ratings);
                            TitleRatings.Save(s, item.Id, lookup, found ? RatingOutcomes.Found : RatingOutcomes.NotFound, ratings, now);
                        }

                        return true;
                    }).ConfigureAwait(false);
                    return (batch.Length, false);

                case BatchState.Slowed:
                    _logger.LogInformation("JellyMold Companion: MDBList asked for a pause of {Seconds:0} s", result.RetryAfter.TotalSeconds);
                    await Task.Delay(result.RetryAfter, token).ConfigureAwait(false);
                    continue;

                case BatchState.DailyLimit:
                    lock (_lock)
                    {
                        _spentToday = true;
                    }

                    _logger.LogInformation("JellyMold Companion: the MDBList key's requests for today are used up; ratings carry on tomorrow");
                    return (0, true);

                case BatchState.InvalidKey:
                    await EnsureKeyChecked(token, force: true).ConfigureAwait(false);
                    return (0, true);

                default:
                    _logger.LogWarning("JellyMold Companion could not fetch ratings for {Count} titles: {Reason}", batch.Length, result.Reason);
                    var failedAt = DateTimeOffset.UtcNow;
                    await _companion.Recording.Write(s =>
                    {
                        foreach (var (item, lookup) in batch)
                        {
                            TitleRatings.Save(s, item.Id, lookup!, RatingOutcomes.Failed, null, failedAt);
                        }

                        return true;
                    }).ConfigureAwait(false);
                    return (0, false);
            }
        }

        return (0, true);
    }
}
