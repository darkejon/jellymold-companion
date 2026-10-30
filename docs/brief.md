# A Jellyfin server plugin for JellyMold — brief for a new session

Written 2026-10-30 in the JellyMold app session, for a separate agent to start the plugin from. The plugin
is a new project in its own repository; the JellyMold iOS app (this repository) is context, read-only for
you. Integrating the plugin into the app is a later task done in the app's own sessions.

---

## Start here

You are building a Jellyfin **server plugin** that keeps a complete, permanent, per-user record of what
each person has watched — when, for how long, and how often — plus a small set of services that make the
JellyMold iPhone/iPad app faster and richer. The maintainer's words: *"a complete record of everything
you've watched on your Jellyfin server, accumulating over time."*

**Before writing any code**, read this whole brief and the files listed under *Context*, then produce:

1. A **design document**: data model, how playback events become records, the counting rules, the import
   design, the HTTP API contract, the database and migration approach, the test plan.
2. A **list of questions** for the maintainer, starting with the *Open decisions* at the end of this
   brief. Wait for answers on those before building the parts they affect.

Then build in the phases under *Delivery*, smallest useful thing first.

**Who you are working with.** The maintainer is a UX designer, not a developer: they read code, run builds
and make product and design calls. Decisions with real trade-offs are presented to them, not quietly made.
Write plainly. Small commits, one per logical step; commit and push only when asked.

---

## Why this exists

JellyMold's stats page today is built from Jellyfin's per-item **watch state** — the *played* flag and
resume position — and the item's running time. That is lossy in ways that matter:

- **Delete the media and it vanishes from the stats.** Jellyfin keeps the watch data internally (keyed by
  provider ids), but its API only returns watch data through an item that still exists.
- **No real time.** "Watch time" is the running time of everything flagged played. Half an episode counts
  as its resume position; a film marked watched without playing counts in full; a rewatch counts once.
- **No history.** One played flag, one play count, one last-played date per item — no sessions, no dates
  per viewing, no durations.

The existing **Playback Reporting** plugin (official, in the catalog, v16 for 10.10.7) was evaluated on
2026-10-30 from its source and rejected as the long-term answer:

- **Admin-only API** (`RequiresElevation` on the whole controller). A household member who is not the server
  admin can never read their own history from an app.
- **Deletes rows older than 3 months every night by default** (`MaxDataAge = 3`).
- **Drops its whole table if the schema changes** on a future version — a lost history waiting to happen.
- **Wall-clock durations**: seeking and buffering count as watching; pauses are only partly excluded.
- No completion flag, no positions, no "marked watched" events, no series/season ids (the series name is
  baked into a text field), plays under 20 seconds never recorded.
- Admin endpoints that change data over GET and concatenate request values into SQL.

It is still worth **importing** from (see *Importing past history*): the maintainer may install it as a
stop-gap so real data starts accumulating before this plugin exists.

**A real incident to design against (2026-10-29).** An app update closed JellyMold mid-episode; Jellyfin
only ended the abandoned session 5 minutes later (its idle timer). A later session ran the episode to ~96%
and Jellyfin marked it played; earlier, the episode had briefly vanished from Continue Watching for a reason
nobody could reconstruct, because nothing recorded the session lifecycle. This plugin should make that kind
of question answerable after the fact.

---

## Environment and constraints

- **Target Jellyfin 10.10.7.** The maintainer's server ("JRDisk") is a Synology NAS running the
  SynoCommunity package `10.10.7-26`, which does not offer 10.11. Build for `targetAbi` 10.10.7.0 on
  **.NET 8**. Plan for 10.11 later — it rebuilt its database (EF Core) and broke plugins — but do not let it
  shape v1.
- **Start from** the official template, `github.com/jellyfin/jellyfin-plugin-template`, and follow the
  current official plugins (Trakt, Playback Reporting, Intro Skipper-style community plugins) for idioms:
  `IPluginServiceRegistrator`, `IHostedService`, ASP.NET Core controllers, an embedded dashboard page.
- **Test server: Docker on the maintainer's Mac** (`docker` is installed; image `jellyfin/jellyfin:10.10.7`),
  with a small test library and two users (one admin, one not). **Never install or test on JRDisk without
  asking** — it holds the real library and the maintainer's real watch data.
- **.NET SDK is not installed.** Install it user-locally (Microsoft's `dotnet-install.sh` into `~/.dotnet`).
  Never use `sudo` on this machine.
- **Licence:** Jellyfin is GPL; a plugin built against its libraries is almost certainly GPL too. Open
  source, its own repository (the maintainer's GitHub is `darkejon`). The app only talks to it over HTTP, so
  the app's licensing is unaffected — confirm this reasoning in the design doc rather than assuming it.
- **Distribution:** a custom plugin repository (a `manifest.json` the admin adds under *Dashboard → Plugins →
  Repositories*, with versioned release zips and checksums), hosted on GitHub. Submission to the official
  catalog can come later.
- **The app merges several servers.** One person may have two Jellyfin servers with overlapping libraries;
  the app collapses copies by provider ids (TMDb/TVDB/IMDb, and for episodes series + season + episode).
  The plugin is per server, so every record it returns must carry provider ids for the app to merge on.

---

## Part 1 — The watch history (the core)

### Requirements, from the maintainer

- **Every playback on the server is recorded**, whichever app played it — JellyMold, the web client, a TV
  app — per user, **admin or not**.
- **Kept in perpetuity.** Records survive the media being deleted from the library. There is no user-facing
  way to delete history (the database stays small, and a history that can be trimmed is not a complete
  one). Export is fine.
- **Actual watch minutes.** An episode stopped part-way counts the minutes actually watched.
- **Completion at 90%.** Reaching 90% counts the item as watched in full.
- **Rewatches count.** Watching something twice is two viewings.
- **Imported past history**, so nobody starts from zero (Part 2).
- **Per-user access.** Every user can read their own records; admins can read everyone's. The app must be
  able to talk to it freely with an ordinary user's token.

### Design guidance (yours to refine in the design doc)

**Three levels of record.**
- **Events** — raw: start, progress (sampled), pause, resume, seek, stop, from Jellyfin's session events.
  Optional to keep long-term; useful for diagnosing incidents like the one above.
- **Sessions** — one continuous play: user, item, client, device, play method, started/ended at, start and
  end position, **seconds actually watched**, whether it ended cleanly or was closed by Jellyfin's idle timer.
- **Viewings** — the unit a person means by "I watched it": one or more sessions of the same item,
  accumulating until it is **completed** (reaches the threshold) or abandoned. Watching episode 3 over two
  evenings is one viewing; watching it again next month is a second viewing.

**Counting rules to propose and test:**
- *Seconds actually watched* = media time advanced at normal play, from position progress, excluding pauses
  and seeks — not wall-clock. Record playback speed; keep both *content seconds* and *wall seconds* so the
  app can choose.
- *Completed* when the furthest position reaches the threshold (default 90%). Propose whether to follow the
  server's own `MaxResumePct` setting instead.
- *Credited time* for a completed viewing = the full running time (the 90% rule); for an incomplete one =
  seconds actually watched. Store both raw and credited.
- *Rewatch*: a new viewing begins when playback starts near the beginning (say under 10%) after a completed
  viewing, or after a long gap. Define it precisely.
- *Marked watched / unwatched without playing* (`UserDataSaved` with a toggle reason): record as its own
  kind of entry — counts as watched, credits no actual time (or the running time — open decision).
- **Abandoned sessions**: when a client vanishes (app killed, network gone), finalise the session from its
  last progress when Jellyfin's idle timer stops it (about 5–10 minutes after the last check-in, using the
  last reported position). Flag it as closed by the server.
- **Offline viewing**: JellyMold plays downloaded files with no server contact and replays positions later.
  Provide an endpoint for the app to post *client-reported sessions* (item, start/end time, positions,
  seconds) so offline watching counts. Mark them as client-reported.
- Exclude trailers, theme songs/videos, extras, and very short plays (configurable minimum, e.g. 60 s — but
  keep the seconds so nothing is silently lost). Video only in v1; say whether music and audiobooks fit
  later.

**Survives deletion.** Every record carries a **snapshot of the item** at the time: type, title, series and
season names and ids, season and episode numbers, production year, running time, and provider ids. Listen
for library removals so the record notes *deleted from the library on…*, but never removes anything.

**Database.**
- SQLite in the plugin's own data folder, WAL mode, UTC timestamps (the app passes its time zone for
  day-based grouping).
- **Migrations are additive and never destroy data** — the direct lesson from Playback Reporting. Test the
  upgrade path from every released schema version.
- A nightly backup copy (keep a few), and a sanity check at startup.
- Estimate and state the size: rows per year for a heavy household.

**Admin settings page** (embedded in the Jellyfin dashboard): which users are recorded (default all), the
completion threshold, the minimum play length, imports, export, backups. Propose what happens when a
Jellyfin user is deleted (open decision).

---

## Part 2 — Importing past history

Goal: a user arriving with years of history does not start from zero. **Everything before a cutover point
comes from imports; everything after it from live recording.** The cutover defaults to the moment recording
started for that user and can be moved earlier by the user.

Sources, roughly in order of value and effort:

1. **Jellyfin's own watch state** (no account needed). For each played item: one imported viewing, dated by
   `LastPlayedDate`, credited at running time, `PlayCount` noted. Flagged *estimated*. Runs per user at
   install and for users added later. Worth checking whether the plugin can also reach Jellyfin's retained
   watch data for **already-deleted** items (stored by provider-id keys in `UserDatas`) — that would recover
   history the API hides.
2. **Playback Reporting's database** (`playback_reporting.db` in Jellyfin's data path) if present on the same
   server: real dates and seconds per viewing, including deleted items. Read it directly; do not depend on
   that plugin's API.
3. **Trakt** — per-user device-code sign-in on the plugin's page, then the paginated watch history (dates,
   provider ids). Needs our own Trakt API app. **Read `docs/watch-logging-services.md` first**: it found
   Jellyfin's official Trakt plugin already scrobbling server-side (installed and active on JRDisk), and it
   left Trakt's terms for commercial or paid third-party apps unresolved. JellyMold is a paid app; whether a
   free open-source plugin changes that is a question for the maintainer, not an assumption.
4. **Simkl** — the same shape. Its commercial terms were never examined. Simkl is also installed on JRDisk.
5. **Files**: a Trakt data export, Letterboxd's diary CSV (films, with dates), perhaps a documented generic
   CSV. Useful where an API is off the table.

Rules for every import:
- Match items to the library by provider ids; **keep unmatched items too** (title and ids), since the media
  may never have been on this server.
- Tag each record with its source. Never double-count: the same viewing from two sources, or from an import
  and live recording, is one viewing. Imports are idempotent — running one twice changes nothing.
- Show the user what an import will add before committing it, and allow it to be undone (undoing an
  *import* is not deleting *history*).

---

## Part 3 — The API for the app

- One base path, versioned: `/JellyMold/v1/…`.
- A **discovery endpoint** any signed-in user can call (plugin version, features, completion threshold), so
  the app can tell whether the plugin is installed without admin rights.
- **Per-user by default**: the user comes from the request's token; admins may pass a user id. Use
  Jellyfin's ordinary signed-in policy, never the admin policy, for the user's own data.
- **The stats page in one round trip.** Today the app fetches every played and started item from every
  server (30-second timeouts) and adds things up itself. Offer a summary endpoint computed server-side:
  counts, actual and credited time for films and series, top series and films, streaks, recent activity,
  per-year figures.
- **History with a change cursor.** Paged history plus a `since` token, so the app's cached copy (it keeps
  one on the device and in iCloud) updates by deltas rather than refetching everything.
- Per-item and per-series history (viewings of this episode, rewatches, first watched).
- Year-in-review style reports, if cheap once the model exists.
- An **export** endpoint (JSON, CSV).
- No raw-SQL endpoints, no GET requests that change data, input validated and bounded.
- Publish the contract as OpenAPI (or a precise Markdown doc) — it is what the app integration will be built
  from.

---

## Part 4 — Beyond watch history (ideas for the maintainer to choose from)

Ordered by how much they help the app. Put them in the design doc with an effort estimate each; build none
until chosen.

1. **A per-user store for JellyMold's own data on the server.** The app syncs settings, quiz records and
   learned intro data through iCloud today (`docs/icloud-sync.md`) — and **iCloud sync is switched off** while
   the app is on a free developer account. A small per-user key-value or document store on the server would
   give cross-device sync now, work for households and future non-Apple clients, and keep the data next to the
   library it describes. Mirror the merge rules `docs/icloud-sync.md` describes.
2. **Quizzes for devices without Apple Intelligence.** JellyMold builds dialogue quizzes on-device with
   Apple Intelligence; the maintainer's own iPhone 14 Pro cannot. If quiz rounds built on a capable device
   were stored on the server, any device could take them. Also: quiz scores and memory shared across devices,
   and a **household leaderboard** per programme.
3. **Subtitles ready before they are needed.** The first request for a subtitle track inside a video makes
   Jellyfin extract it from the whole file — slow enough on the NAS, with a large file, that a selected track
   looked unselected on 2026-10-29. A scheduled task could extract text subtitle tracks at library scan time
   and serve compact cue JSON (start, end, text), which the app's subtitle display, quiz and chapter naming all
   want. Check what Jellyfin already caches before duplicating it.
4. **Intro and credits segments for every app.** Jellyfin 10.10 lets plugins supply media segments. Publish
   intro, recap and credits segments from clearly named chapters ("Title Sequence", "End Credits") and from
   intros the app has learned across a season — then every Jellyfin app gets the Skip button. JRDisk already
   has a *Chapter Segments Provider* plugin; establish the overlap first.
5. **Chapter names made once, shared everywhere.** The app names unnamed chapters with Apple Intelligence and
   keeps them per item; storing them server-side lets every device (and the non-AI ones) use them.
6. **Faster sign-in than typing a code.** Quick Connect already works in the app (since 2026-10-07): the new
   device shows a six-character code that the user types into an already signed-in Jellyfin app. Ideas:
   - **QR pairing**: the new device shows a QR code carrying the server address and the Quick Connect code; an
     already signed-in phone scans it and approves with one tap (`/QuickConnect/Authorize` needs only the
     approver's own token — this half may need no plugin at all).
   - **Approve from the dashboard**: the new device registers a pending request with the plugin (by username,
     not password); the user's signed-in Jellyfin web page shows *"iPad wants to sign in — Approve"*.
   Weigh the security of each: the code is the secret, so never list pending requests to anyone but the user
   they name.
7. **Session diagnostics.** The event log in Part 1, readable by the user and their admin, makes "why did this
   leave Continue Watching" answerable.

Anything else that would make the app faster, simpler to set up, or able to show things it cannot today is
welcome in the design doc — that is an explicit invitation from the maintainer.

---

## Delivery

- **Phase 0** — the design document and questions above. No code.
- **Phase 1** — scaffold from the template; Docker test server and a script to load the plugin into it;
  recording (events → sessions → viewings); the database with migrations and backups; per-user discovery,
  summary and history endpoints; the admin settings page; automated tests (xUnit) for the counting rules;
  the repository manifest and a first release.
- **Phase 2** — imports: Jellyfin watch state first, then Playback Reporting's database, then file imports,
  then Trakt and Simkl once their terms are settled.
- **Phase 3** — whichever Part 4 items the maintainer picks, likely the per-user data store first.
- Each phase ends with a short manual test list the maintainer can run on the Docker server, and a changelog.
- Alongside: a **README** that tells a server admin how to add the repository and install the plugin in a
  few steps, and the **API contract** for the app.

---

## Context to read (in the JellyMold repository)

The app lives at `/Users/jon/Documents/Virtue Apps/Projects/JellyMold/JellyMold-dev/JellyMold`. Read, do not
change.

- `CLAUDE.md` — how the project is run, and the target server's details (Jellyfin 10.10.7 on a Synology).
- `Packages/JellyMoldKit/Sources/PlayerCore/WatchStats.swift` — today's stats model: what is counted, how
  copies across servers are merged, what "under way" means.
- `JellyMold/Player/StatsView.swift` — what the stats page shows, so the summary endpoint can serve it.
- `Packages/JellyMoldKit/Sources/PlayerCore/WatchStatsStore.swift` — the app's cached copy of the stats.
- `Packages/JellyMoldKit/Sources/JellyfinKit/JellyfinClient.swift` and `PlaybackReporter.swift` — the
  Jellyfin endpoints the app uses and how it reports playback (start, progress every 10 s, stop).
- `Packages/JellyMoldKit/Sources/JellyfinKit/WatchStateQueue.swift` — positions recorded offline and replayed.
- `Packages/JellyMoldKit/Sources/PlayerCore/QuizRecords.swift` and `docs/icloud-sync.md` — the quiz and
  settings data that a server-side store could carry.
- `Packages/JellyMoldKit/Sources/PlayerCore/IntroDetector.swift` — how intros are found from chapters.
- `docs/watch-logging-services.md` — the Trakt/Simkl research and its open questions.
- `docs/multi-server.md` — how the app treats several servers as one library.

---

## Open decisions for the maintainer

1. **Name** and **repository** for the plugin (a working name: *JellyMold Companion*).
2. **What "watch time" means in the app** once both exist: actual minutes watched, or credited time (full
   running time once 90% is reached)? The plugin should store both either way.
3. **Completion threshold**: a fixed 90%, or the server's own `MaxResumePct`?
4. **"Marked watched" without playing**: counted as watched — with the running time credited, or none?
5. **When a Jellyfin user is deleted**: keep their history, or remove it?
6. **Which import sources in v1**, and whether to approach Trakt and Simkl about their terms for a paid app.
7. **Whether to record music and audiobooks**, or video only.
8. **Which Part 4 ideas to build**, and in what order.
