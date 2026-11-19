-- Schema version 5. Ratings from other sites, fetched from MDBList with the server owner's own key (design §16 idea 13).
-- Only adds, like every migration.

-- Each film's or series' ratings, one row per source, on the source's own scale (IMDb out of 10, the Tomatometer a
-- percentage, Letterboxd out of 5). Replaced whole each time the title is fetched again.
CREATE TABLE title_ratings (
    item_id     TEXT NOT NULL,
    source      TEXT NOT NULL,          -- 'imdb' | 'tomatoes' | 'tomatoesAudience' | 'metacritic' | 'tmdb' | 'letterboxd' | 'trakt' | 'rogerEbert' | 'myAnimeList'
    value       REAL NOT NULL,
    votes       INTEGER,
    fetched_at  INTEGER NOT NULL,
    PRIMARY KEY (item_id, source)
);

-- When each title was last asked about, and what came of it, so it is asked again only when due.
CREATE TABLE rating_fetches (
    item_id     TEXT PRIMARY KEY,
    provider    TEXT NOT NULL,          -- the id it was looked up by: 'tmdb' | 'tvdb' | 'imdb'
    provider_id TEXT NOT NULL,
    kind        TEXT NOT NULL,          -- 'movie' | 'show'
    outcome     TEXT NOT NULL,          -- 'found' | 'notFound' (MDBList does not know it) | 'failed'
    fetched_at  INTEGER NOT NULL
);
