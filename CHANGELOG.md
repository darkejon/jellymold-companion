# Changelog

Plugin versions carry the Jellyfin major version first: `10.x.y.z` is the build for Jellyfin 10.10, `12.x.y.z` the
build for Jellyfin 12 (design §13a). Both come from the same release.

## 0.1.0 — not yet released (builds 10.0.1.0 and 12.0.1.0)

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
