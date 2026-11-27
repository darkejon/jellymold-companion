namespace JellyMoldCompanion.Notifications;

/// <summary>New episodes of one series waiting to be told about, each with when it arrived.</summary>
public sealed record EpisodeBatch(Guid Series, IReadOnlyDictionary<Guid, DateTimeOffset> Episodes)
{
    public DateTimeOffset First => Episodes.Values.Min();

    public DateTimeOffset Last => Episodes.Values.Max();

    /// <summary>Ten minutes since the first episode arrived: a batch held for its series' ids goes now, or not at all.</summary>
    public bool PastLongest(DateTimeOffset now) => now - First >= EpisodeBatches.Longest;
}

/// <summary>
/// New episodes gathered per series, so a season added in one scan is one notification (design §16 idea 16). A batch
/// is ready once no new episode of its series has arrived for <see cref="Quiet"/>, or <see cref="Longest"/> after its
/// first, whichever is sooner. Safe to use from Jellyfin's threads; the clock is the caller's.
/// </summary>
public sealed class EpisodeBatches
{
    /// <summary>No new episode of the series for this long: the scan has finished adding it.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(90);

    /// <summary>A long scan still sends something this long after the first episode.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    /// <summary>A batch the relay could not be told about is tried again until this long after its first episode.</summary>
    public static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(30);

    private readonly Dictionary<Guid, Dictionary<Guid, DateTimeOffset>> _waiting = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>An episode arrived. Arriving again (a second scan) keeps its first arrival.</summary>
    public void Add(Guid series, Guid episode, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(series, out var episodes))
            {
                _waiting[series] = episodes = new Dictionary<Guid, DateTimeOffset>();
            }

            episodes.TryAdd(episode, now);
        }
    }

    /// <summary>The batches ready to go, as they stand: quiet for <see cref="Quiet"/>, or <see cref="Longest"/> old.</summary>
    public IReadOnlyList<EpisodeBatch> Ready(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _waiting
                .Select(w => new EpisodeBatch(w.Key, new Dictionary<Guid, DateTimeOffset>(w.Value)))
                .Where(b => now - b.Last >= Quiet || now - b.First >= Longest)
                .OrderBy(b => b.First)
                .ToList();
        }
    }

    /// <summary>
    /// These episodes are told about, or dropped. Any of the series that arrived meanwhile stay, and make a batch of
    /// their own.
    /// </summary>
    public void Done(Guid series, IEnumerable<Guid> episodes)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(series, out var waiting))
            {
                return;
            }

            foreach (var episode in episodes)
            {
                waiting.Remove(episode);
            }

            if (waiting.Count == 0)
            {
                _waiting.Remove(series);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _waiting.Clear();
        }
    }
}
