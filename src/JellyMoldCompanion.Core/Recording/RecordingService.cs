using System.Threading.Channels;
using JellyMoldCompanion.Api;
using JellyMoldCompanion.Storage;
using Microsoft.Extensions.Logging;

namespace JellyMoldCompanion.Recording;

/// <summary>
/// The queue between Jellyfin and the recorder (design §2). Jellyfin's threads drop messages here and return at
/// once; one loop takes them off in order and applies them. A fault in the recorder costs a record, never a
/// playback — every exception is caught and logged here.
/// </summary>
public sealed class RecordingService : IAsyncDisposable
{
    private readonly HistoryDatabase _database;
    private readonly Func<CountingRules> _rules;
    private readonly Func<RecordingScope> _scope;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<RecorderMessage> _queue =
        Channel.CreateUnbounded<RecorderMessage>(new UnboundedChannelOptions { SingleReader = true });

    private HistoryStore? _store;
    private Recorder? _recorder;
    private Task? _loop;
    private ITimer? _sweep;

    public RecordingService(HistoryDatabase database, Func<CountingRules> rules, Func<RecordingScope> scope, ILogger logger, TimeProvider? time = null)
    {
        _database = database;
        _rules = rules;
        _scope = scope;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True once started and able to record — false when the database is read-only (§10.2) or startup failed.</summary>
    public bool IsRecording => _recorder is not null;

    /// <summary>Messages waiting. Counted here: a single-reader channel cannot count itself.</summary>
    public int Pending => Volatile.Read(ref _pending);

    private int _pending;

    public int OpenSessions => _recorder?.OpenSessionCount ?? 0;

    public void Start()
    {
        _database.Initialise();
        if (_database.IsReadOnly)
        {
            return;
        }

        _store = new HistoryStore(_database);
        _recorder = new Recorder(_store, _rules, _scope, _logger);
        _recorder.Recover(_time.GetUtcNow());
        _loop = Task.Run(RunAsync);
        _sweep = _time.CreateTimer(_ => Enqueue(new SweepTick(_time.GetUtcNow())), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Never throws, never blocks. False when not recording.</summary>
    public bool Enqueue(RecorderMessage message)
    {
        if (_recorder is null || !_queue.Writer.TryWrite(message))
        {
            return false;
        }

        Interlocked.Increment(ref _pending);
        return true;
    }

    public Task<IReadOnlyList<ClientSessionOutcome>> SubmitClientSessions(ClientSessionSubmission submission)
    {
        if (!Enqueue(submission))
        {
            IReadOnlyList<ClientSessionOutcome> refused = submission.Sessions
                .Select(s => new ClientSessionOutcome(s.Input.ClientSessionId, "Rejected", "notRecording", null)).ToList();
            return Task.FromResult(refused);
        }

        return submission.Done.Task;
    }

    private async Task RunAsync()
    {
        await foreach (var message in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _recorder!.Apply(message);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "JellyMold Companion could not record {Message}", message.GetType().Name);
                if (message is ClientSessionSubmission submission)
                {
                    submission.Done.TrySetException(e);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sweep is not null)
        {
            await _sweep.DisposeAsync().ConfigureAwait(false);
        }

        _queue.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _store?.Dispose();
    }
}
