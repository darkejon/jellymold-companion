# Changelog

Plugin versions carry the Jellyfin major version first: `10.x.y.z` is the build for Jellyfin 10.10, `12.x.y.z` the
build for Jellyfin 12 (design §13a). Both come from the same release.

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
