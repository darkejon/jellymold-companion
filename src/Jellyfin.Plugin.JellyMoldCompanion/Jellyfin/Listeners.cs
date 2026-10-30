using Jellyfin.Data.Events.Users;
using JellyMoldCompanion.Recording;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Playback, session and user events, heard as event consumers (design §2): Jellyfin awaits them inside the
/// client's own request, so one client's start, progress and stop arrive in order, and catches whatever they
/// throw. Each one only turns the event into a message and drops it on the queue.
/// </summary>
public sealed class PlaybackListener :
    IEventConsumer<PlaybackStartEventArgs>,
    IEventConsumer<PlaybackProgressEventArgs>,
    IEventConsumer<PlaybackStopEventArgs>,
    IEventConsumer<SessionEndedEventArgs>,
    IEventConsumer<UserDeletedEventArgs>
{
    private readonly CompanionService _companion;
    private readonly ILogger<PlaybackListener> _logger;

    public PlaybackListener(CompanionService companion, ILogger<PlaybackListener> logger)
    {
        _companion = companion;
        _logger = logger;
    }

    public Task OnEvent(PlaybackStartEventArgs eventArgs) => Report(eventArgs, ReportKind.Start);

    public Task OnEvent(PlaybackProgressEventArgs eventArgs) =>
        // Jellyfin's own one-second guess at the position: never counted (§4), so not even queued.
        eventArgs.IsAutomated ? Task.CompletedTask : Report(eventArgs, ReportKind.Progress);

    public Task OnEvent(PlaybackStopEventArgs eventArgs) => Report(eventArgs, ReportKind.Stop);

    public Task OnEvent(SessionEndedEventArgs eventArgs)
    {
        if (eventArgs.Argument?.Id is { } id)
        {
            _companion.Enqueue(new JellyfinSessionEnded(DateTimeOffset.UtcNow, id));
        }

        return Task.CompletedTask;
    }

    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        if (eventArgs.Argument is { } user)
        {
            _companion.Enqueue(new JellyfinUserDeleted(DateTimeOffset.UtcNow, user.Id));
        }

        return Task.CompletedTask;
    }

    private Task Report(PlaybackProgressEventArgs e, ReportKind kind)
    {
        try
        {
            var item = _companion.Host.Snapshot(e.Item);
            if (item is null || e.Users is not { Count: > 0 })
            {
                return Task.CompletedTask;
            }

            var session = e.Session;
            _companion.Enqueue(new PlaybackReport(DateTimeOffset.UtcNow)
            {
                Kind = kind,
                JellyfinSessionId = session?.Id ?? e.DeviceId ?? "unknown",
                PlaySessionId = string.IsNullOrEmpty(e.PlaySessionId) ? null : e.PlaySessionId,
                Users = e.Users.Select(_companion.Host.ToRecorded).ToList(),
                Item = item,
                PositionTicks = e.PlaybackPositionTicks,
                IsPaused = e.IsPaused,
                IsAutomated = e.IsAutomated,
                Client = e.ClientName,
                ClientVersion = session?.ApplicationVersion,
                DeviceName = e.DeviceName,
                DeviceId = e.DeviceId,
                PlayMethod = session?.PlayState?.PlayMethod?.ToString(),
                MediaSourceId = string.IsNullOrEmpty(e.MediaSourceId) ? null : e.MediaSourceId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyMold Companion could not read a {Kind} report", kind);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The two events Jellyfin offers only as plain .NET events: watch data saved, and an item leaving the library.
/// Both are raised synchronously with nothing to catch an exception, so every handler is wrapped.
/// </summary>
public sealed class LibraryListener : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly ILibraryManager _library;
    private readonly CompanionService _companion;
    private readonly IdleGuessCorrector _corrector;
    private readonly ILogger<LibraryListener> _logger;

    public LibraryListener(IUserDataManager userData, ILibraryManager library, CompanionService companion, IdleGuessCorrector corrector,
        ILogger<LibraryListener> logger)
    {
        _userData = userData;
        _library = library;
        _companion = companion;
        _corrector = corrector;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved += OnUserDataSaved;
        _library.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        _library.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var item = _companion.Host.Snapshot(e.Item);
            if (item is null || e.UserData is null || _companion.Host.Person(e.UserId) is not { } user)
            {
                return;
            }

            _companion.Enqueue(new UserDataSaved(DateTimeOffset.UtcNow)
            {
                User = user,
                Item = item,
                // The plugin's own correction after an idle stop is not somebody unticking the item.
                Reason = _corrector.IsCorrecting(e.UserId, item.ItemId) ? SaveReason.Other
                    : Enum.TryParse<SaveReason>(e.SaveReason.ToString(), out var reason) ? reason : SaveReason.Other,
                Played = e.UserData.Played,
                PositionTicks = e.UserData.PlaybackPositionTicks,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyMold Companion could not read a watch-data change");
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is { } item)
            {
                _companion.Enqueue(new LibraryItemRemoved(DateTimeOffset.UtcNow, item.Id));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyMold Companion could not read a library removal");
        }
    }
}
