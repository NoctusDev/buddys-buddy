/**
 * Buddy's Jobs — the board every copy of the plugin talks to.
 *
 * Two Dalamud plugins cannot see each other: Dalamud's IPC is same-client only, so anything shared
 * between players needs something in the middle. This is that something, and it is deliberately the
 * smallest thing that works — no accounts, no sessions, no user records. A board is a shared secret;
 * knowing it is what lets you read and write that board's jobs.
 *
 * The secret is never stored. Its SHA-256 is the board id, so this database cannot be used to join a
 * board it did not already belong to.
 *
 * Claiming is a single conditional UPDATE, which is what makes two people claiming the same job at
 * the same time resolve correctly: SQLite decides, one of them gets `changes === 1`, the other is
 * told it was already taken. No locking, no read-then-write race.
 */

const MAX_BODY = 8 * 1024;
const MAX_OPEN_JOBS = 500;
const HISTORY_DAYS = 30;
const PROTOCOL = 1;

export default {
    async fetch(request, env) {
        try {
            return await handle(request, env);
        } catch (err) {
            return json({ error: 'internal error', detail: String(err && err.message) }, 500);
        }
    },
};

async function handle(request, env) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, '');

    if (request.method === 'OPTIONS') return preflight();

    // Answers without a board key, so a client can tell "the server is up" apart from
    // "my join code is wrong" — two failures that otherwise look identical.
    if (path === '/v1/health') {
        return json({ ok: true, protocol: PROTOCOL, time: now() });
    }

    if (path.startsWith('/v1/venue/')) return venue(request, env, path);

    const board = await boardOf(request);
    if (!board) return json({ error: 'missing or malformed board key' }, 401);

    const jobId = matchJob(path);

    if (path === '/v1/jobs' && request.method === 'GET') return listJobs(env, board);
    if (path === '/v1/jobs' && request.method === 'POST') return createJob(env, board, await body(request));

    if (jobId) {
        const action = path.slice(path.lastIndexOf('/') + 1);
        const payload = request.method === 'POST' ? await body(request) : {};

        switch (action) {
            case 'claim':    return claimJob(env, board, jobId, payload);
            case 'release':  return releaseJob(env, board, jobId, payload);
            case 'complete': return completeJob(env, board, jobId, payload);
            case 'cancel':   return cancelJob(env, board, jobId, payload);
        }
    }

    return json({ error: 'not found' }, 404);
}

/** `/v1/jobs/<id>/<action>` → the id, or null. Ids are uuids, so the shape is checkable. */
function matchJob(path) {
    const m = path.match(/^\/v1\/jobs\/([0-9a-f-]{36})\/[a-z]+$/);
    return m ? m[1] : null;
}

// ── The board key ────────────────────────────────────────────────────────────

/**
 * A board is identified by the hash of its join code, never by the code.
 *
 * Short codes are rejected outright: the code is the only thing standing between a stranger and the
 * board, and a four-character one is not a secret.
 */
async function boardOf(request) {
    const header = request.headers.get('authorization') || '';
    const key = header.toLowerCase().startsWith('bearer ') ? header.slice(7).trim() : '';
    if (key.length < 16) return null;

    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(key));
    return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
}

// ── Jobs ─────────────────────────────────────────────────────────────────────

async function listJobs(env, board) {
    const cutoff = now() - HISTORY_DAYS * 86400;

    const { results } = await env.DB.prepare(
        `SELECT * FROM jobs
          WHERE board = ?1
            AND (state IN ('open', 'claimed') OR updated_at >= ?2)
          ORDER BY updated_at DESC
          LIMIT 1000`,
    ).bind(board, cutoff).all();

    return json({ protocol: PROTOCOL, time: now(), jobs: (results || []).map(shape) });
}

async function createJob(env, board, payload) {
    const itemId = int(payload.itemId);
    const quantity = int(payload.quantity);
    const postedBy = text(payload.actor, 64);

    if (!itemId) return json({ error: 'itemId is required' }, 400);
    if (quantity < 1) return json({ error: 'quantity must be at least 1' }, 400);
    if (!postedBy) return json({ error: 'actor is required' }, 400);

    const open = await env.DB.prepare(
        `SELECT COUNT(*) AS n FROM jobs WHERE board = ?1 AND state IN ('open', 'claimed')`,
    ).bind(board).first();

    if (open && open.n >= MAX_OPEN_JOBS) {
        return json({ error: `this board already has ${MAX_OPEN_JOBS} jobs open` }, 429);
    }

    const id = crypto.randomUUID();
    const stamp = now();

    await env.DB.prepare(
        `INSERT INTO jobs (id, board, item_id, item_name, quantity, delivered, hq,
                           reward, note, state, posted_by, created_at, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, 0, ?6, ?7, ?8, 'open', ?9, ?10, ?10)`,
    ).bind(
        id, board, itemId, text(payload.itemName, 128) || `item #${itemId}`, quantity,
        payload.hq ? 1 : 0, text(payload.reward, 128), text(payload.note, 256), postedBy, stamp,
    ).run();

    return json({ ok: true, id, job: shape(await fetchJob(env, board, id)) });
}

/**
 * Takes a job, if it is still there to take.
 *
 * The whole race lives in the WHERE clause. Two people claiming at once both run this; exactly one
 * of them changes a row, and the loser is told who beat them rather than silently overwriting.
 */
async function claimJob(env, board, id, payload) {
    const actor = text(payload.actor, 64);
    if (!actor) return json({ error: 'actor is required' }, 400);

    const result = await env.DB.prepare(
        `UPDATE jobs SET state = 'claimed', claimed_by = ?3, claimed_at = ?4, updated_at = ?4
          WHERE id = ?1 AND board = ?2 AND state = 'open'`,
    ).bind(id, board, actor, now()).run();

    const job = await fetchJob(env, board, id);
    if (!job) return json({ error: 'no such job' }, 404);

    if (!changed(result)) {
        return json({ error: 'already taken', by: job.claimed_by, job: shape(job) }, 409);
    }

    return json({ ok: true, job: shape(job) });
}

/** Puts a claimed job back. The claimer or whoever posted it may do this; nobody else. */
async function releaseJob(env, board, id, payload) {
    const actor = text(payload.actor, 64);

    const result = await env.DB.prepare(
        `UPDATE jobs SET state = 'open', claimed_by = NULL, claimed_at = NULL, updated_at = ?4
          WHERE id = ?1 AND board = ?2 AND state = 'claimed'
            AND (claimed_by = ?3 OR posted_by = ?3)`,
    ).bind(id, board, actor, now()).run();

    const job = await fetchJob(env, board, id);
    if (!job) return json({ error: 'no such job' }, 404);
    if (!changed(result)) return json({ error: 'not yours to release', job: shape(job) }, 403);

    return json({ ok: true, job: shape(job) });
}

/**
 * Records a delivery.
 *
 * Partial deliveries are the normal case, not an edge case — "500 iron ore" is three people bringing
 * what they could. Anything short of the target adds to the running total and puts the job back on
 * the board for the remainder, so the next person picks up where the last one left off.
 */
async function completeJob(env, board, id, payload) {
    const actor = text(payload.actor, 64);
    if (!actor) return json({ error: 'actor is required' }, 400);

    const job = await fetchJob(env, board, id);
    if (!job) return json({ error: 'no such job' }, 404);
    if (job.state === 'done' || job.state === 'cancelled') {
        return json({ error: `job is already ${job.state}`, job: shape(job) }, 409);
    }
    if (job.claimed_by && job.claimed_by !== actor && job.posted_by !== actor) {
        return json({ error: 'someone else has this one', by: job.claimed_by, job: shape(job) }, 403);
    }

    const amount = payload.amount === undefined ? job.quantity - job.delivered : int(payload.amount);
    if (amount < 1) return json({ error: 'amount must be at least 1' }, 400);

    const delivered = Math.min(job.quantity, job.delivered + amount);
    const finished = delivered >= job.quantity;

    await env.DB.prepare(
        `UPDATE jobs
            SET delivered = ?3,
                state = ?4,
                claimed_by = CASE WHEN ?4 = 'open' THEN NULL ELSE claimed_by END,
                claimed_at = CASE WHEN ?4 = 'open' THEN NULL ELSE claimed_at END,
                updated_at = ?5
          WHERE id = ?1 AND board = ?2`,
    ).bind(id, board, delivered, finished ? 'done' : 'open', now()).run();

    return json({ ok: true, finished, delivered, job: shape(await fetchJob(env, board, id)) });
}

/** Calls a job off. Only whoever posted it may do this. */
async function cancelJob(env, board, id, payload) {
    const actor = text(payload.actor, 64);

    const result = await env.DB.prepare(
        `UPDATE jobs SET state = 'cancelled', updated_at = ?4
          WHERE id = ?1 AND board = ?2 AND posted_by = ?3 AND state IN ('open', 'claimed')`,
    ).bind(id, board, actor, now()).run();

    const job = await fetchJob(env, board, id);
    if (!job) return json({ error: 'no such job' }, 404);
    if (!changed(result)) return json({ error: 'only the person who posted it can cancel it' }, 403);

    return json({ ok: true, job: shape(job) });
}

function fetchJob(env, board, id) {
    return env.DB.prepare(`SELECT * FROM jobs WHERE id = ?1 AND board = ?2`).bind(id, board).first();
}

/** D1 has moved `changes` between `meta` and the top level across versions; accept either. */
function changed(result) {
    const meta = result && result.meta ? result.meta : result || {};
    return (meta.changes || 0) > 0;
}

/** The wire shape. The board hash never leaves the server. */
function shape(row) {
    if (!row) return null;

    return {
        id: row.id,
        itemId: row.item_id,
        itemName: row.item_name,
        quantity: row.quantity,
        delivered: row.delivered,
        hq: !!row.hq,
        reward: row.reward || '',
        note: row.note || '',
        state: row.state,
        postedBy: row.posted_by,
        claimedBy: row.claimed_by || '',
        claimedAt: row.claimed_at || 0,
        createdAt: row.created_at,
        updatedAt: row.updated_at,
    };
}

// ── Venue Audio short codes ──────────────────────────────────────────────────

/**
 * A six-character code stands in for a long guest invite.
 *
 * The plugin hashes the code into `id` and encrypts the invite under a key derived from it, so this
 * table holds neither codes nor readable invites. Six characters is not a secret against someone who
 * holds the table and brute-forces it offline; it only keeps the stored rows opaque.
 *
 * Rows expire unless the host keeps refreshing them, so a crashed broadcast cleans itself up.
 */
const VENUE_TTL = 15 * 60;
const VENUE_MAX_BLOB = 4096;
const CODE_ALPHABET = /^[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{6}$/;

async function venue(request, env, path) {
    const stamp = now();
    const code = path.match(/^\/v1\/venue\/codes\/([0-9a-f]{64})$/);
    const at = path.match(/^\/v1\/venue\/at\/([0-9a-f]{1,32})$/);

    if (code && request.method === 'GET') {
        const row = await env.DB.prepare(`SELECT blob FROM venue_codes WHERE id = ?1 AND expires_at > ?2`)
            .bind(code[1], stamp).first();
        return row ? json({ blob: row.blob }) : json({ error: 'no venue has that code right now' }, 404);
    }

    if (at && request.method === 'GET') {
        const { results } = await env.DB.prepare(
            `SELECT public_code FROM venue_codes
              WHERE location = ?1 AND public_code IS NOT NULL AND expires_at > ?2
              ORDER BY expires_at DESC LIMIT 4`,
        ).bind(at[1], stamp).all();
        return json({ codes: (results || []).map(r => r.public_code) });
    }

    if (!code) return json({ error: 'not found' }, 404);

    const owner = await ownerOf(request);
    if (!owner) return json({ error: 'missing or malformed owner token' }, 401);

    if (request.method === 'PUT') {
        const payload = await body(request);
        const blob = typeof payload.blob === 'string' && payload.blob.length <= VENUE_MAX_BLOB && /^[A-Za-z0-9+/=]+$/.test(payload.blob)
            ? payload.blob : '';
        if (!blob) return json({ error: 'blob is required' }, 400);

        const location = typeof payload.location === 'string' && /^[0-9a-f]{1,32}$/.test(payload.location) ? payload.location : null;
        const publicCode = location && typeof payload.code === 'string' && CODE_ALPHABET.test(payload.code) ? payload.code : null;
        if (location && !publicCode) return json({ error: 'announcing needs the code' }, 400);

        await env.DB.prepare(`DELETE FROM venue_codes WHERE expires_at <= ?1`).bind(stamp).run();

        // One statement decides ownership: an existing live row only changes if the same owner sent it.
        const result = await env.DB.prepare(
            `INSERT INTO venue_codes (id, owner, blob, location, public_code, expires_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)
             ON CONFLICT (id) DO UPDATE SET blob = ?3, location = ?4, public_code = ?5, expires_at = ?6
              WHERE venue_codes.owner = ?2`,
        ).bind(code[1], owner, blob, location, publicCode, stamp + VENUE_TTL).run();

        if (!changed(result)) return json({ error: 'that code is taken' }, 409);
        return json({ ok: true, expiresAt: stamp + VENUE_TTL });
    }

    if (request.method === 'DELETE') {
        await env.DB.prepare(`DELETE FROM venue_codes WHERE id = ?1 AND owner = ?2`).bind(code[1], owner).run();
        return json({ ok: true });
    }

    return json({ error: 'method not allowed' }, 405);
}

async function ownerOf(request) {
    const header = request.headers.get('authorization') || '';
    const token = header.toLowerCase().startsWith('bearer ') ? header.slice(7).trim() : '';
    if (!/^[0-9a-f]{64}$/.test(token)) return null;

    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(token));
    return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
}

// ── Plumbing ─────────────────────────────────────────────────────────────────

const now = () => Math.floor(Date.now() / 1000);

const int = value => {
    const n = Number.parseInt(value, 10);
    return Number.isFinite(n) && n > 0 ? n : 0;
};

const text = (value, max) =>
    typeof value === 'string' ? value.trim().slice(0, max) : '';

async function body(request) {
    const raw = await request.text();
    if (raw.length > MAX_BODY) throw new Error('request body too large');

    try {
        return raw ? JSON.parse(raw) : {};
    } catch {
        throw new Error('body is not valid JSON');
    }
}

const CORS = {
    'access-control-allow-origin': '*',
    'access-control-allow-headers': 'authorization, content-type',
    'access-control-allow-methods': 'GET, POST, PUT, DELETE, OPTIONS',
};

const json = (payload, status = 200) =>
    new Response(JSON.stringify(payload), {
        status,
        headers: { 'content-type': 'application/json; charset=utf-8', ...CORS },
    });

const preflight = () => new Response(null, { status: 204, headers: CORS });
