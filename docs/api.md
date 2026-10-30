# JellyMold Companion — API, version 1

*This document is released under [CC0](https://creativecommons.org/publicdomain/zero/1.0/): write a client from it
under any licence (design §13). The plugin itself is GPL-3.0.*

Base path **`/JellyMold/v1`** on the Jellyfin server. JSON in Jellyfin's own style: PascalCase names, ids as
32-character hex GUIDs, dates in UTC (ISO 8601), durations in **seconds**. Null fields may be left out.

## Signing in, and whose data

Every endpoint takes a person's ordinary Jellyfin sign-in — the same `Authorization: MediaBrowser … Token="…"`
header as the rest of Jellyfin's API. No admin rights are needed for your own data.

- **The data is the signed-in person's.** It is read from their sign-in, never from the path or body.
- **`UserId`** (a query parameter on most endpoints) is honoured only for admins. Anyone else naming another person
  gets **403**.
- **An API key** signs in as an admin with no person, so it must always pass `UserId` (**400** otherwise).
- **No GET changes anything.** Pages are at most 500 items.

| Response | Meaning |
|---|---|
| 401 | Not signed in — and, on `GET /Info`, the plugin *is* installed |
| 404 on `GET /Info` | The plugin is not installed |
| 403 | Signed in, but not allowed (someone else's data, or an admin endpoint) |
| 400 | A bad input; `Detail` says which |

## The objects

### `ItemRef` — what an item was

Kept with every record, so it outlives the item being deleted. Carries what an app needs to merge the same media
across servers.

| Field | |
|---|---|
| `Id` | Jellyfin item id |
| `Type` | `Movie`, `Episode`, `Video`, `Audio` (a song), `AudioBook` |
| `Name`, `OriginalTitle`, `ProductionYear`, `RuntimeSeconds` | |
| `SeriesId`, `SeriesName`, `SeasonId`, `SeasonNumber`, `EpisodeNumber`, `EpisodeNumberEnd` | Episodes |
| `AlbumId`, `AlbumName`, `Artists`, `AlbumArtists` | Music and audiobooks |
| `ProviderIds`, `SeriesProviderIds` | As Jellyfin holds them: `{"Tmdb": "603", "Imdb": "tt0133093"}` |
| `InLibrary`, `RemovedAt` | Whether it is still in the library; when it left |
| `Origin` | `Library`, or `Import` for media only an import mentioned — perhaps never on this server |

### `HistoryEntry` — a viewing, or a listen

One or more sessions of an item, until it is finished or given up on. Watching something again is a new entry.

| Field | |
|---|---|
| `Id` | |
| `Item` | `ItemRef` |
| `Kind` | `Played`, `Marked` (ticked watched without playing), `Imported` |
| `Source` | `live`, `client` (posted by an app after watching offline), or an import's name |
| `Status` | `InProgress`, `Completed`, `Abandoned` (left unfinished for 60 days, or started again from the top) |
| `StartedAt`, `LastActivityAt`, `CompletedAt` | |
| `CompletedBy` | `Position` (played to the completion point), `Mark` (ticked), `Import` |
| `FurthestSeconds` | The furthest point reached |
| `ActualSeconds` | Media time that advanced while playing. Pauses, seeks and buffering excluded. |
| `WallSeconds` | Real time spent playing — differs from actual at other speeds |
| `CreditedSeconds` | **The full running time if completed by playing; otherwise the actual time.** A tick credits nothing. |
| `Estimated` | True for imported history with no real durations |
| `Ordinal` | 1 the first time this person watched it, 2 the second… |
| `Sessions` | How many sessions it took |

**When something counts as completed** (`GET /Info` reports the numbers in force): films, episodes and other videos
at the server's resume percentage (90 by default); audiobooks with fewer than the server's audiobook minutes left (5);
songs once half, or four minutes, has actually been heard.

### `SessionEntry` — one continuous play

`Id`, `ItemId`, `EntryId` (its `HistoryEntry`, or null for a short play that counted for nothing), `Source`, `Client`,
`DeviceName`, `PlayMethod`, `StartedAt`, `EndedAt`, `StartPositionSeconds`, `EndPositionSeconds`, `FurthestSeconds`,
`ActualSeconds`, `WallSeconds`, `PausedSeconds`, `Seeks`, `Counted`, and `EndReason`: `stopped`, `replaced` (another
item started on the same device), `idle` (the app vanished and Jellyfin's idle timer ended it), `session-ended`,
`restart` (the server went down mid-play), `swept`, `looped` (repeat one), `client`.

### `Page<T>`

`{ "Items": [ … ], "TotalRecordCount": 1234, "StartIndex": 0 }`

## Endpoints

### `GET /Info`

Whether the plugin is here, and the rules it counts by.

```json
{
  "Name": "JellyMold Companion", "PluginVersion": "12.0.1.0", "ApiVersion": 1,
  "Features": ["history", "summary", "changes", "clientSessions", "export", "sessions"],
  "Recording": true,
  "Rules": { "CompletionPercent": 90, "AudioBookCompletionMinutesLeft": 5, "MusicListenedFraction": 0.5,
             "MusicListenedSeconds": 240, "MinimumVideoSeconds": 60, "MinimumMusicSeconds": 30,
             "MinimumAudioBookSeconds": 60, "RecordedKinds": ["Audio", "AudioBook", "Episode", "Movie", "Video"] },
  "You": { "UserId": "…", "IsAdmin": false, "Recorded": true, "RecordingSince": "2026-10-30T17:07:48Z" }
}
```

### `GET /Summary?Tz=Europe/London&Recent=5`

The stats page in one request. `Tz` is an IANA time zone (UTC if absent or unknown); a day runs 04:00 to 04:00 in it,
so a 1 a.m. episode belongs to the evening it started. `Recent` (1–50) is how many films and series each list holds.

```json
{
  "GeneratedAt": "…", "AsOf": "<change token>",
  "Films":    { "Watched": 212, "Viewings": 240, "UnderWay": 3, "ActualSeconds": 0, "CreditedSeconds": 0,
                "AverageRuntimeSeconds": 7020, "Longest": FilmRow, "Recent": [FilmRow] },
  "Episodes": { "Watched": 3412, "Viewings": 3500, "ActualSeconds": 0, "CreditedSeconds": 0, "AverageRuntimeSeconds": 2580 },
  "Series":   { "Started": 88, "Finished": 21, "MostWatched": SeriesRow, "Recent": [SeriesRow] },
  "Total":    { "ActualSeconds": 0, "CreditedSeconds": 0, "WallSeconds": 0 },
  "Streaks":  { "CurrentDays": 4, "LongestDays": 37, "LongestFrom": "2026-03-02", "LongestTo": "2026-04-07" },
  "TopFilms": [FilmRow], "TopSeries": [SeriesRow], "Recent": [HistoryEntry],
  "ByYear":   [{ "Year": 2026, "Viewings": 812, "ActualSeconds": 0, "CreditedSeconds": 0 }],
  "Music":      { "Listens": 9120, "ActualSeconds": 0, "CreditedSeconds": 0, "TopArtists": [{ "Name": "…", "Listens": 0 }],
                  "TopAlbums": [{ "Name": "…", "Listens": 0 }], "TopTracks": [{ "Item": ItemRef, "Listens": 0 }] },
  "AudioBooks": { "Finished": 14, "UnderWay": 2, "ActualSeconds": 0 }
}
```

`Watched` counts each film or episode once however often; `Viewings` counts every completed viewing. Films, episodes,
series, totals, streaks and years cover video only; music and audiobooks have their own blocks.

**`FilmRow`**: `Item`, `Finished`, `Viewings`, `LastWatched`, `RuntimeSeconds`, `PositionSeconds` (how far into a film
under way; null when finished), `ActualSeconds`, `CreditedSeconds`.

**`SeriesRow`**: `SeriesId`, `Name`, `ProviderIds`, `EpisodesWatched`, `EpisodesInLibrary` (null once the series has
left the library), `Finished`, `InLibrary`, `LastWatched`, `ActualSeconds`, `CreditedSeconds`.

### `GET /Films?Order=recent|title&StartIndex=0&Limit=100` · `GET /Series?Order=recent|mostWatched|mostComplete|title&…`

Every film, or every series, watched or under way: the stats page's *More* lists. `Page<FilmRow>` / `Page<SeriesRow>`.

### `GET /History?From=…&To=…&Kind=Movie,Episode&StartIndex=0&Limit=100`

The history, most recent activity first. `Page<HistoryEntry>`. `Kind` takes any of `Movie`, `Episode`, `Video`,
`Audio`, `AudioBook`, comma-separated.

### `GET /Items/{itemId}/History`

One item's entries (including any under an earlier copy of the same media) and its sessions. Works for items gone
from the library. `{ "Entries": [HistoryEntry], "Sessions": [SessionEntry] }`.

### `GET /Changes?Since=<token>&Limit=500`

Everything changed since a token, for keeping a cached copy up to date by deltas.

```json
{ "Changed": [HistoryEntry], "Removed": ["<entry id>"], "Next": "<token>", "More": false, "Resync": false }
```

- First call: no `Since` — returns everything, in pages.
- **`More: true`** — call again straight away with `Next`.
- **`Removed`** — entries that no longer count (a tick taken back within a day, an import undone, a record superseded
  by a better source's account of the same viewing). Drop them.
- **`Resync: true`** — the token was from another database (it was restored from a backup): this answer starts from
  the beginning, and the cached copy should be replaced rather than updated.

### `GET /Sessions?ItemId=…&From=…&To=…` · `GET /Sessions/{sessionId}`

The diagnostics. The second returns the session with its raw events — start, pause, resume, seek, stop, idle-stop,
played-flag-on/off, resumable-on/off (when Jellyfin's own watched tick or resume point changed, and why), and more:
`{ "Session": SessionEntry, "Events": [{ "At", "Kind", "ItemId", "SessionId", "PositionSeconds", "Detail" }] }`.

### `POST /ClientSessions`

Watching an app did with no server contact (a download on a plane). Post it **before** replaying the stop and the
watched tick to Jellyfin, so the tick is recognised as confirming it.

```json
{ "Sessions": [ {
    "ClientSessionId": "3F2C…", "ItemId": "…",
    "StartedAt": "2026-10-28T19:02:11Z", "EndedAt": "2026-10-28T19:47:40Z",
    "StartPositionSeconds": 0, "EndPositionSeconds": 2600, "FurthestPositionSeconds": 2600,
    "ActualSeconds": 2600, "WallSeconds": 2600, "PausedSeconds": 120, "PlaybackRate": 1.0,
    "DeviceName": "iPhone", "Client": "JellyMold" } ] }
```

Answer: `{ "Results": [{ "ClientSessionId", "Result": "Accepted" | "Duplicate" | "Rejected", "Reason", "SessionId" }] }`.

- At most 200 a request. `ClientSessionId` is yours, up to 100 characters; posting the same one twice is a
  `Duplicate`, as is a session the server also heard live (`overlapsLiveSession`).
- `Rejected` reasons: `unknownItem`, `notRecorded`, `inTheFuture`, `olderThanAYear`, `badTimes` (ended before it
  started, or longer than a day), `implausibleSeconds`, `positionOutOfRange`, `badClientSessionId`, `notRecording`.

### `GET /Export?Format=json|csv`

The signed-in person's whole history, as a file. Admins may pass `UserId`, or `All=true` for everyone.

### Imports — past history (design §8)

Everything watched before the plugin was installed comes from imports. **Jellyfin's own watch state** is imported for
each person automatically, once, as their baseline. The rest are run by hand:

1. **`POST /Imports/{source}/Preview`** reads the source and answers what importing it would do. Nothing is written.
2. **`POST /Imports/{importId}/Commit`** applies exactly that.
3. **`POST /Imports/{importId}/Undo`** takes it back: its entries stop counting (they are kept, as `Undone`), and
   anything it had replaced counts again. An import undone can be previewed and committed again.

| `source` | Who | Needs a file |
|---|---|---|
| `jellyfin` | Anyone, for themselves (it already ran) | no |
| `playback-reporting` | Admin only — it holds everyone's plays | no |
| `trakt-export` | Anyone, for themselves | Trakt's history JSON, or its watched-movies / watched-shows JSON |
| `letterboxd` | Anyone, for themselves | `diary.csv` or `watched.csv` |
| `csv` | Anyone, for themselves | The CSV below |

Admins may pass `UserId` to import for someone else. **The file is the request body** (`Content-Type: text/csv` or
`application/json`), or a file in a multipart form; at most 20 MB.

The preview and the commit answer with a report:

```json
{ "ImportId": "…", "Source": "letterboxd", "UserId": "…", "Rows": 412,
  "New": 380, "AlreadyImported": 0, "Duplicates": 22, "Replaces": 6, "AfterCutover": 4, "Unmatched": 31,
  "Sample": [{ "Title": "Paris, Texas (1984)", "When": "2024-03-08T12:00:00Z", "Outcome": "New, not in this library" }],
  "UnmatchedTitles": ["…"], "Notes": ["Letterboxd gives no provider ids, so films are matched by title and year."] }
```

- **`AlreadyImported`** — importing twice adds nothing.
- **`AfterCutover`** — dated after recording began for that person; live recording has it.
- **`Duplicates`** — the same viewing (within 12 hours; 24 against Jellyfin's estimated dates) is already recorded from
  a better source, and this one is kept but not counted. **`Replaces`** — this import is the better source. The order
  is live › offline sessions › Playback Reporting › Trakt › files › Jellyfin's watch state.
- **`Unmatched`** — not in this library; kept with the title and ids the source gave (`Origin: "Import"`).

Imported entries are `Kind: "Imported"`. Those from sources without durations (Jellyfin's watch state, Trakt, files)
are `Estimated: true` and credited the running time where it is known. Playback Reporting brings real seconds, and its
plays become sessions, so they count towards streaks.

`GET /Imports` lists the caller's imports (admins: `All=true` for every one); `GET /Imports/Sources` says which
sources can be used here and now.

**The CSV:** a header row, then one row per viewing. Columns, any order, case-insensitive:

| Column | |
|---|---|
| `date` | Required. `2024-03-08` (read as midday UTC) or a full date and time with offset |
| `type` | `movie` (the default), `episode`, `audio`, `audiobook` |
| `title` | The film's title, or the episode's |
| `year` | For films |
| `tmdb`, `imdb`, `tvdb` | Provider ids. For an episode, the **series'** ids |
| `series`, `season`, `episode` | For episodes |
| `seconds` | Time actually watched, if known |
| `completed` | `yes` (the default) or `no` |

### Admin only

- `GET /Admin/Status` — recording or not, any problem in plain words, database size and counts, backups, people.
- `POST /Admin/Backups` — back up now.
