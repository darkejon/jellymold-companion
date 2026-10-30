# JellyMold Companion — design

Phase 0 of the brief, `docs/brief.md` (copied from the app repository). Written 2026-10-30; decisions
recorded the same day in §19.

---

## The shape of it, on one page

A Jellyfin server plugin that hears every playback on the server — whichever app is playing, and films,
episodes, music and audiobooks alike — and writes it down in its own database, per person, for good. Each
record carries a copy of what the item *was* (title, series, episode, album, artist, running time, provider
ids), so it outlives the file being deleted.

**Three levels of record.**

| | What it is | Example |
|---|---|---|
| **Event** | Something Jellyfin said, when it said it | *21:14:05 — paused at 23:10, iPhone* |
| **Session** | One continuous play | *Tuesday, 25 minutes of episode 3, ended when the app vanished* |
| **Viewing** | What a person means by "I watched it" | *Tuesday's 25 minutes and Wednesday's 20 — episode 3, watched* |

**What counts.**

- **Actual time** is media time that moved forward while playing. Pauses, seeks, buffering and an app
  that vanished mid-episode add nothing.
- **Watched** means the furthest point reached the server's threshold — 90% of the running time unless an
  admin changed it. Reaching it **credits** the whole running time. Anything else — unfinished, or ticked
  watched — is credited only the time actually watched. A tick still counts as watched; it just adds no time.
- **History imported at install is the one exception**: it sets the baseline, so each item watched before
  the plugin existed is credited its running time. From then on, time grows only from actual watching.
- **Watching again is a second viewing.** Three times through a film is three viewings; a song on repeat is
  a listen each time round.

**Who can read it.** Every user reads their own history with an ordinary token; admins read everyone's.

**What it never does.** Delete history. Records survive the media going, plugin upgrades (migrations only
ever add), and a damaged disk (nightly backups). There is no user-facing delete; there is export.

**On top of the record**, the stats page in one request instead of every played item from every server,
a change feed so the app's cached copy updates by deltas, and an endpoint for watching done offline.

---

## 1. What exists already, and why build this

Read from each project's source on 2026-10-30.

| | What it keeps | Readable by a non-admin? | Survives deletion? |
|---|---|---|---|
| **Jellyfin's own watch state** | Played, play count, last played, position — one of each per item | Yes, but only through an item that still exists | The data does; the API hides it |
| **Jellyfin's activity log** | "X is playing Y" lines | No (admin only) | Kept 30 days |
| **Playback Reporting** (plugin) | One row per play of 20 s or more, wall-clock seconds, the item's name as text | No (admin only since v16) | Yes, as a name — but trimmed after 3 months by default |
| **Jellystat** (separate server) | Polls sessions, imports Playback Reporting | One dashboard login, not per user | Yes |
| **Streamystats** (separate server, by Streamyfin's author) | Polls sessions | Yes, per user | Yes, and re-attaches history to a re-added item by provider ids — the same idea as §7 |
| **StarTrack, AchievementBadges** (plugins) | A hand-kept diary; badges | Yes | Not playback |

**Nothing inside Jellyfin keeps a permanent, per-play, per-user history that a non-admin can read.** The
closest is Streamystats, which is a second server to run beside Jellyfin rather than a plugin, and which an
app cannot reach with a Jellyfin sign-in. The case for building is three properties together: permanent,
per person (admin or not), and readable by an app with an ordinary sign-in.

---

## 2. How it sits inside Jellyfin

| Piece | Job |
|---|---|
| `Plugin` | What Jellyfin loads. Owns the settings and serves the dashboard page. |
| `ServiceRegistrator` | Tells Jellyfin's dependency container about everything below. |
| `PlaybackListener` | Event consumers for playback, sessions and users, and a hosted service for the user-data and library events that have no consumer form. Turns each into a small message on a queue and returns at once. |
| `Recorder` | **The one writer.** Takes messages off the queue in order and applies them: opens, updates and closes sessions; assembles viewings; writes events. |
| `HistoryStore` | The SQLite database: schema, migrations, reads, writes. |
| `Sweeper` | Finishes sessions nothing finished: a stop Jellyfin never sent, a server restart mid-play. |
| `BackupTask` | A Jellyfin scheduled task (visible under Dashboard → Scheduled Tasks): nightly copy, keeps a few. |
| `Importers` | One per source, each producing a *preview* and then a *commit*. |
| Controllers | The HTTP API under `/JellyMold/v1`. |

**How it listens.** Jellyfin 10.10 offers two ways to hear playback, and they behave differently:

- **Event consumers** (`IEventConsumer<T>`), for start, progress, stop, session ended and user deleted.
  Jellyfin awaits them inside the client's own request and catches anything they throw — so one client's
  start, progress and stop reach the plugin **in order**, and a fault in the plugin cannot break playback.
- **Plain events**, for *user data saved* and *item removed*, which have no consumer form. The progress
  and start events also exist in this form, but Jellyfin fires those on background tasks in no guaranteed
  order (a stop can arrive before its start), and an exception in a progress handler fails the client's
  request. So: consumers wherever they exist, plain events only where they do not, every handler wrapped.

**Why one writer, off Jellyfin's thread.** A consumer runs inside the client's HTTP request, so database
work there would slow every client's progress report. The listener only drops a message on a queue and
returns; a single writer takes them off in order. That makes *the order events happened* the *order they
are written*, which is what the counting rules depend on. A bug in the plugin must cost a record, never a
playback.

**Everything Jellyfin-specific goes through one adapter** (`IJellyfinHost`: look up an item, a user, the
server's resume settings). The counting rules, the database and the API never touch Jellyfin types
directly, which makes them testable without a server and makes 10.11 (§14) a change to one file.

---

## 3. The record

SQLite, one file, timestamps as UTC milliseconds. Ids are Jellyfin's 32-character GUIDs (the form its API
returns). Names in `code` are the column names the build will use.

### `users`

| Column | |
|---|---|
| `user_id` | Jellyfin user id |
| `name` | Latest known name — kept after the user is deleted |
| `recording_since` | When live recording began for this person: **the cutover** (§8). Null when not recorded. |
| `deleted_at` | When Jellyfin deleted the account, if it has |

### `media` — the snapshot that outlives the file

One row per item ever played or imported, refreshed whenever the item is seen again and frozen when it
leaves the library.

| Column | |
|---|---|
| `item_id` | Jellyfin item id |
| `kind` | `Movie`, `Episode`, `Video`, `Audio` (a music track), `AudioBook` |
| `name`, `original_title`, `production_year`, `premiere_date` | |
| `runtime_ticks` | Running time as last known |
| `series_id`, `series_name`, `season_id`, `season_name`, `season_number`, `episode_number`, `episode_number_end` | For episodes |
| `album_id`, `album_name`, `artists`, `album_artists`, `track_number`, `disc_number` | For music, and for audiobooks, which Jellyfin describes in the same terms (the book as the album, the author as the artist) |
| `provider_ids`, `series_provider_ids` | JSON, exactly as Jellyfin holds them (`{"Tmdb":"…","Imdb":"tt…","Tvdb":"…"}`) |
| `library_id`, `library_name` | Which library it was in |
| `first_seen_at`, `updated_at` | |
| `removed_at` | *Deleted from the library on…* Null while it is there. |

### `media_keys`

Every cross-server name an item answers to: `tmdb:603`, `imdb:tt0133093`, for an episode
`tvdb-series:73244|s03e07`, and for a track its MusicBrainz id where there is one, otherwise artist, album
and title together. Used to recognise the same media under a new item id (a re-rip, a re-added
library), to match imports, and to never count a viewing twice. The app does its own merging from the
provider ids each record carries, as it does today; these keys are the plugin's own.

### `sessions` — one continuous play

| Column | |
|---|---|
| `session_id` | Ours |
| `user_id`, `item_id` | |
| `viewing_id` | The viewing it belongs to. Null for a short play that joined nothing (§5.5). |
| `source` | `live`, `client` (reported by an app after watching offline), or an import |
| `jf_session_id`, `play_session_id` | Jellyfin's session (per device and app) and the play's own id, when the client sends one |
| `client`, `client_version`, `device_name`, `device_id`, `play_method`, `media_source_id` | |
| `started_at`, `ended_at`, `last_seen_at`, `received_at` | `received_at` differs from `started_at` for watching reported later |
| `start_position`, `end_position`, `furthest_position` | Ticks |
| `runtime_ticks` | Running time at the time — the completion maths uses this, not today's value |
| `actual_seconds` | Media time advanced at normal play (§5.1) |
| `wall_seconds` | Real time spent playing (not paused) |
| `paused_seconds`, `seek_count` | |
| `playback_rate` | Only for client sessions, which can say. Jellyfin's own reports carry no speed at all. |
| `end_reason` | `stopped`, `replaced` (another item started on the same device), `idle` (Jellyfin's idle timer closed it), `session-ended` (the app's connection to Jellyfin ended mid-play), `restart` (the server went down mid-play), `swept`, `client` |

### `viewings` — "I watched it"

For music and audiobooks the same record is a *listen*; the API calls both an entry in the history. The
table keeps one name because the rules are the same.

| Column | |
|---|---|
| `viewing_id` | |
| `user_id`, `item_id` | Latest item id; `media_keys` carry it across a re-rip |
| `kind` | `played`, `marked` (ticked watched without playing), `imported` |
| `source` | `live`, `client`, `jellyfin`, `playback-reporting`, `trakt`, `simkl`, `letterboxd`, `trakt-export`, `csv` |
| `import_id` | Which import added it, so the import can be undone |
| `status` | `in-progress`, `completed`, `abandoned`, `retracted` (a mistaken mark undone at once — §6.4), `undone` (its import was undone) |
| `started_at`, `last_activity_at`, `completed_at` | |
| `completed_by` | `position`, `mark`, `import` |
| `furthest_position`, `runtime_ticks` | |
| `actual_seconds`, `wall_seconds` | Sum of its sessions |
| `credited_seconds` | §5.3 |
| `estimated` | True where the source gave no real durations (Jellyfin's own watch state, Trakt) |
| `play_count_noted` | Jellyfin's play count at import, kept as information |
| `superseded_by` | Set when another source's record of the same viewing wins (§8.1). Kept, not counted. |

### `events` — the raw log

| Column | |
|---|---|
| `at`, `user_id`, `item_id`, `session_id`, `kind`, `position`, `detail` (JSON) | |

Kinds: `start`, `pause`, `resume`, `seek`, `stop`, `idle-stop`, `reopened`, `session-ended`, `replaced`, `orphan-stop` (a stop with no
play before it), `played-flag-on`, `played-flag-off`, `resumable-on`, `resumable-off`, `item-removed`,
`user-deleted`, `import-committed`, `import-undone`, `plugin-started`. **Kept for good**: about eight per
session, small.

### `progress_samples` — kept 30 days

Every progress report from the client: `session_id`, `at`, `position`, `paused`. About 360 an hour of playback,
which is the one table big enough to matter (§10.5), and the one nobody needs after a month: the session
already holds what the samples added up to. Retention is a setting; *forever* is allowed.

### `imports`, `tombstones`, `meta`

`imports` — one row per import: source, user (or server-wide), when previewed, committed, undone, and what
it added. `tombstones` — ids removed from the change feed by an undone import (§9.4). `meta` — the
database's own identity (`db_id`, regenerated on a restore), the change counter, when it was created and by
which plugin version.

---

## 4. From playback to record

### What the listener hears

| Jellyfin event | What the recorder does |
|---|---|
| Playback started | Close any open session on the same Jellyfin session for a *different* item (`replaced` — Jellyfin sends no stop for it). Open a session per user on the play. Snapshot the item. Event `start`. |
| Playback progress **from the client** | Find the open session (open one if none — a client that never sent a start, or one that came back after an idle stop, §6.1). Add the interval since the last sample to the counters (§5.1). Note a pause, resume or seek as an event. Update `furthest_position`. Write the sample. |
| Playback progress **invented by the server** | **Ignored.** See below. |
| Playback stopped | Close the session with its last interval counted. Decide `end_reason` (§6.1) — and for an idle stop, use the last *real* position, not the one in the stop. Attach it to a viewing (§5.4). A stop with no open session is logged as `orphan-stop` and credits nothing (§6.3). |
| Session ended | Close anything still open on it at its last real report (`session-ended`). Jellyfin removes a session — the app's connection closing, a sign-out, a shutdown — **without** a stop for what it was playing. |
| User data saved | Log played-flag and resumable changes as events (§6.1 says why). A *mark played / unplayed* goes to §6.4. |
| Item removed from library | Set `media.removed_at`. For a series, season or album, everything in it too: Jellyfin removes the children with **one** event for the parent. Log it. Remove nothing. |
| User deleted | Set `users.deleted_at`. Log it. History is kept; an admin can remove it from the settings page. |

### Jellyfin invents progress, and the plugin must not believe it

While something is playing and not paused, Jellyfin's server runs a one-second timer of its own that adds
exactly one second to the last position the client reported and announces it as a progress event — flagged
*automated*. It always adds one second a second, whatever the real speed, and it keeps going when the client
has vanished. Counting those would turn an app killed mid-episode into five to ten minutes of watching that
never happened, so **only reports from the client count**: for time, for position, and as proof the client is
still there.

### Which session a report belongs to

A report is matched to an open session by the client's **play session id** when it sends one, otherwise
by **Jellyfin session + item**. JellyMold sends no play session id today (§17 asks it to), so the second is
the rule that will carry most traffic at first; it is safe because one Jellyfin session — one app on one
device — plays one thing at a time.

### Open sessions are written down as they go

Each progress report updates the session's row, not only memory. A server that restarts mid-film therefore
loses at most one interval: on startup the sweeper closes every open session at its last sample with
`end_reason = restart`. The sweeper also runs every few minutes and closes anything with no real report for
15 minutes — longer than Jellyfin's idle timer (which fires 5 to 10 minutes after the last report), so it
only ever catches what Jellyfin did not.

---

## 5. Counting rules

Each rule below is a function of a session's samples, tested on its own with made-up sample streams (§15).

### 5.1 Actual seconds

Take consecutive reports *a* and *b* from the client. Δwall is the clock time between them, Δpos the change
in position. Jellyfin's reports carry **no playback speed** — there is no field for it anywhere in 10.10 —
so the rules are written not to need one.

| The interval | Actual seconds | Wall seconds | Also |
|---|---|---|---|
| *a* was paused | 0 | 0 | paused += Δwall |
| Normal play: 0 ≤ Δpos ≤ Δwall × 3.5 + 2 s | Δpos | Δwall | |
| Anything else — a seek either way, a skip button, a jump | min(Δwall, 15 s) | the same | seeks += 1 |

3.5 is a little above the fastest any client plays (JellyMold's ceiling is 3×), and is a setting.

What that means in practice:

- **Pauses count nothing**, and the time is kept separately.
- **A seek counts only the playing before it.** JellyMold reports a seek the moment it happens, so the
  interval before a seek is a few seconds long and the credit is a few seconds. A client that reports
  lazily is wrong by at most one report interval (10 s) per seek, which the 15 s cap bounds.
- **The cost of having no speed:** a short skip (ten seconds forward) inside an interval of ordinary play
  can pass as play, because 3.5× has to be allowed for. Worst case a few seconds per skip on a client that
  reports skips late; none on JellyMold, which reports them as they happen.
- **Rewinding and watching again counts again.** Two minutes watched twice is four minutes of your life.
  (How much of the *film* has been seen is a different number — `furthest_position` — and is what
  completion uses.)
- **Speed is honest both ways.** At 2× for ten minutes: actual 20 minutes of film, wall 10 minutes of
  evening. The app can show either.
- **Buffering** adds to neither: the position did not move.
- **An app that vanishes adds nothing.** Jellyfin's idle stop arrives minutes later at the same position:
  Δpos = 0, actual 0.

### 5.2 Completion

A viewing is **completed** when its furthest position reaches the server's own threshold — **decided
2026-10-30: follow Jellyfin's resume settings**, so the stats never disagree with Jellyfin's watched tick:

| Kind | Completed when | Jellyfin setting |
|---|---|---|
| Films, episodes, other video | 90% of the running time is reached | `MaxResumePct` (90 by default) |
| Audiobooks | fewer than 5 minutes remain | `MaxAudiobookResume` (5 by default). Jellyfin has its own rule for books because 90% of a twelve-hour book would leave seventy minutes unheard. |
| Music | **half the song, or four minutes, has actually been heard** — whichever comes first. Last.fm's rule, decided 2026-10-30. Measured in seconds heard, not position, so skipping to the middle does not count. | None used: Jellyfin's rule for anything under five minutes is "played once 5% is heard", which would count a song skipped after ten seconds as listened. |

`completed_at` is the moment it crossed, from the sample, not the moment the session ended.

**One server rule not followed:** Jellyfin also ticks any video shorter than five minutes
(`MinResumeDurationSeconds`) as soon as 5% of it has played. Following it would count a four-minute clip watched
after twelve seconds, so the plugin applies the percentage to everything and the two can disagree for very short
videos. Seen in the Docker test, whose generated films are four minutes long.

**An audiobook in several files.** One in a single file is one item. One split into a file per chapter is,
in Jellyfin, one item per file under the book; each file is recorded on its own, and the *book* counts as
finished when its last file is completed. The Docker test library will carry both shapes, because this is
the part most likely to differ from what is written here.

Position-based, like Jellyfin's own played flag: skipping to the end of a film completes it. Coverage
(how much of the timeline was actually seen) could be kept as well; it is not in v1, because it would be
the only number that disagrees with Jellyfin's tick and nothing in the app asks for it.

**The threshold applies going forward.** Each viewing records the threshold it was judged by; changing the
setting does not rewrite the past. A *recalculate* button on the admin page can re-judge everything if
that is ever wanted.

### 5.3 Credited time

Decided 2026-10-30:

- **Completed by reaching the threshold**: the full running time — the 90% rule.
- **Everything else is credited only the time actually watched**, never more than the running time: an
  unfinished viewing, and a viewing completed by a **tick** rather than by the position. So ticking
  something watched without playing it counts it as watched and credits **nothing**; and when JellyMold
  ticks an episode watched as its credits start at 85%, the viewing counts as watched and is credited the
  minutes actually played.
- **History imported at install sets the baseline.** The first import of Jellyfin's own watch state (§8.2)
  credits each watched item its running time, so the totals start from a real figure rather than zero.
  Other imports of past history without durations (Trakt, Letterboxd and the other files) are treated the
  same way, since they too describe watching from before the plugin existed. Sources with real durations
  (Playback Reporting, client sessions) follow the live rules.
- After that, **time only ever grows from actual watching.**
- **Music counts and credits on two different marks.** A song *counts as listened* at half or four minutes
  (§5.2), but its full running time is credited only once 90% of it has been heard; before that, the seconds
  actually heard. Half a song and a skip is the commonest thing in music, and crediting it in full would add
  time nobody listened to. Films, episodes and audiobooks have one mark for both.

Both numbers are always available: actual and credited, per viewing and in every total. The headline figure
in the app is credited time (decided 2026-10-30), with actual time beside it.

### 5.4 Viewings and rewatches

When a session ends it joins a viewing. Find this person's **latest viewing of the same media** (same
item, or a shared media key):

| The latest viewing is… | This session… | Result |
|---|---|---|
| None | — | **New viewing** |
| Completed | starts at or past (threshold − 5 points) and within 6 hours of its last activity | **Joins it.** Watching the credits after a break, or a second session that ran on past 90%. |
| Completed | anything else | **New viewing** — a rewatch |
| In progress, last active more than 60 days ago | — | The old one becomes **abandoned**; **new viewing** |
| In progress | starts in the first 10% and at least 10 points before where it got to | The old one becomes **abandoned**; **new viewing** — started again from the top |
| In progress | anything else | **Joins it** |

So:

- **Episode 3 over two evenings is one viewing.** Wednesday resumes where Tuesday stopped.
- **Episode 3 again next month is a second viewing**, because the first completed.
- **Starting a film, giving up at 40%, and starting it again from the beginning a week later** is an
  abandoned viewing (its 40% of time still counts as time, but not as a watched film) and a new one.
- An abandoned viewing never counts as *watched*, so nothing is double-counted.

**Loops.** A player on *repeat one* does not always stop and start again; some just jump back to the
beginning. A report that jumps from past 90% to the first few seconds therefore closes the session there
and opens a new one, so each time round is a play of its own — a song on repeat counts every
time, and the jump is not mistaken for a seek.

Sixty days and the other numbers are settings with these defaults; each has a test.

### 5.5 Short plays

A session under the minimum (a setting per kind: 60 seconds of actual time for video and audiobooks, 30 for
music, where a skipped song is the norm) that does not complete anything:

- **joins** an in-progress viewing if it would (a minute of resuming something you are watching is real),
- otherwise is **kept as an uncounted session** — no viewing is created. The seconds are on record; they
  simply do not make "I watched it". Browsing a film's opening to see what it looks like is not a viewing.

### 5.6 Worked example

A 44-minute episode.

1. **Tuesday 21:00.** Plays from 0:00. Pauses at 20:00 for five minutes, resumes. At 25:00 the app is
   killed by an update. Jellyfin's idle timer stops the session about five minutes later at 25:00.
   *Session 1: actual 25:00, wall 25:00, paused 5:00, end `idle`. Viewing A opens, furthest 25:00 (57%),
   in progress, credited 25:00.*
2. **Wednesday 20:30.** Resumes at 24:30, plays to 41:00, stops. *Session 2: actual 16:30. Viewing A
   crosses 90% at 39:36 → completed at that moment. Actual 41:30 (thirty seconds watched twice), credited
   44:00.*
3. **Wednesday 20:50.** Reopens it at 41:00 to watch the end credits. *Session 3 starts past 85% within
   six hours of A's last activity → joins A. A's actual becomes 44:30; still one viewing.*
4. **A month later.** Starts it from 0:00. *A is completed and this starts at 0% → viewing B, a rewatch.
   Ordinal 2.*

---

## 6. The awkward cases

### 6.1 Abandoned sessions — the 2026-10-29 incident

An app update closed JellyMold mid-episode; Jellyfin ended the session five minutes later; a later session
ran it to 96%; earlier, the episode had briefly left Continue Watching for a reason nobody could
reconstruct.

**What Jellyfin's source says happens when an app vanishes mid-play.** Every five minutes Jellyfin looks for
plays with no report from the client for over five minutes, and stops them — 5 to 10 minutes after the last
report. It sends that stop **at the position its own one-second timer has reached** (§4), which by then is 5
to 10 minutes *past* where the viewer actually was. And it judges *watched* from that position like any
other stop. So:

- **Resume lands late.** The resume point Jellyfin keeps is the invented one, minutes ahead of where the
  viewer left.
- **A play abandoned near the end can be marked watched.** An episode left at 80% can be stopped at 95%,
  ticked played, its position reset — and **it leaves Continue Watching.** Starting it again clears the tick
  (Jellyfin does that on every start), and it comes back.

That is one plausible account of the 2026-10-29 episode that briefly vanished. It is not proven — nothing
recorded the session — but it is exactly the kind of thing this plugin makes provable next time, and it
suggests an idea (§16, idea 10) and a note for the app (§17).

**What the plugin records about each part:**

- **The vanished session** ends `idle`, at the **last real report's** position and time; the stop's own
  position is ignored. The test: a stop arriving more than five minutes after the client's last report.
  Jellyfin marks nothing to say the stop was its own, but it never carries a play session id — so once
  JellyMold sends one (§17), the test becomes exact for JellyMold.
- **A client that comes back.** If real reports for the same item resume on the same Jellyfin session
  within 30 minutes of an idle stop (a long network drop while a buffered stretch played on), the session
  is **reopened** rather than a new one begun, and the gap is counted by §5.1's ordinary rules: if the
  position moved on as fast as the clock, it was playing all along.
- **The later session** to 96% completes the viewing in the ordinary way.
- **Leaving Continue Watching** is a change in Jellyfin's own user data: the played flag went on, or the
  position went to zero. The recorder keeps the last known played flag and position per person and item,
  and logs a `played-flag-on/off` or `resumable-on/off` event *only when one changes*, with the device and
  the reason Jellyfin gave (playback finished, marked played, playback started…). Continue Watching is
  exactly *not played, position above zero*, so the question "when did this leave Continue Watching, and
  why?" becomes one query — and an idle stop that ticked something played will show as exactly that.

Logging changes rather than every save keeps it small: Jellyfin saves user data on every progress report.

### 6.2 Server restarts

Covered in §4: open sessions are on disk, closed at their last sample with `end_reason = restart` on the
next start.

### 6.3 Offline viewing

JellyMold plays downloads with no server and keeps where it got to (`WatchStateQueue`). Today, back online,
it replays a **stop** at the furthest position and then a **mark played** if finished. To the plugin that
looks like a stop with no play before it, then a tick — no time, and the tick would read as *marked without
playing*.

So the API takes **client sessions** (§9.3): the app posts what it watched offline — item, when, from and to
which position, actual seconds — *before* replaying the stop and the mark. Each is marked `client`.
Duplicates are refused: by the app's own id for the session (posting twice changes nothing), and by
overlap — a client session covering the same person, item and time as a live session is the same watching.

Then the replayed stop is an `orphan-stop` (logged, credits nothing) and the mark falls under §6.4's first
rule and confirms the client session's viewing.

### 6.4 Marked watched or unwatched

Jellyfin tells the plugin *why* user data was saved, and a tick from a person or an app (`TogglePlayed`) is
distinguishable from Jellyfin's own judgement at the end of a play (`PlaybackFinished`). Two things still
need care:

- **JellyMold marks things played itself**: when an episode finishes, `markPlayed` calls Jellyfin's *mark
  played* — the same call a person makes by ticking it, with the same reason.
- **Jellyfin clears the tick every time something starts playing.** That is a `played-flag-off` event with
  the reason *playback started*, and it is not an unmark.

So a tick is only a *manual* mark when nothing explains it:

| A *mark played* arrives… | Result |
|---|---|
| While a session of that item is open, or within 10 minutes of one ending or being received | It **confirms that playback**: the session's viewing is completed. If the position had not already reached the threshold, it is `completed_by = mark` and credited the time actually played (§5.3). No new viewing. |
| Within 10 minutes of a viewing of it completing | Nothing new — a duplicate. |
| Otherwise | A **marked viewing**: completed, so it counts as watched, with no actual time and **no credited time** (decided 2026-10-30). |

| A *mark unplayed* arrives… | Result |
|---|---|
| Within 24 hours of a marked viewing of that item | That viewing is **retracted** — kept, not counted. A slip of the thumb should not be on the record for good. |
| Otherwise | Logged as an event. History is not changed: having watched something stays true. |

Marking a whole series (or album) watched in Jellyfin marks each episode (or track), which arrives as one
mark each and becomes one marked viewing each.

### 6.5 Several people on one play

Jellyfin lets a session carry more than one user. Each gets their own session row and their own viewing.

### 6.6 What is not recorded

- **Recorded from v1 (decided 2026-10-30):** films, episodes, other videos (home videos), **music and
  audiobooks** — in exactly the same way, with only completion and the short-play minimum differing by kind
  (§5.2, §5.5). JellyMold does not play music or books, but a JellyMold Music or JellyMold Books may follow
  as separate apps, and any other app can read it meanwhile.
- **Never**: trailers, theme songs and theme videos, extras (featurettes, deleted scenes), Live TV, and
  e-books (reading is not playback).
- **Users switched off** on the settings page.

Short plays are recorded but uncounted (§5.5), so nothing is silently lost.

---

## 7. Surviving deletion

Every session and viewing points at a `media` row holding the item as it was. When Jellyfin removes the item,
the row gains `removed_at` and nothing else changes. The API returns removed items like any other, with
`InLibrary: false`, and the app can still merge them across servers by their provider ids.

If the same media comes back under a new item id — a better rip, a rebuilt library — its media keys match,
so the new item continues the old item's viewings (resumes the in-progress one, counts as a rewatch after a
completed one) instead of starting from nothing.

---

## 8. Importing past history

**Everything before the cutover comes from imports; everything after it from live recording.** The cutover
is `users.recording_since`, set when recording starts for that person. Imported records dated after it are
skipped: live recording has them, better. **The cutover cannot be moved by hand** (decided 2026-10-30): the
brief allowed moving it earlier, and nobody could name a case for it. An import that got something wrong is
undone on its own (§8.1).

### 8.1 Rules every import follows

- **Preview, then commit.** An import first reports what it would add — new viewings, ones that duplicate
  something already there, items it could not match — and writes nothing until committed.
- **Undo.** Undoing an import marks its viewings `undone`; any viewings it had superseded count again.
  Undoing an *import* is not deleting *history*: the rows stay, with the reason.
- **Idempotent.** Every imported record carries its source's own key (a Playback Reporting row id, a Trakt
  history id, a user and item and date for Jellyfin's watch state). Importing twice adds nothing.
- **Matching** by provider ids to the library. **Unmatched items are kept** with whatever the source knew
  (title, year, ids) — the media may never have been on this server, and the app may hold it elsewhere.
- **Never double-count.** Two records of one viewing — from two sources, or an import and live — are the
  same viewing when they are the same person and media and their dates are within 12 hours (24 for
  Jellyfin's estimated dates). The better source wins and the other is kept as `superseded`:
  live › client › Playback Reporting › Trakt, Simkl › files › Jellyfin watch state.

### 8.2 Jellyfin's own watch state (no account needed)

For each person: every played item — film, episode, track, audiobook — becomes one completed viewing dated by
its last-played date, credited its running time, marked `estimated`. Every started item (a position, no tick)
becomes an in-progress viewing credited its position. Runs automatically for each recorded person when
recording starts. **This is the baseline** (decided 2026-10-30): the one place a tick earns its running time,
so the totals start from everything already watched; it is estimated, and it can be undone.

Three things Jellyfin's source says about these numbers, which keep the import honest:

- **"Last played" is the last time it was *started*** — including a resume — not when it was finished.
- **The play count goes up on every start**, resumes included, so it is not a count of viewings. It is kept
  as information only and never turned into rewatches.
- **Something part-way through a rewatch reads as unwatched**, because starting it cleared the tick. It
  imports as in progress, and the earlier, completed viewing is invisible to this source.

**Already-deleted items.** Jellyfin does not delete watch data with the item (nor with the user), and 10.10
lets a plugin read it by key with no item (`IUserDataRepository.GetAllUserData`). The keys are built from
provider ids: a film is stored under its bare TMDb id, its IMDb id and its old item id; an episode under its
series' first provider id followed by the season and episode as three digits each (`81189` `001` `005`).
So keys that match no current item can be turned back into provider ids — recovering watched films and
episodes the API hides. What cannot be recovered is a *title*: the keys hold ids, not names, and a bare
number does not say which provider it came from (a film's rows are written together, which is the way to
tell). Two ways to name them, both in Phase 2's estimate: ask Jellyfin's own metadata providers, or leave the
names to the app, which may hold the same media on another server. **10.10 only** — that interface is gone
in 10.11 (§14).

### 8.3 Playback Reporting's database

If `<Jellyfin data>/playback_reporting.db` is there, read it directly and read-only (not through that
plugin's admin API). Its one table, `PlaybackActivity`, holds a row per play: `DateCreated`, `UserId`,
`ItemId`, `ItemType`, `ItemName`, `PlaybackMethod`, `ClientName`, `DeviceName`, `PlayDuration` (seconds,
pauses already subtracted). Each row becomes a session, and the sessions are assembled into viewings with
§5.4's rules. What its schema forces on the import:

- **No positions**, so completion is judged as seconds played against the running time where the item
  still exists, and left in progress where it does not.
- **Dates are the server's local time, stored as text with no offset.** They are converted with the
  server's time zone; the hour repeated when the clocks go back is genuinely ambiguous, and those rows are
  flagged rather than guessed.
- **Only the first user of a play** was ever recorded, and **plays under about 20 seconds** never were.
- **No primary key**, so each row's import key is its date, user and item together — the same three
  columns that plugin uses to find its own rows.
- For deleted episodes, series, season and episode are parsed back out of `ItemName`
  (`"Series - s01e02 - Title"`).

Admin-only and server-wide: it holds everyone's rows.

### 8.4 Trakt and Simkl

Per person, sign in with a device code (the kind where a site shows a short code to type on the phone), then
page through the watch history: dates and provider ids, no durations — so `estimated`, credited at running
time. Our own API app with each service. **Held until their terms for a paid app are settled** — an email to
each first (decided 2026-10-30). Note that Trakt's free tier allows one connected app, and the official Trakt plugin is already
installed on JRDisk: a person using it would have to disconnect it or pay to use ours. Reading a Trakt
*export file* (§8.5) avoids both.

### 8.5 Files

- **Trakt export** (the JSON files from Trakt's export page) — dates and provider ids.
- **Letterboxd diary** (`diary.csv`) — films with watched dates and a rewatch flag, but **no provider ids**:
  matched by title and year, so more will land unmatched, and the preview says which.
- **A documented CSV** for anything else: date, type, title, year, provider ids, series, season, episode,
  seconds.

### 8.6 Where a person does an import for themselves

**Jellyfin's dashboard is for admins only** — a non-admin cannot open a plugin's page. So everything a
person does for themselves (connect Trakt, upload a Letterboxd file, look at an import's preview) is done
from JellyMold through the API, and the admin page can do the same on anyone's behalf. Server-wide imports
(Jellyfin's watch state, Playback Reporting) are admin actions.

---

## 9. The API

Base path `/JellyMold/v1`. JSON in Jellyfin's own style (PascalCase, which the app's decoders already
expect). Times in seconds, dates ISO 8601 in UTC. The full contract is published as an OpenAPI document in
the repository during Phase 1 — it is what the app integration is built from.

### 9.1 Access

- **Every endpoint needs a signed-in user**, using Jellyfin's ordinary policy (a bare `[Authorize]`), never
  the admin one.
- **The user is the token's**, read from the caller's own sign-in (the `Jellyfin-UserId` claim) — never from
  a path or a body. An optional `UserId` is honoured only for admins; anyone else passing another person's id
  gets 403. (The official Trakt plugin trusts a user id in the path without checking it against the caller;
  this is the mistake to avoid.)
- **An API key** signs in as an admin with no user, so it must always name a `UserId`.
- Admin-only endpoints are marked so. **No GET changes anything.** Every input is bounded (pages of at most
  500, uploads of at most 20 MB, dates within sane ranges), and every query is parameterised SQL —
  nothing from a request is ever pasted into a query.

### 9.2 Endpoints

| | |
|---|---|
| `GET /Info` | **Discovery.** Plugin and API version, feature list, the counting rules in force (threshold, minimum play), and for the caller: recorded or not, recording since, admin or not. 404 means no plugin; that is how the app tells. |
| `GET /Summary?Tz=Europe/London` | **The stats page in one request.** See below. |
| `GET /Films?Order=recent\|title&StartIndex&Limit` | Every film watched or under way — the *More* page. |
| `GET /Series?Order=recent\|mostWatched\|mostComplete\|title&…` | Every series, with episodes watched, episodes in the library now, time, last watched. |
| `GET /Series/{seriesId}` | One series: each episode's viewings, first watched, rewatches, time. Also by provider id for a removed series. |
| `GET /Items/{itemId}/History` | One item's viewings or listens, with their sessions. Works for removed items. |
| `GET /History?From&To&Kind&StartIndex&Limit` | The history, newest first. `Kind` narrows it to films, episodes, music or audiobooks. |
| `GET /Changes?Since={token}&Limit` | **The change feed** (§9.4). |
| `GET /Sessions?ItemId&From&To` · `GET /Sessions/{id}` | Sessions and their events: the diagnostics (§16, idea 7). |
| `POST /ClientSessions` | **Watching done offline** (§9.3). |
| `GET /Reports/Years/{year}?Tz` | Year in review. |
| `GET /Export?Format=json\|csv` | The caller's whole history. Admins may pass `UserId` or `All`. |
| `GET /Imports` · `POST /Imports/{source}/Preview` · `POST /Imports/{id}/Commit` · `POST /Imports/{id}/Undo` | §8. |
| `GET /Admin/Status` · `POST /Admin/Backups` · `POST /Admin/Restore` | Admin: database size and health, back up now, restore. |

### `GET /Summary`

Built for what `StatsView` shows today, plus what the record makes possible:

```json
{
  "AsOf": "<change token>",
  "Films":    { "Watched": 212, "UnderWay": 3, "ActualSeconds": 1520000, "CreditedSeconds": 1610000,
                "AverageRuntimeSeconds": 7020, "Longest": { "Item": { … }, "RuntimeSeconds": 13200 },
                "Recent": [ /* five FilmRows */ ] },
  "Episodes": { "Watched": 3412, "ActualSeconds": …, "CreditedSeconds": …, "AverageRuntimeSeconds": 2580 },
  "Series":   { "Started": 88, "Finished": 21, "MostWatched": { "Series": { … }, "CreditedSeconds": 1483000 },
                "Recent": [ /* five SeriesRows */ ] },
  "Total":    { "ActualSeconds": …, "CreditedSeconds": …, "WallSeconds": … },
  "Streaks":  { "CurrentDays": 4, "LongestDays": 37, "LongestFrom": "2026-03-02", "LongestTo": "2026-04-07" },
  "TopFilms": [ … ], "TopSeries": [ … ],
  "Recent":   [ /* last ten viewings */ ],
  "ByYear":   [ { "Year": 2026, "Viewings": 812, "ActualSeconds": …, "CreditedSeconds": … } ],
  "Music":      { "Listens": 9120, "ActualSeconds": …, "TopArtists": [ … ], "TopAlbums": [ … ], "TopTracks": [ … ] },
  "AudioBooks": { "Finished": 14, "UnderWay": 2, "ActualSeconds": … }
}
```

The video sections are shaped for JellyMold's stats page. `Music` and `AudioBooks` are deliberately small in
v1 — enough for any app to show something — and grow when an app that plays them wants more.

Days are the caller's: the app passes its time zone, and a day runs from 04:00 to 04:00 so a 1 a.m. episode
belongs to the evening it started in. A streak is consecutive days with something counted.

Every item in every response carries what the app needs to merge copies across servers: provider ids,
series provider ids, series name, season and episode numbers, and `InLibrary`.

### 9.3 `POST /ClientSessions`

```json
{ "Sessions": [ {
    "ClientSessionId": "3F2C…",          // the app's own id: posting twice is harmless
    "ItemId": "…",
    "StartedAt": "2026-10-28T19:02:11Z", "EndedAt": "2026-10-28T19:47:40Z",
    "StartPositionSeconds": 0, "EndPositionSeconds": 2600, "FurthestPositionSeconds": 2600,
    "ActualSeconds": 2600, "WallSeconds": 2600, "PausedSeconds": 120,
    "PlaybackRate": 1.0, "DeviceName": "Jon's iPhone", "Offline": true } ] }
```

The answer says, per session, `Accepted`, `Duplicate` or `Rejected` with a reason. Bounds: at most 200 a
request; not in the future; no older than a year; ended after it started and within a day of it; actual
seconds no more than the elapsed time allows; positions within the running time. The item must exist, or
have a snapshot from before it was removed.

### 9.4 The change feed

Every write to a viewing or session takes the next number from a counter. `GET /Changes?Since=` returns
everything changed after that number — new and updated viewings, and the ids of any removed by an undone
import — plus a token for next time. The token also carries the database's identity, so if the database is
ever restored from a backup (and its counter goes backwards), the app is told to fetch everything once
rather than silently missing changes. The app's cached copy (on the device, and in iCloud when that returns)
then updates by deltas.

---

## 10. The database

### 10.1 Where and how

- One file, `<Jellyfin data>/jellymold/history.db`, with backups beside it in `backups/`. Under Jellyfin's
  data path, so a backup of Jellyfin's data folder includes it — and **not** in the plugin's own folder,
  which carries the plugin's version in its name and is replaced on every update. (Intro Skipper, Streamyfin
  and Playback Reporting all keep their databases in the data path for this reason.)
- **The SQLite library Jellyfin already loads** — `Microsoft.Data.Sqlite` 8.0.11, which 10.10.7 pins —
  referenced but not shipped in the release zip. Two native SQLite builds in one process is a known way for
  plugins to crash a server; Intro Skipper and Streamyfin ship only their own dll for the same reason.
- Plain SQL through a thin data layer — no Entity Framework. The schema is small, and the whole point is
  migrations we write by hand and can see are harmless (§10.2); generated migrations on SQLite rebuild
  tables to change a column, which is the destructive pattern this design exists to avoid.
- WAL mode, so the API reads while the recorder writes. One writer (§2).

### 10.2 Migrations only ever add

- The schema version is stored in the file. Each migration is a numbered SQL script, run in a transaction.
- **Additive only**: new tables, new columns, new indexes. A test reads every migration script and fails
  the build on `DROP TABLE`, `DROP COLUMN` or a table rename. A real reshape is done by creating a new table,
  copying, and leaving the old one in place.
- **A backup before every migration**, kept indefinitely: `history-before-v3.db`.
- **A newer database than the plugin understands** (someone downgraded) is opened read-only: nothing is
  recorded and the dashboard says why. It is never "repaired".
- **The upgrade path from every released schema is tested**: each release adds a fixture database of its
  schema with sample data to the tests, and every later version must migrate it with nothing lost.

### 10.3 Backups

Nightly, as a Jellyfin scheduled task: a consistent copy (SQLite's own `VACUUM INTO`, safe while it is in
use) to `backups/history-2026-10-30.db`. Keeps seven daily and four weekly. *Back up now* and *restore* on
the admin page; export as a third copy anywhere.

### 10.4 Startup check

An integrity check on every start. If it fails, the damaged file is kept aside for salvage, the latest good
backup is restored automatically (losing at most a day), recording continues, and the dashboard shows a
warning naming what happened. Never a silent fresh start.

### 10.5 How big

A heavy household — six people, three hours of video each a day:

| | Rows a year | Size a year |
|---|---|---|
| Sessions | ~11,000 | ~4 MB |
| Viewings | ~8,000 | ~2 MB |
| Events | ~90,000 | ~7 MB |
| Media snapshots | ~5,000 new | ~3 MB |
| **Video history, with indexes** | | **~25 MB** |
| Music — three of them, two hours a day | ~80,000 track plays | **~30 MB** more |
| Progress samples, if kept forever | ~3.5 million | ~220 MB |
| Progress samples, 30 days (default) | ~300,000 standing | ~20 MB, flat |

Music is the biggest contributor by count, because a track is three minutes where an episode is forty. A
typical household (two people, two hours of video and an hour of music a day) is about ten megabytes a year;
twenty years of the heavy household is about a gigabyte. Keeping history forever costs nothing worth
discussing; keeping every progress sample forever is the only choice that does, and it is a setting.

---

## 11. The admin settings page

In Jellyfin's dashboard, under the plugin.

- **People** — who is recorded (everyone by default), each with *recording since* and their history's size.
- **Counting** — completion follows the server's resume settings (shown, with a link to where they are
  changed); the minimum play per kind; rewatch and abandon windows; which kinds are recorded (all on by
  default: films, episodes, home videos, music, audiobooks).
- **Imports** — run Jellyfin watch state and Playback Reporting; see every import with its preview, commit
  and undo; per-person file imports on someone's behalf.
- **Export** — one person or everyone, JSON or CSV.
- **Backups** — the list, back up now, restore, keep how many.
- **Diagnostics** — database health, size, schema version, the last few errors, progress-sample retention.
- **Deleted users** — their history, kept, with a button to remove it.

---

## 12. Security and privacy

- **Within a household, people see only their own history**; admins see everyone's. The README says so
  plainly, because a person's viewing history is among the more revealing things they own.
- **Admin endpoints use Jellyfin's admin policy**; everything else the ordinary one (§9.1).
- **Trakt and Simkl tokens**, when they exist, live in the plugin's database, are never returned by any
  endpoint and never exported.
- **Nothing leaves the server** except what a person deliberately connects (Trakt, Simkl) and what they
  export.
- Jellyfin's API conventions are followed for errors (401, 403, 404, 400 with a reason) so the app's error
  handling needs nothing new.

---

## 13. Licence

**Jellyfin is GPL, and so is this plugin.** It loads into the server's process and is built against the
server's own libraries, which makes it a derivative work. The libraries a plugin compiles against —
the `Jellyfin.Controller` and `Jellyfin.Model` packages — are published as **GPL-3.0-only** (the server
repository's own licence file is GPL-2.0), and the official template and plugins are GPL-3.0. So: **GPL-3.0**,
open source, in its own repository under `darkejon`. (Checked from the packages' declared licences, not legal
advice.)

**The app is unaffected.** JellyMold talks to the plugin over HTTP, exactly as it already talks to the GPL
Jellyfin server itself. Separate programs exchanging data over a network protocol are separate works — the
Free Software Foundation's own FAQ names sockets and pipes as how *separate* programs communicate — and the
GPL reaches the plugin's code, not the programs that send it requests. Three habits keep that clean:

- No code moves between the two repositories in either direction.
- **The API contract is published under a permissive licence** (CC0 or MIT), separately from the code, so
  the app's client is written from a document nobody can argue is GPL.
- The app never ships the plugin; the server admin installs it from the plugin repository.

---

## 13a. Building and distributing it

**Two builds from one codebase, each pinned exactly** (decided 2026-10-30):

| Build | For | Built against | Tested on |
|---|---|---|---|
| **10.10** | JRDisk, and any server SynoCommunity keeps on 10.10 | .NET 8, `Jellyfin.Controller`/`Model` **10.10.7**, `targetAbi` 10.10.7.0 | A Docker copy of 10.10.7 — never JRDisk without asking |
| **12** | Current Jellyfin | .NET 10, `Jellyfin.Controller`/`Model` **12.1.0**, `targetAbi` 12.1.0.0 | The local test server on the Mac (12.1.0) |

One project file builds both (one target framework each), and the few places the two Jellyfins differ sit
in the adapter (§2) behind a build switch. Neither build ships Jellyfin's own libraries.

**Each build's version starts with its Jellyfin major version** — `10.0.1.0` and `12.0.1.0` for release 0.1.0.
Jellyfin installs the highest version whose `targetAbi` it meets, with no upper bound, so without this a Jellyfin
12 server would be offered the 10.10 build whenever its number happened to be higher.

**Why pinned.** The Jellyfin plugin world has moved on: the official template now targets 10.11, its build
workflows default to .NET 10, and the `Jellyfin.Controller` package runs to 12.4. No template version ever
targeted 10.10. A plugin that asks for "the latest" gets the newest, and on an older server Jellyfin marks it
*Not supported* and never loads it. So the project starts from the template's late-2024 layout and pins every
Jellyfin package — as Playback Reporting v16 and Intro Skipper's 10.10 build do. The 12 build is pinned to
12.1.0, the oldest 12 release the local server runs, so it loads on 12.1 and later.

**The plugin repository is ours to host.** Jellyfin's release tooling (`jprm`) builds the zip; a GitHub
release carries it; but the shared workflow only writes a manifest for repositories under `jellyfin/`. So the
repository holds its own `manifest.json`, served from GitHub, which an admin adds under *Dashboard → Plugins →
Repositories*. Each version entry carries its zip's address, an **MD5** checksum (what Jellyfin checks before
installing), a changelog and its `targetAbi`. The manifest lists the 10.10 build and the 12 build side by
side; each server installs the newest version its ABI allows. Intro Skipper's manifest is the model.

**Settings a client needs are served by our own endpoints.** Jellyfin's plugin-configuration API is admin
only, so `/Info` is how a non-admin app learns the rules in force (Streamyfin does the same).

---

## 14. Jellyfin 10.10 and 12

10.11 rebuilt Jellyfin's database and broke plugins, and 12 followed. What that touches here:

- **Our database is ours** — a separate file, untouched by Jellyfin's own migrations, and the same file
  format under either build. A server upgraded from 10.10 to 12 keeps its history.
- **The Jellyfin adapter (§2) is where the builds differ.** Read from 12.1's source on 2026-10-30 (tag
  `v12.1`); what changed, and what the plugin does about it:

  | In Jellyfin 12.1 | Effect here |
  |---|---|
  | **The idle stop uses the last position the client reported**, not the invented one | The resume-lands-late fault of §6.1 is fixed upstream on 12; §16 idea 10 matters for 10.10 only. The plugin's own rule (end at the last real report) is right on both. |
  | Starting playback **no longer clears** the played tick | A rewatch stays ticked throughout. Nothing here depended on the clearing. |
  | **Alternate versions:** finishing one version ticks the film's other versions, with the reason a person's tick carries (`TogglePlayed`) | A tick is matched to a play of the same *media* (shared provider ids), not only the same item, so the other versions do not become extra viewings. Tested. |
  | `ItemRemoved` fires for every descendant, then the parent | Harmless: marking removed is idempotent. |
  | `User`, `PermissionKind` moved namespace; `IUserManager.Users` became `GetUsers()` | Behind `JELLYFIN_12` in the adapter. |
  | `IUserDataRepository` is gone; watch data is stored per item | The deleted-items recovery (§8.2) is 10.10 only. |
  | A server installs the highest plugin version whose `targetAbi` it meets, with no upper bound | Versions carry the Jellyfin major first (`10.x` / `12.x`, §13a). |
  | Ships `Microsoft.Data.Sqlite` 10.0.11 | The 12 build compiles against it. |

  Unchanged: the one-second invented progress, the session ending with no stop, the resume settings and save
  reasons, the event-consumer pattern, the claims and policies.
- **Jellyfin restarts in-process**, and a plugin's code is not reloaded by it: a new build needs the server
  process itself restarted (`tools/load-local.sh` quits and reopens Jellyfin.app; the Docker server restarts its
  container).
- **The deleted-items recovery (§8.2) uses an interface 10.11 removed.** On 12 it would have to read
  Jellyfin's new database directly, or not exist. It matters most for servers that have been running for
  years, so the 10.10 build gets it first.

---

## 15. Test plan

**Unit tests (xUnit), the bulk of it.** The counting rules are plain functions of sample streams, so each
case is a few lines of made-up samples and an expected result:

- Normal play; pause and resume; a long pause; seeks forward and back; skip buttons; 2× and 0.5×; buffering.
- An app that vanishes, then Jellyfin's idle stop at its invented position — with the server's one-second
  ticks arriving all the while, and ignored. The client coming back within 30 minutes. A session that ends
  with no stop. A server restart mid-play. A stop with no start. A new item started with no stop for the old.
- A series removed from the library: every episode's snapshot marked, from one event.
- Music and audiobooks: a song on repeat-one that jumps back rather than restarting; a skipped song under the
  minimum; an album ticked played; an audiobook finished with four minutes left; a book in one file and a
  book in a file per chapter.
- Credited time: a tick without playing (counts, credits nothing); JellyMold's tick at 85% (credits what was
  played); the install-time baseline (credits running time).
- Completion crossing mid-interval; a second session past 90% joining; the credits watched after a break.
- Resuming the next day; restarting from the top; abandonment after 60 days; a rewatch a month on.
- Short plays that join and short plays that don't.
- Marks: the app's own mark after playback; a manual mark; a duplicate mark; an unmark within 24 hours and
  after. Marking a whole series.
- Two users on one session. A client session; the same posted twice; one overlapping a live session.
- The same media under a new item id.

**Migrations.** A fixture database for every released schema version, migrated to the newest, compared row
for row. The script check that fails on anything destructive.

**Imports.** Fixtures for each source — a Playback Reporting database built from its real schema, a fake
library for Jellyfin's watch state, a Letterboxd CSV, a Trakt export. Each checked for: what the preview
says, idempotency (twice adds nothing), undo, and the precedence between sources.

**The API, end to end, on both builds.** A script signs in as an admin and as an ordinary user, plays items by
sending the same playback reports a real client sends, and checks the plugin's answers — including that the
ordinary user can read their own history, cannot read the admin's, and cannot call admin endpoints. It runs
against the local 12.1 server and against the Docker 10.10.7 server, and both must pass before a release.

**Manual.** Each phase ends with a short list for the maintainer to run with the web client and the app
(§18), on the local server.

---

## 16. Beyond watch history

Each with an effort guess: **S** a day or two, **M** most of a week, **L** one to two weeks.

**Chosen 2026-10-30: 1, 2, 3, 4, 5, 8 and 10 — every idea the plugin can deliver.** 6 needs no plugin (it is
app work); 7 arrives with Phase 1 anyway. 9 was left out of the list the maintainer chose from, by mistake, and
is still to be asked.

| # | Idea | What it takes | Effort |
|---|---|---|---|
| 1 | **A per-person store for JellyMold's own data** — settings, quiz records, learned intros — so devices sync without iCloud (off while the app is on a free developer account), and households and future non-Apple apps get it too. | A versioned document per person per key, written with *only if it is still the version I read*. On a clash the server refuses and the app merges with the rules it already has (`PerItemStore.merging`, the quiz logs' unions) and tries again. **The merge rules stay in the app, in one place**; the server stores and arbitrates. | **M** |
| 2 | **Quiz rounds for devices without Apple Intelligence** — rounds built on a capable device stored on the server, so the 14 Pro can play them. Scores shared across devices. A household leaderboard per programme. | Rounds are documents in idea 1's store, keyed by item: **S** on top of 1. The leaderboard is the first thing that shows one person's data to another, so it needs an opt-in per person: **M**. | **S** / **M** |
| 3 | **Subtitles ready before they are needed** — extract text subtitle tracks at scan time so the first request is instant. | Jellyfin already does the right thing *late*: the first request for any embedded subtitle of a file extracts **all** of that file's subtitle tracks in one pass and keeps them for good (in its data folder, never cleaned). Nothing does it at scan time. So this is a scheduled task that triggers Jellyfin's own extraction for new items — no second copy. One cost to weigh: Jellyfin's pass takes picture-based (PGS) tracks too, which can be tens of megabytes each. | **S** |
| 4 | **Intro and credits segments for every app** — publish skip segments from clearly named chapters and from intros JellyMold learns across a season. | 10.10 lets a plugin provide media segments. **Named chapters are already covered** by the *Chapter Segments Provider* on JRDisk (admin-editable patterns for intro, recap, preview, credits and commercials — though its default *commercial* pattern matches any chapter title containing "ad", like "Shadows", which is worth a look on JRDisk). The new part is the app's **learned** intros, posted to the plugin and published as segments, so the web client and TV apps get the Skip button too. Two limits in Jellyfin: providers are asked only by a scheduled task (every 12 hours), and **an item that already has segments from any provider is skipped** — so learned intros fill the gaps the chapter provider leaves, rather than correcting it. | **M** |
| 5 | **Chapter names made once, shared everywhere** — names the app gives unnamed chapters with Apple Intelligence, kept per item on the server. | A shared, per-item document (not per person). | **S** after 1 |
| 6 | **Faster sign-in.** QR: the new device shows a QR code with the server address and Quick Connect code; a signed-in phone scans and approves. | **Needs no plugin.** Quick Connect's approval needs only the approver's own sign-in; the code is six digits and lives ten minutes. App-only work. *Approve from the dashboard* would need a page non-admins can open, which Jellyfin does not offer plugins — not recommended. | **S** (app) |
| 7 | **Session diagnostics** — the event log, readable by the person and their admin: "why did this leave Continue Watching?" | Falls out of Part 1 (§6.1); an endpoint and, later, a screen. | **S** |
| 8 | *(new)* **The home screen in one request** — Continue Watching, Next Up and the newest per library from one call. | The app makes several requests per server per launch today; the plugin can assemble the same answer from Jellyfin's own queries in one. | **M** |
| 9 | *(new)* **Find this media here** — given provider ids, this server's copy and the person's watch state in one call. | Makes the multi-server merge's look-ups (duplicates, the per-media override) one request per server instead of a search. | **S** |
| 10 | *(new)* **Undo Jellyfin's guess after an idle stop** — when Jellyfin's idle timer stops a vanished client at its invented position (§6.1), put the resume point back where the viewer really was, and take back a *watched* tick that only the invented position earned. | The plugin already knows both positions. It would be the first thing the plugin **writes into Jellyfin's own watch data** rather than only recording — a product call, so a switch on the settings page, off by default. Fixes resume-lands-late for every app on the server, not only JellyMold. | **S** |

**Proposed order for Phase 3:** **1** (sync without iCloud, and the base for 2 and 5) → **10** (small, and it
fixes a real fault every app on the server has) → **4** → **3** → **2** → **5** → **8**. Idea 10 stays a switch
on the settings page, because it is the one feature that changes Jellyfin's own watch data.

---

## 17. What the app will change when it integrates

For the app's own sessions later — listed here so the contract is designed for them.

- **Send a play session id** (a new UUID per play), the media source id and `PlayMethod: DirectPlay` with
  start, progress and stop. Sessions are then matched exactly; Jellyfin's idle stops (which never carry one)
  can be told from JellyMold's own stops exactly; and Jellyfin's dashboard shows the play correctly too.
- **Playback speed has nowhere to go** — Jellyfin's reports have no field for it. The plugin does not need it
  (§5.1). If the app ever wants it on record, a small plugin endpoint can take it; not in v1.
- **Not the plugin's job, found on the way:** after the app is killed mid-play, Jellyfin's idle stop leaves
  the resume point 5 to 10 minutes past where the viewer was, and can tick a nearly-finished episode watched
  (§6.1). §16 idea 10 fixes it on the server for every app; until it lands, worth knowing in the app's own
  sessions.
- **Offline:** post client sessions to `/ClientSessions` *before* replaying the stop and the mark (§6.3).
- **The stats page:** use `/Summary` where `/Info` answers, and today's computation where it does not.
  Merge across servers on the provider ids every record carries, as now.
- **The cached copy** (`WatchStatsStore`) updates through `/Changes` instead of refetching.

---

## 18. Delivery

| Phase | What lands | Ends with |
|---|---|---|
| **0** | This document and the questions | Answers |
| **1** | 12.1's source read as 10.10.7's was (§14); scaffold from the template, building for 10.10 and 12; a script that loads a build into the local 12.1 server, and a Docker 10.10.7 server for the other build; recording of video, music and audiobooks (events → sessions → viewings); the database, migrations and backups; `/Info`, `/Summary`, `/History`, `/Changes`, `/ClientSessions`, `/Export`; the settings page; unit tests for every rule above; the plugin repository manifest and a first release; README; the OpenAPI contract | A manual test list and a changelog |
| **2** | Imports: Jellyfin watch state (and the deleted-items recovery if it proves out), Playback Reporting, files; Trakt and Simkl once their terms are settled | The same |
| **3** | The chosen §16 ideas, in the order proposed there | The same |

**Environment notes for Phase 1.** The .NET 10 SDK goes into `~/.dotnet` with Microsoft's install script, no
`sudo`; it builds both the .NET 8 and .NET 10 targets. The local 12.1 server is the everyday test bed (see
`CLAUDE.md`). Docker is needed only for the 10.10.7 build: the `docker` command here talks to OrbStack, which
is not running and needs starting (`open -a OrbStack`). Nothing touches JRDisk.

---

## 19. Decisions, and what is still open

### Decided 2026-10-30

1. **Name and home.** *JellyMold Companion*, in a public GitHub repository, `darkejon/jellymold-companion`,
   folder `JellyMold/JellyMold-dev/JellyMold-Companion` beside the app.
2. **Watch time in the app** is **credited time** for the headline: the full running time once the threshold is
   reached, otherwise the minutes actually watched. Actual time beside it.
3. **Completion follows the server's resume settings** (§5.2), so the stats agree with Jellyfin's tick.
4. **A tick without playing counts as watched and credits no time.** Time comes from actual watching — with one
   exception: the install-time import of everything already watched sets the baseline at running time (§5.3).
5. **A deleted Jellyfin user's history is kept**, marked deleted, with an admin button to remove it.
6. **Imports in v1:** Jellyfin's watch state, Playback Reporting and files. Trakt and Simkl after an email to
   each about their terms for a paid app.
7. **Music and audiobooks are recorded from v1**, in the same way as video (§6.6), for a possible JellyMold
   Music and JellyMold Books and for any other app.

8. **Every idea the plugin can deliver is in** (§16): the per-person store, quiz rounds and a household
   leaderboard, subtitles prepared ahead, learned intros as skip segments, shared chapter names, the home
   screen in one request, and undoing Jellyfin's idle-stop guess.
9. **The cutover is not movable by hand** (§8).
10. **A song counts as listened at half its length or four minutes heard**, whichever comes first — Last.fm's
    rule (§5.2). Its full running time is credited only at 90% (§5.3).
11. **Two builds, Jellyfin 10.10 and 12**, from one codebase (§13a).

### Where the build stands

- **Phase 1** (recording, the API, the database, the settings page) and **Phase 2** (imports) are built, as release
  0.2.0. Neither is published yet.
- **Not built from Phase 2:** the recovery of already-deleted items from Jellyfin's own watch data (§8.2). It reads an
  interface only Jellyfin 10.10 has, it can recover provider ids but never titles, and it needs a test against a real
  library with deletions in it. Worth doing once on JRDisk's copy of the data, with the maintainer, rather than as a
  feature. Trakt and Simkl sign-in wait on an email to each about their terms (decision 6).
- **Found in Phase 2:** Jellyfin keeps a book split into chapter files as one item per file in the book's folder, with
  no album; the folder is treated as the book. A preview runs the whole import and rolls it back, so its counts are the
  commit's.

### Still open

12. **§16 idea 9, "find this media here"** — given provider ids, this server's copy and the person's watch
    state in one request, for the multi-server merge. Small; left out of the list by mistake.

---

## Sources

Read from source on 2026-10-30: Jellyfin at tag `v10.10.7`, and each plugin at its 10.10 release.

| Fact | Where |
|---|---|
| Playback events, both forms; event args fields | `MediaBrowser.Controller/Session/ISessionManager.cs`, `MediaBrowser.Controller/Library/PlaybackProgressEventArgs.cs`, `Emby.Server.Implementations/Session/SessionManager.cs` |
| The server's one-second progress timer (`IsAutomated`) | `MediaBrowser.Controller/Session/SessionInfo.cs` (`OnProgressTimerCallback`) |
| Idle stop: every 5 min, over 5 min silent, invented position, no flag | `SessionManager.CheckForIdlePlayback` |
| Session removed without a stop | `SessionManager` (session end path) |
| No playback speed field | `MediaBrowser.Model/Session/PlaybackProgressInfo.cs`, `PlayerStateInfo.cs` |
| Watched rule (`MinResumePct` 5, `MaxResumePct` 90, `MinResumeDurationSeconds` 300); start clears the tick and bumps the play count | `Emby.Server.Implementations/Library/UserDataManager.cs`, `MediaBrowser.Model/Configuration/ServerConfiguration.cs` |
| Save reasons (`TogglePlayed`, `PlaybackFinished`…) | `MediaBrowser.Model/Entities/UserDataSaveReason.cs` |
| Watch data survives item and user deletion; keys; read by key | `Emby.Server.Implementations/Data/SqliteUserDataRepository.cs`, `SqliteItemRepository.DeleteItem`, `MediaBrowser.Controller/Persistence/IUserDataRepository.cs` |
| One removal event per deleted series | `LibraryManager.DeleteItem` |
| User deleted (consumer form) | `Jellyfin.Server.Implementations/Users/UserManager.cs` |
| Policies and claims | `MediaBrowser.Common/Api/Policies.cs`, `Jellyfin.Api/Auth/CustomAuthenticationHandler.cs` |
| Media segment providers, the 12-hour task, skip-if-present | `MediaBrowser.Controller/MediaSegements/IMediaSegmentProvider.cs`, `MediaSegmentManager`, `MediaSegmentExtractionTask` |
| Subtitle extraction: all tracks in one pass, on first request, kept in `data/subtitles` | `MediaBrowser.MediaEncoding/Subtitles/SubtitleEncoder.cs` |
| Quick Connect: six digits, ten minutes | `Emby.Server.Implementations/QuickConnect/QuickConnectManager.cs` |
| SQLite versions (`Microsoft.Data.Sqlite` 8.0.11, SQLitePCLRaw 2.1.6) | `Directory.Packages.props` |
| Packages' licence GPL-3.0-only | `MediaBrowser.Controller.csproj`, `MediaBrowser.Model.csproj` |
| Playback Reporting schema, local-time dates, 20 s rule, 3-month trim, admin-only API | `jellyfin-plugin-playbackreporting` v16: `Data/ActivityRepository.cs`, `EventMonitorEntryPoint.cs`, `TaskCleanDb.cs` |
| Template layout; manifest format and MD5 check | `jellyfin-plugin-template` (2024 layout), `MediaBrowser.Model/Updates/*`, `InstallationManager.cs` |
| Chapter Segments Provider patterns | `jellyfin-plugin-chapter-segments` v1.0.0, `Configuration/PluginConfiguration.cs` |
