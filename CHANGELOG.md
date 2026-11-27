# Changelog

Plugin versions carry the Jellyfin major version first: `10.x.y.z` is the build for Jellyfin 10.10, `12.x.y.z` the
build for Jellyfin 12 (design §13a). Both come from the same release.

## Unreleased

- **New episodes, for JellyMold's notifications.** Seerr tells JellyMold when a requested show is complete, never when
  an episode arrives. The plugin now does: when an episode arrives of a show somebody on the server requested through
  Seerr and asked JellyMold to tell them about each episode of, it tells JellyMold's notification service, which sends
  their phone a notification. A season added in one scan is one notification. Only those shows are sent, and only the
  show's name and ids, and each episode's season, number, title and id; nothing else in the library leaves the server. On
  by default; *Tell JellyMold apps about new episodes* on the settings page turns it off. `/Info` lists
  `episodeNotifications` while it is on.

- **The server's clock, for allowed hours.** Jellyfin keeps an account's allowed hours (Parental control › Access
  schedule) on the server's own clock and never tells apps which time zone that is. `/Info` now says, so JellyMold can
  warn a viewer five minutes and one minute before their time ends, and say when they can watch again, even when the
  server runs on a different clock from the household (a Docker container left on UTC, say).

- **Subtitles on time after a resume, on Jellyfin 10.10.** In CarPlay and HomeView, a film or episode resumed
  partway through, or jumped far ahead or back, showed its subtitles late by however far into the film the video
  began — minutes, so none were seen. Jellyfin 10.10 starts the video's clock again wherever it begins; the subtitle
  pieces now say where that was. Jellyfin 12 was not affected.

## 0.8.0 — 2026-11-19 (builds 10.0.8.0 and 12.0.8.0)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.8.0

- **Updates finish themselves.** Jellyfin installs new versions of the plugin by itself but never restarts for them,
  so a server nobody restarts went on running the old version for months. The plugin now restarts Jellyfin to finish
  its own update, once per version, in quiet hours on the server's clock: from 04:00 for three hours, only while
  nobody is playing and no scheduled task is running, or else the next night. Each restart is in Jellyfin's activity
  log. The time can be changed, or the restart switched off, in a new *Updates* section of the settings page.
- **Never with Playback Reporting installed.** On Jellyfin 10.10.7, Jellyfin's own restart stops a server with
  Playback Reporting instead of restarting it (its Restart button does the same). Such a server is told instead: the
  settings page says a new version is waiting, and to restart Jellyfin where it runs.
- The settings page says at the top when a new version is waiting, and when it will start working. `/Info` reports
  the same as `Update`, for apps to tell admins.

## 0.7.0 — 2026-11-19 (builds 10.0.7.0 and 12.0.7.0)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.7.0

- **Subtitles on time in CarPlay and HomeView.** Players that draw Apple's own subtitles from Jellyfin's stream —
  CarPlay, HomeView, an Apple TV under AirPlay — show every line about ten seconds late, because Jellyfin times its
  subtitle pieces for a different kind of video from the one it sends them (Jellyfin's own fix is in no release yet,
  and Jellyfin 10.10 will never have it). The plugin serves Jellyfin's stream with its own subtitles, timed right and
  in 6-second pieces so lines appear sooner and catch up faster after a scrub. JellyMold uses it where the plugin is
  installed; nothing to switch on.

## 0.6.0 — 2026-11-19 (builds 10.0.6.0 and 12.0.6.0)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.6.0

- **Ratings from other sites.** With an MDBList API key (free from mdblist.com) on the settings page, the plugin
  fetches each film's and series' ratings from IMDb, Rotten Tomatoes (critics and audience), Metacritic, TMDb,
  Letterboxd, Trakt, Roger Ebert and MyAnimeList, so JellyMold can show the one each viewer prefers. New titles a few
  minutes after they arrive, anything an app asks for at once, and the rest nightly (*Fetch ratings*, 02:00); each
  title again a month later. It uses at most half the key's daily allowance, and a hundred titles go in each request.
  Only titles' ids are sent to MDBList. The key is checked with MDBList when it is saved, and apps are offered the
  ratings only once it has been accepted.

## 0.5.0 — 2026-11-16 (builds 10.0.5.0 and 12.0.5.0)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.5.0

- **Your stats on the web.** Each person can see their stats in any browser at `/JellyMold/Stats` on the server's
  address, laid out as the JellyMold app shows them: the *Watched* card with the time, watching streaks, films and
  series (every one a level deeper), recently watched, and each year. It uses the browser's Jellyfin sign-in where there
  is one, or its own form; admins can choose whose stats to see. Nothing to install, and it loads nothing from anywhere
  else.
- At the top of the page, a banner for the JellyMold app: its icon, a line about it, and *Get the app*. Hiding it
  hides it for good in that browser.
- An admin's menu of whose stats to see lists every account on the server, starting with their own, and anyone
  recorded whose account has since been deleted.
- **JellyMold Stats in the dashboard.** Admins find the same page in the dashboard's menu, under *Plugins* beside
  Playback Reporting's, signed in with the dashboard's own sign-in and drawn in the dashboard's theme.
- **JellyMold Stats in everyone's menu.** A link to the page in the side menu of Jellyfin in a browser, for everyone,
  not only admins, on from the start. The plugin adds it to the web client's menu as Jellyfin sends it, without
  touching Jellyfin's files, so Jellyfin's updates do not remove it. Browsers are told to check that menu each time
  Jellyfin opens, so the link appears or goes at once; a browser that opened Jellyfin before this update needs one
  hard reload.
- The settings page has a *Stats on the web* section: the page's address, and a button that takes the link out of
  everyone's menu (or puts it back).
- The settings page's note that the plugin is made for JellyMold is now the first thing under its title.

## 0.4.1 — 2026-11-14 (builds 10.0.4.1 and 12.0.4.1)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.4.1

- The settings page opens with a short note that the plugin is made for JellyMold, the Jellyfin app for iPhone, iPad
  and Apple TV, with a link to it on the App Store.

## 0.4.0 — 2026-11-14 (builds 10.0.4.0 and 12.0.4.0)

https://github.com/darkejon/jellymold-companion/releases/tag/v0.4.0

- **A play resumed as the plugin first starts carries on its viewing.** On JRDisk an app resumed an episode half a
  second before the baseline was taken. The play became a second viewing beside the baseline's half-watched one, and
  finishing it would have credited the first six minutes twice. A play now looks again for a viewing to join when it
  first counts, not only when it starts.
- **Undoing an import keeps what was played since.** If live play had carried on one of its entries (an episode
  half-watched at install and finished later), undoing the import used to stop that play counting too, and importing
  again did not bring it back. Now the played part is kept as an entry of its own, credited the time played (or the
  running time, if it was finished while being played); only what the import said stops counting. A play still going
  during the undo carries on in that entry. Importing the same records again leaves it to live recording.
- An undone import's own entries, where one had replaced another, no longer have their sessions counted again.
- The settings page says "1 session, 1 person", not "1 sessions, 1 people".
- `tools/package.sh` refuses to build code that is not committed, so each build's stamp names the commit it came from.
  The 0.3.0 zips are stamped `f2a785a`, the 0.2.0 commit: they were built before the 0.3.0 code was committed. What
  is in them is 0.3.0.
- **Black bars measured once per file**, so JellyMold opens a widescreen film already cropped, the first time too
  (design §16 idea 11). Jellyfin's own ffmpeg looks at one keyframe at each of 12 points across the running time and
  keeps the smallest bars seen. New files are measured a few minutes after they arrive, anything an app asks for at
  once, and the rest of the library for up to two hours a night. Apps can report what they found for a file not
  measured yet; the plugin's measurement always wins. On until switched off, under *Helping Jellyfin*.
  `GET`/`POST /Items/{itemId}/PictureCrop`.
- Schema 4, by migration: black bars.
- **The settings page no longer shows music and audiobook settings** (whether to record them, and their shortest
  plays), as no app plays them yet. Both are still recorded, with the settings they had.
- **Both *Helping Jellyfin* switches are now on by default**: preparing subtitles ahead of time, and putting the
  resume point back after an app vanishes mid-play (Jellyfin 10.10). On a server where the settings page was saved
  with them off, they stay off.
- On the settings page, the *Undo* button beside an import is readable in the dark theme, a finished import says
  "699 added" rather than "699 to add", and *Import for* is shown only when the server has more than one person.
- **Undo on the settings page works.** It waited for an answer Jellyfin's confirmation never gives (on 10.10 and 12
  alike), so clicking it did nothing.
- **Previews nobody imported are no longer listed** under *Imports so far*, and are cleared after a day. Looking an
  import up no longer depends on it being among the newest 200.
- 148 unit tests. Checked live on Jellyfin 10.10.7 (Docker): a letterboxed test film measured exactly, the 16:9 ones
  as having no bars, an app's report given until the plugin measured, and the nightly task.

## 0.3.0 — 2026-10-30 (builds 10.0.3.0 and 12.0.3.0)

The first published release: https://github.com/darkejon/jellymold-companion/releases/tag/v0.3.0

Phase 3: the extra features chosen from design §16. Supersedes 0.2.0, which was never published.

- **A store for each person and one for the household** — documents an app keeps on the server, written only if still
  the version read, with a change feed. Settings and quiz records sync without iCloud; quiz rounds (`quiz-round:…`)
  and chapter names (`chapters:…`) are shared by every device in the household.
- **Quiz results and a household leaderboard**, per programme or overall. Nobody's results are shown to anyone else
  until they choose to share them.
- **Learned skip segments published to Jellyfin**, so every app on the server shows Skip for an intro JellyMold found.
- **Putting the resume point back after an app vanishes mid-play** (Jellyfin 10.10; Jellyfin 12 fixed it itself). Off
  until switched on: the one feature that writes Jellyfin's own watch data. It applies Jellyfin's own rule to the
  viewer's real position, so it undoes the guess and nothing else.
- **Subtitles prepared ahead of time**: a nightly task extracts new items' embedded subtitles through Jellyfin's own
  extraction. Off until switched on.
- **The home screen in one request**: Continue Watching, Next Up and the newest in each library, as Jellyfin's own items.
- Schema 3, by migration: documents, quiz results and sharing, segments.
- 114 unit tests. Checked live on Jellyfin 12.1 and 10.10.7: the stores and their clashes, the leaderboard's opt-in,
  a learned intro reaching Jellyfin's segment list, the home screen.

## 0.2.0 — never published (builds 10.0.2.0 and 12.0.2.0)

Phase 2: importing past history (design §8).

- **Each person's baseline from Jellyfin's own watch state**, taken automatically once — shortly after the server
  starts, and for anyone added later. Everything already watched counts, credited its running time; anything under way
  at its position.
- **Playback Reporting's database**, read directly when it is on the server (admin only): real dates and seconds,
  gathered into viewings, deleted episodes rebuilt from their names, local times converted with the server's zone.
- **Files**: a Trakt export (history or watched JSON), Letterboxd's diary or watched list, and a documented CSV.
- **Every import previews first**, commits exactly what it showed, adds nothing when run twice, skips anything dated
  after recording began, recognises the same viewing from two sources (the better one counts; the other is kept), keeps
  media not in the library, and can be undone — and redone.
- **The settings page** gains a *Past history* section: Playback Reporting, file imports for any person, the list of
  imports with undo.
- **Audiobooks split into chapter files** count as one book, finished when its last file is.
- Schema 2: media known only from an import, and each import's preview. Added by migration; nothing removed.
- 93 unit tests. Checked live: the baselines on Jellyfin 12.1 and 10.10.7; on 10.10.7 a real Playback Reporting
  database, a Letterboxd file that duplicates it, and undo.

## 0.1.0 — never published (builds 10.0.1.0 and 12.0.1.0)

The first build: Phase 1 of the design.

- **Records every playback, per person**, from any app: films, episodes, other videos, music and audiobooks. Events,
  sessions and viewings (design §3), with a snapshot of each item kept so the record outlives the file.
- **The counting rules** (§5): actual time from position, not the clock; completion at the server's resume setting
  (audiobooks with five minutes left, songs at half or four minutes heard); credited time; rewatches; short plays kept
  but uncounted; repeat-one loops counted each time round.
- **The awkward cases** (§6): Jellyfin's invented progress ignored; idle stops ended at the last real position, and
  reopened if the app comes back within half an hour; sessions that end with no stop; server restarts; ticks from
  people, from JellyMold and (on Jellyfin 12) from other versions of a film told apart; a tick taken back within a day
  retracted.
- **The API** (docs/api.md): Info, Summary, Films, Series, History, per-item history, the change feed, sessions with
  their events, offline sessions, export. Per person by default; admins may name anyone.
- **The database** (§10): SQLite under Jellyfin's data path, migrations that only add, a nightly backup task keeping
  seven daily and four weekly copies, an integrity check at startup that restores the latest backup if it fails.
- **The settings page**: what and who to record, the shortest play that counts, retention, backups, export.
- 71 unit tests; an end-to-end check passed against Jellyfin 12.1 (the local server) and 10.10.7 (Docker), including
  per-person access.
