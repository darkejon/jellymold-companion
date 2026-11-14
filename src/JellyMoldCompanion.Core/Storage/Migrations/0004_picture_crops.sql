-- Schema version 4. Black bars built into the picture (design §16 idea 11). Only adds, like every migration.

-- How much of each file's frame is black bars — a widescreen film stored in a 16:9 frame — so an app can open it already
-- cropped. One row per file and source: the plugin's own measurement ('server'), and what apps have reported ('app').
-- The plugin's measurement wins whenever it has one; apps' reports fill the gap until it does.
CREATE TABLE picture_crops (
    media_source_id TEXT NOT NULL,
    source          TEXT NOT NULL,          -- 'server' | 'app'
    item_id         TEXT NOT NULL,
    outcome         TEXT NOT NULL,          -- 'measured' | 'unmeasurable' (no picture found) | 'failed' (ffmpeg could not read it)
    width           INTEGER NOT NULL,       -- the frame the bars are in, in its stored pixels
    height          INTEGER NOT NULL,
    bar_top         INTEGER NOT NULL,
    bar_bottom      INTEGER NOT NULL,
    bar_left        INTEGER NOT NULL,
    bar_right       INTEGER NOT NULL,
    file_path       TEXT,                   -- the file measured, so a replaced file is measured again
    file_size       INTEGER,
    file_modified   INTEGER,
    samples         INTEGER NOT NULL,       -- frames that showed a picture; for an app's report, 0
    method          INTEGER NOT NULL,       -- how it was measured, so a better way can measure everything again
    reported_by     TEXT,                   -- for an app's report, who sent it
    measured_at     INTEGER NOT NULL,
    PRIMARY KEY (media_source_id, source)
);
CREATE INDEX picture_crops_item ON picture_crops (item_id);
