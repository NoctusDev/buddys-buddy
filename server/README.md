# Buddy's Jobs — the board server

The bit in the middle. Two Dalamud plugins cannot see each other — Dalamud's IPC is same-client
only — so a shared jobs board needs a server, and this is the smallest one that does the work.

A Cloudflare Worker and a D1 database. No accounts, no sessions, no user records. Free tier covers
this comfortably: D1 allows 5 GB and 5 million row reads a day, and a board of friends will use a
rounding error of both.

## What a "board" is

A shared secret, and nothing else. Everyone who types the same join code into the plugin sees the
same jobs.

**The code is never stored.** The server keeps its SHA-256 and uses that as the board id, so the
database cannot be used to join a board it did not already belong to. There is no recovery: lose the
code and the jobs are unreachable. Generate one that is worth keeping —

```powershell
# 32 random hex characters. Anything shorter than 16 is rejected by the server.
-join ((1..32) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) })
```

## Deploying it

Once, from this folder:

```bash
npm install                      # installs the pinned wrangler and the test harness
npx wrangler login               # opens a browser, free Cloudflare account

npx wrangler d1 create buddys-jobs   # prints database_id — paste it into wrangler.toml
npm run schema                       # creates the table in the remote database

npm run deploy                       # prints the URL to paste into the plugin
```

> **Wrangler is pinned to 3.x on purpose.** Wrangler 4 requires Node ≥ 22, and the machine this was
> written on runs Node 20 — `npx wrangler@latest` installs and then refuses with `EBADENGINE`.
> Wrangler 3 deploys the same worker and the same D1 schema perfectly well. If Node ever moves past
> 22, `npm install --save-dev wrangler@4` and drop this note.

The URL it prints (`https://buddys-jobs.<your-subdomain>.workers.dev`) is what goes into the
module's **Server** box, and the join code into its **Board key** box. Nothing else is configured.

To check it is alive without a code — this is the one endpoint that answers unauthenticated, so that
"the server is down" and "my join code is wrong" cannot be mistaken for each other:

```bash
curl https://buddys-jobs.<your-subdomain>.workers.dev/v1/health
```

## The API

Every request but `/v1/health` carries `Authorization: Bearer <join code>`. `actor` is the character
acting, as `Name@World`.

| | | |
| --- | --- | --- |
| `GET` | `/v1/health` | Liveness. No auth. |
| `GET` | `/v1/jobs` | Every open and claimed job, plus 30 days of finished ones. |
| `POST` | `/v1/jobs` | `{ itemId, itemName, quantity, hq, reward, note, actor }` |
| `POST` | `/v1/jobs/:id/claim` | `{ actor }` — **409** if someone got there first, naming them. |
| `POST` | `/v1/jobs/:id/release` | `{ actor }` — the claimer or the poster. |
| `POST` | `/v1/jobs/:id/complete` | `{ actor, amount }` — `amount` defaults to whatever is left. |
| `POST` | `/v1/jobs/:id/cancel` | `{ actor }` — the poster only. |

### Two people claiming at once

Resolved by the database, not by the client:

```sql
UPDATE jobs SET state = 'claimed', claimed_by = ?
 WHERE id = ? AND board = ? AND state = 'open'
```

Both requests run it, exactly one changes a row, and the loser gets a 409 saying who beat them.
There is no read-then-write window to lose, which is the failure this shape exists to avoid.

### Partial deliveries

The normal case, not an edge case — "500 iron ore" is usually three people bringing what they could.
`complete` adds to a running total; anything short of the target puts the job back on the board for
the remainder, so the next person picks up where the last one left off.

## What it knows about you

The character name and world you act as, and the items you ask for or deliver. That is the whole
list. There is no login, nothing is linked to a Dalamud or Square Enix account, and the plugin sends
nothing at all until a board key is entered.
