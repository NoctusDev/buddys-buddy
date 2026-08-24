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
