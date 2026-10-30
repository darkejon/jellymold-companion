using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyMoldCompanion.Configuration;
using JellyMoldCompanion.Api;
using JellyMoldCompanion.Import;
using JellyMoldCompanion.Model;
using JellyMoldCompanion.Recording;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
#if JELLYFIN_12
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#else
using JellyfinUser = Jellyfin.Data.Entities.User;
#endif
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>
/// The one place that knows Jellyfin's types (design §2). Everything else works on plain snapshots and ids, so the
/// counting rules and the database are the same code under both builds; where Jellyfin 10.10 and 12 differ, the
/// difference is here, behind <c>JELLYFIN_12</c>.
/// </summary>
public sealed class JellyfinHost : ILibraryFacts
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IServerConfigurationManager _server;
    private readonly IApplicationPaths _paths;

    // Jellyfin raises an event per progress report — every ten seconds a stream — and building a snapshot walks up
    // to the series and the library. Five minutes is long enough to spare that and short enough to pick up a rename.
    private readonly ConcurrentDictionary<Guid, (MediaSnapshot? Snapshot, DateTime At)> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, string> _userNames = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<string, string>> _seriesIds = new();

    public JellyfinHost(ILibraryManager library, IUserManager users, IUserDataManager userData, IServerConfigurationManager server,
        IApplicationPaths paths)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _server = server;
        _paths = paths;
    }

    private static readonly BaseItemKind[] RecordedTypes =
        [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.AudioBook];

    // — Items —

    /// <summary>What an item is, or null when it is not something the plugin records (§6.6).</summary>
    public MediaSnapshot? Snapshot(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (_snapshots.TryGetValue(item.Id, out var cached) && now - cached.At < TimeSpan.FromMinutes(5))
        {
            return cached.Snapshot;
        }

        var snapshot = Build(item, withLibrary: true);
        _snapshots[item.Id] = (snapshot, now);
        return snapshot;
    }

    public MediaSnapshot? SnapshotById(Guid itemId) => Snapshot(_library.GetItemById(itemId));

    /// <param name="withLibrary">Which library it is in — a walk up its parents, left out when indexing the whole library.</param>
    private MediaSnapshot? Build(BaseItem item, bool withLibrary)
    {
        if (Kind(item) is not { } kind)
        {
            return null;
        }

        var library = withLibrary ? _library.GetCollectionFolders(item).FirstOrDefault() : null;
        var snapshot = new MediaSnapshot
        {
            ItemId = item.Id,
            Kind = kind,
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            RuntimeTicks = item.RunTimeTicks,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>()),
            LibraryId = library?.Id,
            LibraryName = library?.Name,
        };

        switch (item)
        {
            case Episode episode:
                return snapshot with
                {
                    SeriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId,
                    SeriesName = episode.SeriesName,
                    SeasonId = episode.SeasonId == Guid.Empty ? null : episode.SeasonId,
                    SeasonName = episode.SeasonName,
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    EpisodeNumberEnd = episode.IndexNumberEnd,
                    SeriesProviderIds = _seriesIds.GetOrAdd(episode.SeriesId,
                        _ => new Dictionary<string, string>(episode.Series?.ProviderIds ?? new Dictionary<string, string>())),
                };

            case Audio audio:
                return snapshot with
                {
                    // A book split into chapter files has no album: Jellyfin makes each file an item in the book's
                    // folder, so the folder is the book (seen on 10.10.7, 2026-10-30).
                    AlbumId = audio.AlbumEntity?.Id ?? (audio is AudioBook && audio.ParentId != Guid.Empty ? audio.ParentId : null),
                    AlbumName = audio.Album,
                    Artists = audio.Artists?.ToList() ?? [],
                    AlbumArtists = audio.AlbumArtists?.ToList() ?? [],
                    TrackNumber = audio.IndexNumber,
                    DiscNumber = audio.ParentIndexNumber,
                };

            default:
                return snapshot;
        }
    }

    /// <summary>Films, episodes, other videos, music and audiobooks — never trailers, theme media, extras or Live TV.</summary>
    private static MediaKind? Kind(BaseItem item)
    {
        if (item.ExtraType is not null || item is Trailer || item is LiveTvChannel || item is LiveTvProgram)
        {
            return null;
        }

        return item switch
        {
            Movie => MediaKind.Movie,
            Episode => MediaKind.Episode,
            AudioBook => MediaKind.AudioBook,
            Audio => MediaKind.Audio,
            Video => MediaKind.Video,
            _ => null,
        };
    }

    // — Imports (§8) —

    /// <summary>Every film and episode in the library, indexed for matching an import to it.</summary>
    public LibraryIndex BuildLibraryIndex() => new(
        _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
            })
            .Select(item => Build(item, withLibrary: false))
            .OfType<MediaSnapshot>());

    /// <summary>
    /// The install-time baseline (§8.2): every item this person has watched, as one completed viewing dated by its
    /// last-played date and credited its running time, and every item under way at its position. All estimated:
    /// Jellyfin's "last played" is the last *start*, and its play count counts every start.
    /// </summary>
    public IReadOnlyList<ImportCandidate> WatchState(Guid userId, CountingRules rules)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        var items = _library.GetItemList(new InternalItemsQuery(user) { IsPlayed = true, IncludeItemTypes = RecordedTypes, Recursive = true })
            .Concat(_library.GetItemList(new InternalItemsQuery(user) { IsResumable = true, IncludeItemTypes = RecordedTypes, Recursive = true }))
            .DistinctBy(i => i.Id);

        var candidates = new List<ImportCandidate>();
        foreach (var item in items)
        {
            var snapshot = Snapshot(item);
            var data = _userData.GetUserData(user, item);
            if (snapshot is null || data is null)
            {
                continue;
            }

            var when = new DateTimeOffset(DateTime.SpecifyKind(data.LastPlayedDate ?? item.DateCreated, DateTimeKind.Utc));
            var runtime = snapshot.RuntimeTicks;
            if (data.Played)
            {
                candidates.Add(new ImportCandidate
                {
                    UserId = user.Id,
                    UserName = user.Username,
                    Item = snapshot,
                    Matched = true,
                    SourceKey = item.Id.ToString("N"),
                    Completed = true,
                    StartedAt = when,
                    LastActivityAt = when,
                    CompletedAt = when,
                    FurthestTicks = runtime ?? 0,
                    CreditedSeconds = Completion.Credited(snapshot.Kind, runtime, 0, CompletedBy.Import, rules),
                    Estimated = true,
                    PlayCountNoted = data.PlayCount,
                });
            }
            else if (data.PlaybackPositionTicks > 0)
            {
                var seconds = TimeSpan.FromTicks(data.PlaybackPositionTicks).TotalSeconds;
                candidates.Add(new ImportCandidate
                {
                    UserId = user.Id,
                    UserName = user.Username,
                    Item = snapshot,
                    Matched = true,
                    SourceKey = item.Id.ToString("N"),
                    StartedAt = when,
                    LastActivityAt = when,
                    FurthestTicks = data.PlaybackPositionTicks,
                    ActualSeconds = seconds,
                    WallSeconds = seconds,
                    CreditedSeconds = Completion.Credited(snapshot.Kind, runtime, seconds, null, rules),
                    Estimated = true,
                    PlayCountNoted = data.PlayCount,
                });
            }
        }

        return candidates;
    }

    /// <summary>Where Playback Reporting keeps its database, if it is on this server (§8.3).</summary>
    public string? PlaybackReportingDatabase
    {
        get
        {
            var path = Path.Combine(_paths.DataPath, "playback_reporting.db");
            return File.Exists(path) ? path : null;
        }
    }

    // — People —

    public RecordedUser ToRecorded(JellyfinUser user)
    {
        _userNames[user.Id] = user.Username;
        return new RecordedUser(user.Id, user.Username);
    }

    /// <summary>A person by id, named from a cache: on Jellyfin 12 every lookup is a database round trip.</summary>
    public RecordedUser? Person(Guid userId)
    {
        if (_userNames.TryGetValue(userId, out var name))
        {
            return new RecordedUser(userId, name);
        }

        var user = _users.GetUserById(userId);
        return user is null ? null : ToRecorded(user);
    }

    public IReadOnlyList<RecordedUser> Everyone() =>
#if JELLYFIN_12
        _users.GetUsers().Select(ToRecorded).ToList();
#else
        _users.Users.Select(ToRecorded).ToList();
#endif

    // — Facts for the summary —

    public int? EpisodesInSeries(Guid seriesId)
    {
        if (_library.GetItemById(seriesId) is not Series)
        {
            return null;
        }

        return _library.GetCount(new InternalItemsQuery
        {
            AncestorIds = [seriesId],
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
        });
    }

    /// <summary>
    /// The last file of a book — the one whose completion finishes it: by disc, then track, then name. For a book in one
    /// file, that file.
    /// </summary>
    public Guid? LastFileOfBook(Guid albumId) =>
        _library.GetItemList(new InternalItemsQuery { ParentId = albumId, IncludeItemTypes = [BaseItemKind.AudioBook] })
            .OrderBy(i => i.ParentIndexNumber ?? 0)
            .ThenBy(i => i.IndexNumber ?? 0)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault()?.Id;

    // — Rules —

    /// <summary>The counting rules: the server's resume settings for completion (decided 2026-10-30), the plugin's settings for the rest.</summary>
    public CountingRules Rules(PluginConfiguration settings)
    {
        var server = _server.Configuration;
        return new CountingRules
        {
            MaxResumePercent = server.MaxResumePct > 0 ? server.MaxResumePct : 90,
            MaxAudiobookResumeMinutes = server.MaxAudiobookResume > 0 ? server.MaxAudiobookResume : 5,
            MinimumVideo = TimeSpan.FromSeconds(Math.Max(0, settings.MinimumVideoSeconds)),
            MinimumMusic = TimeSpan.FromSeconds(Math.Max(0, settings.MinimumMusicSeconds)),
            MinimumAudioBook = TimeSpan.FromSeconds(Math.Max(0, settings.MinimumAudioBookSeconds)),
            AbandonAfter = TimeSpan.FromDays(Math.Max(1, settings.AbandonAfterDays)),
        };
    }

    public static RecordingScope Scope(PluginConfiguration settings)
    {
        var kinds = new HashSet<MediaKind>();
        if (settings.RecordMovies)
        {
            kinds.Add(MediaKind.Movie);
        }

        if (settings.RecordEpisodes)
        {
            kinds.Add(MediaKind.Episode);
        }

        if (settings.RecordVideos)
        {
            kinds.Add(MediaKind.Video);
        }

        if (settings.RecordMusic)
        {
            kinds.Add(MediaKind.Audio);
        }

        if (settings.RecordAudioBooks)
        {
            kinds.Add(MediaKind.AudioBook);
        }

        return new RecordingScope
        {
            Kinds = kinds,
            ExcludedUsers = settings.ExcludedUserIds
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToHashSet(),
        };
    }
}
