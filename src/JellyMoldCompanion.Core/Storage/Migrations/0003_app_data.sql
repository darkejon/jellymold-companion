-- Schema version 3. The app's own data (design §16, Phase 3). Only adds, like every migration.

-- Documents an app keeps on the server (§16 idea 1): one person's, or the household's (owner '*') — quiz rounds
-- built on one device and played on another, chapter names made once. Written only if still the version the
-- writer read; on a clash the app merges and tries again, so the merge rules stay in the app.
CREATE TABLE documents (
    owner       TEXT NOT NULL,          -- a user id, or '*' for the household
    key         TEXT NOT NULL,
    version     INTEGER NOT NULL,
    value       TEXT NOT NULL,          -- JSON, as the app sent it
    deleted     INTEGER NOT NULL DEFAULT 0, -- kept as a marker, so other devices hear about a deletion
    updated_by  TEXT,
    updated_at  INTEGER NOT NULL,
    updated_seq INTEGER NOT NULL,
    PRIMARY KEY (owner, key)
);
CREATE INDEX documents_seq ON documents (owner, updated_seq);

-- Quiz results, so a household can be ranked (§16 idea 2). One row per finished quiz, by the app's own id for it.
CREATE TABLE quiz_scores (
    score_id     TEXT NOT NULL,
    user_id      TEXT NOT NULL,
    item_id      TEXT,
    item_name    TEXT,
    series_id    TEXT,
    series_name  TEXT,
    season       INTEGER,
    episode      INTEGER,
    score        INTEGER NOT NULL,
    out_of       INTEGER NOT NULL,
    completed_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, score_id)
);
CREATE INDEX quiz_scores_series ON quiz_scores (series_id);
CREATE INDEX quiz_scores_item ON quiz_scores (item_id);

-- Who has said their quiz results may be shown to the rest of the household. Nobody, until they say so.
CREATE TABLE quiz_sharing (
    user_id    TEXT PRIMARY KEY,
    shares     INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- Skip segments learned by an app — an intro found by comparing a season's chapters — published to Jellyfin so every
-- app gets the Skip button (§16 idea 4). One of each kind per item.
CREATE TABLE segments (
    item_id     TEXT NOT NULL,
    kind        TEXT NOT NULL,          -- Intro | Outro | Recap | Preview | Commercial
    start_ticks INTEGER NOT NULL,
    end_ticks   INTEGER NOT NULL,
    source      TEXT,                   -- what the app says found it: 'learned', 'chapter', …
    updated_by  TEXT,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (item_id, kind)
);
