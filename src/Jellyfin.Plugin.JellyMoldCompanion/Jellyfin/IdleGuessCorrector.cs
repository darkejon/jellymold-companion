using System.Collections.Concurrent;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// Undoes Jellyfin 10.10's guess after an idle stop (design §6.1, §16 idea 10). When an app vanishes mid-play, Jellyfin's
/// idle timer stops it at the position its own one-second timer had reached — 5 to 10 minutes past where the viewer was —
/// and judges *watched* from that. So the resume point lands late, and a nearly finished episode can be ticked watched
/// and leave Continue Watching.
///
/// With the setting on, a few seconds after such a stop (once Jellyfin has saved its guess) this puts the resume point
/// back to the last position the app really reported, and takes back a watched tick only the invented position earned.
/// The one feature that writes into Jellyfin's own watch data, so off until chosen. Jellyfin 12 stops at the real
/// position itself, so there it does nothing.
/// </summary>
public sealed class IdleGuessCorrector : IHostedService
{
    private readonly CompanionService _companion;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IServerConfigurationManager _server;
    private readonly ILogger<IdleGuessCorrector> _logger;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTime> _correcting = new();

    public IdleGuessCorrector(CompanionService companion, IUserManager users, ILibraryManager library, IUserDataManager userData,
        IServerConfigurationManager server, ILogger<IdleGuessCorrector> logger)
    {
        _companion = companion;
        _users = users;
        _library = library;
        _userData = userData;
        _server = server;
        _logger = logger;
    }

    /// <summary>True on this build: Jellyfin 12 stops a vanished app at its real position, so there is nothing to undo.</summary>
    public static bool FixedUpstream =>
#if JELLYFIN_12
        true;
#else
        false;
#endif

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _companion.Recording.IdleStopped = Handle;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _companion.Recording.IdleStopped = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether a watch-data save for this person and item is this corrector's own — so the recorder logs it as a change
    /// to Jellyfin's flags rather than as someone unticking the item.
    /// </summary>
    public bool IsCorrecting(Guid userId, Guid itemId) =>
        _correcting.TryGetValue((userId, itemId), out var since) && DateTime.UtcNow - since < TimeSpan.FromSeconds(30);

    /// <summary>Called on the recorder's thread: only schedules the work.</summary>
    private void Handle(IdleStop stop)
    {
        if (FixedUpstream || !CompanionService.Settings.FixIdleStopGuess || stop.Kind == MediaKind.Audio)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Jellyfin saves its guess as it raises the stop; let it finish first.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Correct(stop);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "JellyMold Companion could not put back a resume point after an idle stop");
            }
        });
    }

    private void Correct(IdleStop stop)
    {
        var user = _users.GetUserById(stop.UserId);
        var item = _library.GetItemById(stop.ItemId);
        if (user is null || item is null || item.RunTimeTicks is not > 0 || _userData.GetUserData(user, item) is not { } data)
        {
            return;
        }

        var (played, position) = WhatJellyfinWouldHaveSaved(stop, item.RunTimeTicks.Value);
        var wasTicked = data.Played;
        if (data.Played == played && data.PlaybackPositionTicks == position)
        {
            // Jellyfin's guess came to the same thing as the truth.
            return;
        }

        var guessed = data.PlaybackPositionTicks;
        data.Played = played;
        data.PlaybackPositionTicks = position;
        _correcting[(user.Id, item.Id)] = DateTime.UtcNow;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        _logger.LogInformation(
            "JellyMold Companion put {Name}'s resume point in {Item} back to {Real} from Jellyfin's guess{Untick}",
            user.Username, item.Name, TimeSpan.FromTicks(position), wasTicked && !played ? ", and took back the watched tick it had earned" : string.Empty);

        var at = DateTimeOffset.UtcNow;
        _ = _companion.Recording.Write(s =>
        {
            s.InsertEvent(at, "idle-guess-undone", user.Id, item.Id, positionTicks: position,
                detail: new { guessedTicks = guessed, untick = wasTicked && !played });
            return true;
        });
    }

    /// <summary>
    /// Jellyfin 10.10's own rule (<c>UserDataManager.UpdatePlayState</c>), applied to the position the app really
    /// reported instead of the one Jellyfin invented — so the correction undoes only the guess, never one of the server's
    /// settings. Played and position, as Jellyfin would have saved them.
    /// </summary>
    private (bool Played, long Position) WhatJellyfinWouldHaveSaved(IdleStop stop, long runtime)
    {
        var config = _server.Configuration;
        var position = stop.LastPositionTicks;
        if (stop.Kind == MediaKind.AudioBook)
        {
            var heard = TimeSpan.FromTicks(position);
            var left = TimeSpan.FromTicks(runtime - position);
            return heard.TotalMinutes < config.MinAudiobookResume ? (false, 0)
                : left.TotalMinutes < config.MaxAudiobookResume ? (true, 0)
                : (false, position);
        }

        var percent = position * 100.0 / runtime;
        if (percent < config.MinResumePct)
        {
            return (false, 0);
        }

        if (percent > config.MaxResumePct || position >= runtime ||
            TimeSpan.FromTicks(runtime).TotalSeconds < config.MinResumeDurationSeconds)
        {
            return (true, 0);
        }

        return (false, position);
    }
}
