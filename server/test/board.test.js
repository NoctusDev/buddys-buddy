/**
 * Exercises the real worker against real SQLite.
 *
 * `wrangler dev` would be the closest thing to production, but it needs Node 22 and this machine
 * runs 20 — so instead the worker's own `fetch` is called with real Request objects, over a D1-shaped
 * shim backed by SQLite compiled to wasm. Everything under test is the shipped code: the routing,
 * the SHA-256 board hashing, and above all the conditional UPDATEs, which are the part that has to
 * be right and the part that cannot be checked by reading it.
 *
 *   npm install && npm test
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import initSqlJs from 'sql.js';

import worker from '../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const SQL = await initSqlJs();

const KEY = 'a-join-code-long-enough-to-be-a-secret';
const OTHER_KEY = 'a-different-board-entirely-0123456789';

const BUDDY = 'Buddy Bud@Zodiark';
const HELPER = 'Some Helper@Phoenix';
const STRANGER = 'Nosy Person@Lich';

let passed = 0;
let failed = 0;

function check(ok, label, detail = '') {
    console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ' — ' + detail : ''}`);
    ok ? passed++ : failed++;
}

/**
 * The smallest thing that behaves like a D1 binding.
 *
 * D1 has moved `changes` between `meta` and the top level across versions, and the worker accepts
 * either — so this reports it the way current D1 does, under `meta`.
 */
function makeDb() {
    const db = new SQL.Database();
    db.run(readFileSync(join(here, '..', 'schema.sql'), 'utf8'));

    return {
        prepare(sql) {
            return {
                bind(...args) {
                    const params = args.map(v => (v === undefined ? null : v));

                    const rows = () => {
                        const stmt = db.prepare(sql);
                        stmt.bind(params);
                        const out = [];
                        while (stmt.step()) out.push(stmt.getAsObject());
                        stmt.free();
                        return out;
                    };

                    return {
                        all: async () => ({ results: rows() }),
                        first: async () => rows()[0] ?? null,
                        run: async () => {
                            db.run(sql, params);
                            return { meta: { changes: db.getRowsModified() } };
                        },
                    };
                },
            };
        },
    };
}

const env = { DB: makeDb() };

const call = (method, path, { key, body } = {}) => {
    const headers = { 'content-type': 'application/json' };
    if (key) headers.authorization = `Bearer ${key}`;

    return worker.fetch(
        new Request(`https://board.test${path}`, {
            method,
            headers,
            body: body === undefined ? undefined : JSON.stringify(body),
        }),
        env,
    );
};

const read = async response => ({ status: response.status, body: await response.json() });

// ── Reachability and auth ────────────────────────────────────────────────────

console.log('\nReachability');
{
    const { status, body } = await read(await call('GET', '/v1/health'));
    check(status === 200 && body.ok === true, 'health answers without a board key');
    check(typeof body.protocol === 'number', 'health names a protocol version', `protocol ${body.protocol}`);
}

console.log('\nBoard keys');
{
    const missing = await read(await call('GET', '/v1/jobs'));
    check(missing.status === 401, 'no key is rejected', `${missing.status}`);

    const short = await read(await call('GET', '/v1/jobs', { key: 'tooshort' }));
    check(short.status === 401, 'a key under 16 characters is rejected', `${short.status}`);

    const ok = await read(await call('GET', '/v1/jobs', { key: KEY }));
    check(ok.status === 200 && Array.isArray(ok.body.jobs), 'a real key reads the board');
}

// ── Posting ──────────────────────────────────────────────────────────────────

console.log('\nPosting a job');
let jobId;
{
    const bad = await read(await call('POST', '/v1/jobs', {
        key: KEY,
        body: { itemId: 5111, quantity: 0, actor: BUDDY },
    }));
    check(bad.status === 400, 'a quantity of zero is refused', bad.body.error);

    const anon = await read(await call('POST', '/v1/jobs', {
        key: KEY,
        body: { itemId: 5111, quantity: 10 },
    }));
    check(anon.status === 400, 'a job with no actor is refused', anon.body.error);

    const created = await read(await call('POST', '/v1/jobs', {
        key: KEY,
        body: {
            itemId: 5111, itemName: 'Iron Ore', quantity: 500,
            reward: '200g each', note: 'no rush', actor: BUDDY,
        },
    }));

    jobId = created.body.id;
    check(created.status === 200 && !!jobId, 'a job is created');
    check(created.body.job.state === 'open', 'a new job starts open', created.body.job.state);
    check(created.body.job.delivered === 0, 'a new job has delivered nothing');

    const listed = await read(await call('GET', '/v1/jobs', { key: KEY }));
    check(listed.body.jobs.length === 1, 'it appears on the board', `${listed.body.jobs.length} job(s)`);
    check(listed.body.jobs[0].itemName === 'Iron Ore', 'the item name survives');
}

console.log('\nBoard isolation');
{
    const other = await read(await call('GET', '/v1/jobs', { key: OTHER_KEY }));
    check(other.status === 200 && other.body.jobs.length === 0,
        'a different board key sees none of it', `${other.body.jobs.length} job(s)`);
}

// ── Claiming ─────────────────────────────────────────────────────────────────

console.log('\nClaiming');
{
    const first = await read(await call('POST', `/v1/jobs/${jobId}/claim`, {
        key: KEY, body: { actor: HELPER },
    }));
    check(first.status === 200 && first.body.job.state === 'claimed', 'the first claim wins');
    check(first.body.job.claimedBy === HELPER, 'the claimer is recorded', first.body.job.claimedBy);

    // The whole race lives in the conditional UPDATE; this is the loser's path.
    const second = await read(await call('POST', `/v1/jobs/${jobId}/claim`, {
        key: KEY, body: { actor: STRANGER },
    }));
    check(second.status === 409, 'the second claim loses', `${second.status}`);
    check(second.body.by === HELPER, 'the loser is told who beat them', second.body.by);
}

console.log('\nReleasing');
{
    const nosy = await read(await call('POST', `/v1/jobs/${jobId}/release`, {
        key: KEY, body: { actor: STRANGER },
    }));
    check(nosy.status === 403, 'a stranger cannot release it', `${nosy.status}`);

    const owner = await read(await call('POST', `/v1/jobs/${jobId}/release`, {
        key: KEY, body: { actor: HELPER },
    }));
    check(owner.status === 200 && owner.body.job.state === 'open', 'the claimer can put it back');
    check(owner.body.job.claimedBy === '', 'releasing clears the claimer');

    // Re-claim so the delivery tests have something claimed to work with.
    await call('POST', `/v1/jobs/${jobId}/claim`, { key: KEY, body: { actor: HELPER } });
}

// ── Delivering ───────────────────────────────────────────────────────────────

console.log('\nDelivering');
{
    const stranger = await read(await call('POST', `/v1/jobs/${jobId}/complete`, {
        key: KEY, body: { actor: STRANGER, amount: 100 },
    }));
    check(stranger.status === 403, 'somebody else cannot deliver a claimed job', `${stranger.status}`);

    const partial = await read(await call('POST', `/v1/jobs/${jobId}/complete`, {
        key: KEY, body: { actor: HELPER, amount: 200 },
    }));
    check(partial.status === 200 && partial.body.finished === false, 'a partial delivery does not finish it');
    check(partial.body.job.delivered === 200, 'the running total goes up', `${partial.body.job.delivered}`);
    check(partial.body.job.state === 'open', 'the remainder goes back on the board', partial.body.job.state);
    check(partial.body.job.claimedBy === '', 'the remainder is unclaimed again');

    // Somebody else finishes what the first person started.
    await call('POST', `/v1/jobs/${jobId}/claim`, { key: KEY, body: { actor: STRANGER } });

    const over = await read(await call('POST', `/v1/jobs/${jobId}/complete`, {
        key: KEY, body: { actor: STRANGER, amount: 9999 },
    }));
    check(over.status === 200 && over.body.job.delivered === 500,
        'delivering more than asked for does not overshoot', `${over.body.job.delivered}`);
    check(over.body.job.state === 'done', 'the job finishes', over.body.job.state);

    const again = await read(await call('POST', `/v1/jobs/${jobId}/complete`, {
        key: KEY, body: { actor: STRANGER, amount: 10 },
    }));
    check(again.status === 409, 'a finished job cannot be delivered again', `${again.status}`);
}

// ── Cancelling ───────────────────────────────────────────────────────────────

console.log('\nCancelling');
{
    const created = await read(await call('POST', '/v1/jobs', {
        key: KEY,
        body: { itemId: 5112, itemName: 'Iron Ingot', quantity: 20, actor: BUDDY },
    }));
    const id = created.body.id;

    const stranger = await read(await call('POST', `/v1/jobs/${id}/cancel`, {
        key: KEY, body: { actor: STRANGER },
    }));
    check(stranger.status === 403, 'only the person who asked can cancel it', `${stranger.status}`);

    const poster = await read(await call('POST', `/v1/jobs/${id}/cancel`, {
        key: KEY, body: { actor: BUDDY },
    }));
    check(poster.status === 200 && poster.body.job.state === 'cancelled', 'the poster can cancel it');
}

console.log('\nUnknown routes');
{
    const missing = await read(await call('GET', '/v1/nope', { key: KEY }));
    check(missing.status === 404, 'an unknown path is a 404', `${missing.status}`);

    const badId = await read(await call('POST', '/v1/jobs/not-a-uuid/claim', { key: KEY, body: { actor: BUDDY } }));
    check(badId.status === 404, 'a malformed job id is a 404', `${badId.status}`);
}

console.log(`\n${passed} passed, ${failed} failed.`);
process.exit(failed === 0 ? 0 : 1);
