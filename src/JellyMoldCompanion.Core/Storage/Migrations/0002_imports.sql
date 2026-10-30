-- Schema version 2. Imports (design §8). Only adds, like every migration.

-- Where an item is known from: NULL for this library; 'import' for something only an import mentioned — media
-- that may never have been on this server, kept so its history is not lost (§8.1: unmatched items are kept).
ALTER TABLE media ADD COLUMN origin TEXT;

-- An import's preview, applied exactly as shown when it is committed.
ALTER TABLE imports ADD COLUMN candidates TEXT;

CREATE INDEX viewings_import ON viewings (import_id);
CREATE INDEX imports_user ON imports (user_id, created_at);
