-- Schema version 1. Design §3.
--
-- Migrations ONLY ADD (design §10.2): new tables, new columns, new indexes. A test reads every script and fails
-- the build on DROP TABLE, DROP COLUMN or a table rename. Timestamps are UTC milliseconds; ids are Jellyfin's
-- 32-character GUIDs.

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE users (
    user_id         TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    first_seen_at   INTEGER NOT NULL,
    recording_since INTEGER,          -- the cutover (§8)
    deleted_at      INTEGER
);

-- What an item was. Survives the item (§7).
CREATE TABLE media (
    item_id             TEXT PRIMARY KEY,
    kind                TEXT NOT NULL,
    name                TEXT NOT NULL,
    original_title      TEXT,
    production_year     INTEGER,
    premiere_date       TEXT,
    runtime_ticks       INTEGER,
    series_id           TEXT,
    series_name         TEXT,
    season_id           TEXT,
    season_name         TEXT,
    season_number       INTEGER,
    episode_number      INTEGER,
    episode_number_end  INTEGER,
    album_id            TEXT,
    album_name          TEXT,
    artists             TEXT,         -- JSON array
    album_artists       TEXT,         -- JSON array
    track_number        INTEGER,
    disc_number         INTEGER,
    provider_ids        TEXT,         -- JSON object, as Jellyfin holds them
    series_provider_ids TEXT,
    library_id          TEXT,
    library_name        TEXT,
    first_seen_at       INTEGER NOT NULL,
    updated_at          INTEGER NOT NULL,
    removed_at          INTEGER
);
CREATE INDEX media_series ON media (series_id);
CREATE INDEX media_season ON media (season_id);
CREATE INDEX media_album  ON media (album_id);

CREATE TABLE media_keys (
    item_id TEXT NOT NULL REFERENCES media (item_id),
    key     TEXT NOT NULL,
    PRIMARY KEY (item_id, key)
);
CREATE INDEX media_keys_key ON media_keys (key);

-- "I watched it" / "I listened to it" (§3, §5.4).
CREATE TABLE viewings (
    viewing_id       TEXT PRIMARY KEY,
    user_id          TEXT NOT NULL REFERENCES users (user_id),
    item_id          TEXT NOT NULL REFERENCES media (item_id),
    kind             TEXT NOT NULL,   -- played | marked | imported
    source           TEXT NOT NULL,   -- live | client | jellyfin | playback-reporting | trakt | …
    import_id        TEXT,
    source_key       TEXT,            -- the source's own id for the record, so an import twice adds nothing
    status           TEXT NOT NULL,   -- in-progress | completed | abandoned | retracted | undone
    started_at       INTEGER NOT NULL,
    last_activity_at INTEGER NOT NULL,
    completed_at     INTEGER,
    completed_by     TEXT,            -- position | mark | import
    furthest_ticks   INTEGER NOT NULL DEFAULT 0,
    runtime_ticks    INTEGER,
    actual_seconds   REAL NOT NULL DEFAULT 0,
    wall_seconds     REAL NOT NULL DEFAULT 0,
    credited_seconds REAL NOT NULL DEFAULT 0,
    estimated        INTEGER NOT NULL DEFAULT 0,
    play_count_noted INTEGER,
    superseded_by    TEXT,
    rule             TEXT,            -- the completion rule it was judged by (§5.2)
    updated_seq      INTEGER NOT NULL
);
CREATE INDEX viewings_user_activity ON viewings (user_id, last_activity_at);
CREATE INDEX viewings_user_item     ON viewings (user_id, item_id);
CREATE INDEX viewings_seq           ON viewings (updated_seq);
CREATE UNIQUE INDEX viewings_source_key ON viewings (source, user_id, source_key) WHERE source_key IS NOT NULL;

-- One continuous play (§3).
CREATE TABLE sessions (
    session_id        TEXT PRIMARY KEY,
    user_id           TEXT NOT NULL REFERENCES users (user_id),
    item_id           TEXT NOT NULL REFERENCES media (item_id),
    viewing_id        TEXT,
    source            TEXT NOT NULL,  -- live | client
    jf_session_id     TEXT,
    play_session_id   TEXT,
    client            TEXT,
    client_version    TEXT,
    device_name       TEXT,
    device_id         TEXT,
    play_method       TEXT,
    media_source_id   TEXT,
    started_at        INTEGER NOT NULL,
    ended_at          INTEGER,
    last_seen_at      INTEGER NOT NULL,
    received_at       INTEGER NOT NULL,
    start_ticks       INTEGER NOT NULL,
    end_ticks         INTEGER,
    last_ticks        INTEGER NOT NULL,
    furthest_ticks    INTEGER NOT NULL,
    last_paused       INTEGER NOT NULL DEFAULT 0,
    runtime_ticks     INTEGER,
    actual_seconds    REAL NOT NULL DEFAULT 0,
    wall_seconds      REAL NOT NULL DEFAULT 0,
    paused_seconds    REAL NOT NULL DEFAULT 0,
    seek_count        INTEGER NOT NULL DEFAULT 0,
    playback_rate     REAL,
    end_reason        TEXT,           -- stopped | replaced | idle | session-ended | restart | swept | looped | client
    counted           INTEGER NOT NULL DEFAULT 0,
    client_session_id TEXT,
    updated_seq       INTEGER NOT NULL
);
CREATE INDEX sessions_user_item ON sessions (user_id, item_id, started_at);
CREATE INDEX sessions_open      ON sessions (ended_at) WHERE ended_at IS NULL;
CREATE INDEX sessions_viewing   ON sessions (viewing_id);
CREATE INDEX sessions_seq       ON sessions (updated_seq);
CREATE INDEX sessions_user_time ON sessions (user_id, started_at);
CREATE UNIQUE INDEX sessions_client ON sessions (user_id, client_session_id) WHERE client_session_id IS NOT NULL;

-- The raw log (§3). Kept for good: a few rows a session.
CREATE TABLE events (
    event_id       INTEGER PRIMARY KEY,
    at             INTEGER NOT NULL,
    user_id        TEXT,
    item_id        TEXT,
    session_id     TEXT,
    kind           TEXT NOT NULL,
    position_ticks INTEGER,
    detail         TEXT               -- JSON
);
CREATE INDEX events_user_time ON events (user_id, at);
CREATE INDEX events_item_time ON events (item_id, at);
CREATE INDEX events_session   ON events (session_id);

-- Every client progress report. Kept 30 days by default — diagnostics, not history.
CREATE TABLE progress_samples (
    session_id     TEXT NOT NULL,
    at             INTEGER NOT NULL,
    position_ticks INTEGER NOT NULL,
    paused         INTEGER NOT NULL
);
CREATE INDEX samples_session ON progress_samples (session_id, at);
CREATE INDEX samples_at      ON progress_samples (at);

-- Jellyfin's played flag and position as last seen, so changes to them can be logged (§6.1).
CREATE TABLE user_item_state (
    user_id        TEXT NOT NULL,
    item_id        TEXT NOT NULL,
    played         INTEGER NOT NULL,
    position_ticks INTEGER NOT NULL,
    updated_at     INTEGER NOT NULL,
    PRIMARY KEY (user_id, item_id)
);

CREATE TABLE imports (
    import_id    TEXT PRIMARY KEY,
    source       TEXT NOT NULL,
    user_id      TEXT,                -- null: server-wide
    created_at   INTEGER NOT NULL,
    committed_at INTEGER,
    undone_at    INTEGER,
    detail       TEXT
);

-- Ids removed from the change feed by an undone import (§9.4).
CREATE TABLE tombstones (
    seq  INTEGER NOT NULL,
    kind TEXT NOT NULL,
    id   TEXT NOT NULL
);
CREATE INDEX tombstones_seq ON tombstones (seq);
