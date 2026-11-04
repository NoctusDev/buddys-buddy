-- Buddy's Jobs — the whole database.
--
-- One table. A "board" is a group of people who share a join code; the code itself is never stored,
-- only its SHA-256, so the database is useless for joining a board even if it leaks.
--
-- Apply with:  npx wrangler d1 execute buddys-jobs --remote --file=schema.sql

CREATE TABLE IF NOT EXISTS jobs (
    id          TEXT PRIMARY KEY,
    board       TEXT NOT NULL,

    item_id     INTEGER NOT NULL,
    item_name   TEXT NOT NULL,
    quantity    INTEGER NOT NULL,
    delivered   INTEGER NOT NULL DEFAULT 0,
    hq          INTEGER NOT NULL DEFAULT 0,

    reward      TEXT,
    note        TEXT,

    -- open | claimed | done | cancelled
    state       TEXT NOT NULL DEFAULT 'open',

    posted_by   TEXT NOT NULL,
    claimed_by  TEXT,
    claimed_at  INTEGER,

    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL
);

-- Every read is "this board, recently changed first", so this is the only index that earns its keep.
CREATE INDEX IF NOT EXISTS jobs_board_updated ON jobs (board, updated_at DESC);

-- Venue Audio short codes. `id` is SHA-256 of the code, so a leaked table names no codes, and `blob`
-- is the guest invite encrypted under a key derived from the code. `public_code` is set only when the
-- host announces at a location, which makes that one venue joinable by anyone standing there.
CREATE TABLE IF NOT EXISTS venue_codes (
    id          TEXT PRIMARY KEY,
    owner       TEXT NOT NULL,
    blob        TEXT NOT NULL,
    location    TEXT,
    public_code TEXT,
    expires_at  INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS venue_codes_location ON venue_codes (location, expires_at);
CREATE INDEX IF NOT EXISTS venue_codes_expiry ON venue_codes (expires_at);
