# Buddy's Buddy

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV that acts as a
**host for lots of small plugins**. Instead of shipping a dozen tiny plugins, each feature is a
self-contained *module* inside one assembly, with its own tab, its own settings, and its own
`/bb <module>` command — enable or disable each one independently, no game restart.

## Modules

| Module | Command | What it does |
| --- | --- | --- |
| **Buddy's Stash** | `/bb stash` | One search box over everything you own, everywhere it is |
| **Buddy's Purse** | `/bb purse` | Totals your gil across character, retainers and free company |
| **Buddy's Bag** | `/bb bag` | Opens the chocobo saddlebag and moves every item out of it into your inventory |
| **Buddy's Chest** | `/bb chest` | Tallies the whole free company chest and withdraws every unit of a chosen item |
| **Buddy's Slaves** | `/bb slaves` | Tallies every retainer and withdraws a shopping list of items, top to bottom |
| **Buddy's Desynther** | `/bb desynth` | Desynthesises the game's whole list in one button and records the yields |
| **Buddy's Ledger** | `/bb ledger` | Records every trade, and what each player has given you in total |
| **Buddy's Jobs** | `/bb jobs` | A shared board of requests — post one, claim someone else's, mark it delivered |

### Buddy's Stash

Every other module already knows what is in one place. Buddy's Stash is the join: bags, crystals,
saddlebag, all five FC compartments, and every retainer, folded into one answer per item.

```
Titanium Ore     567   Chocobo 400 · Compartment 2 120 · Bags 47
```

Click a row for the full breakdown. `/bb stash <name>` prints the same thing to chat.

It reads **nothing** from the game itself — it only aggregates what the other modules have already
seen, so it works with any of them switched off.

**It is careful about what it does not know.** Only one retainer and one FC compartment can be read
at a time, so a complete picture is built up as you go. Counts that came off disk rather than from
this session are shown in amber, and the total's tooltip splits confirmed from remembered:

```
12 location(s) read this session, 5 remembered from an earlier one.
```

Those remembered snapshots live in the plugin's own folder (`sources.json`), not in the plugin
config — see [knowledge/verified.md](knowledge/verified.md) for why that distinction matters here.
**Forget everything remembered** under Settings clears them.

Compartments are keyed by index rather than by free company, so being in two FCs will blur their
*remembered* tallies together; whatever is open in front of you is always right.

### Buddy's Purse

Gil per location — character, each retainer, and the FC — with a total and an optional baseline to
measure earnings against.

A retainer you have never opened cannot be read, so it is **left out rather than counted as zero**.
The same amber marking applies to anything not read this session.

`/bb purse` reports the breakdown · `/bb purse baseline` resets the baseline to the current total.

### Buddy's Bag

`/bb bag` opens the saddlebag if it is closed, then empties it one item per tick until the
saddlebag is empty or your inventory fills up. `/bb bag stop` aborts, `/bb bag status` reports.

It stops on its own when the saddlebag empties, your inventory fills, the saddlebag window closes,
you log out, or moves stop making progress. Items only ever land in **empty** inventory slots —
moving onto an occupied slot swaps the two items, which would push your inventory into the
saddlebag — so partial stacks are left unmerged rather than risking that.

Tunable under Settings: delay between moves (default 200 ms — the game refuses moves issued too
quickly), whether to include the premium saddlebag pages, and whether to report to chat.

### Buddy's Chest

Tallies the free company chest and takes **all** of one item at a time.

The chest loads one compartment at a time — open a compartment and the rest go into a loading
state, so nothing can read the whole chest in one pass. Buddy's Chest therefore *accumulates*: it
re-reads whichever compartment is loaded and merges the results, giving a single combined count per
item across all five compartments plus crystals. It always shows how many compartments it has
actually seen, so a partial tally is never mistaken for a complete one:

```
Scanned 3 of 6 compartments — this tally is INCOMPLETE.
Not yet seen: Compartment 4, Compartment 5, Crystals
```

Click through the remaining tabs and the count completes itself. HQ is tracked as a separate item.

The list is ordered by compartment, matching the chest's own tabs.

Search it, then **Take all** on a row to pull every stack of that item into free inventory slots,
working through compartments in order — 1 to 5, then crystals. **Take all** is only enabled when the
chest is already open and targeted; the module never opens the chest itself.

Each stack goes through take → confirm → verify: taking from a company chest raises the game's
quantity prompt, which already defaults to the full stack, so it is confirmed as-is. A stack is only
counted once its units are **confirmed present in your inventory** — the move and the prompt both
report nothing useful, so inventory count is the only real proof of delivery. Anything in a
compartment that is not open cannot be moved, and the shortfall is reported rather than dropped
silently.

`/bb chest` reports the tally · `/bb chest find <name>` looks an item up · `/bb chest rescan`
clears it · `/bb chest stop` aborts a withdrawal.

### Buddy's Slaves

The same idea for retainers, with **Take X** instead of take-all.

Only the retainer open at a bell is readable, so the tally accumulates per retainer as you visit
them — a table shows every retainer in bell order and when each was last scanned, so you can see at
a glance what is still missing.

**Take X** opens a prompt for an amount (with an **All** shortcut), then withdraws exactly that many
units, taking from retainers **top to bottom** in bell order. Partial stacks are confirmed at the
exact remainder, so a request is never overshot.

A request outlives a single retainer: it takes what it can from the open one, then waits and tells
you where the rest is —

```
High Mythrite Ingot: 220/500 — next in Chocobo
```

— and resumes on its own as soon as you open that retainer. Every stack is confirmed present in
your inventory before it counts toward the total.

#### Queue mode — the shopping list

Tick **Queue mode** and the row buttons become **Add**, building a list of as many items as you
like. **Run list** then works the whole list at once.

The queue drains **greedily per retainer**: everything the open retainer can supply is taken —
across every item on the list — before anything waits. Ask for 20 items, and if the first retainer
fully satisfies 8 of them and runs dry on 2, it takes all 8 plus whatever it has of the other 2,
then pauses and names where the rest is. Open that retainer and it resumes automatically. Each
retainer is visited once, not once per item.

The list shows per-item progress and state:

```
Shopping list (8/20 complete)
High Mythrite Ingot   340 / 500   Chocobo
Titanium Ore          200 / 200   done
Fire Crystal            0 / 999   taking…
```

`Run list` · `Stop` · `Clear list`, and **x** removes a single row. Items can be added while a run
is in progress.

`/bb slaves` reports the tally and list · `/bb slaves find <name>` looks an item up ·
`/bb slaves run` · `/bb slaves stop` · `/bb slaves clear` · `/bb slaves rescan`.

### Buddy's Desynther

One table of everything you own that can be desynthesised, and a button per pile. No queue, no
filters, and the game's Desynthesis window does not need to be open.

**There is nothing to configure.** `Item.Desynth > 0` is the game's own answer to "can this be
desynthesised" and the required skill is the item's own level, so eligibility is never a decision you
have to make.

```
Item                       Have  Where     Skill    Worth it?
Bronze Cross-pein Hammer      1  Armoury    32/8       +310
Classical Battleaxe           1  Armoury    32/580     +1,940
```

The **Skill** column is your desynthesis level against the item's level. It is a **hint about yield,
not a restriction** — anything desynthesisable can be desynthesised at any skill level; being
under-levelled just means poorer returns and no skill gain. Nothing is ever excluded because of it.
That skill is per crafting class and separate from your crafting levels: which class *made* an item
decides which of the eight applies, so being level 70 Blacksmith does not mean your Blacksmith
*desynthesis* is 70.

(If your Desynthesis window shows a red `skill/required` column, that is SimpleTweaks' *Extended
Desynthesis Window*, not the game refusing you.)

Bags and the armoury chest, never equipped gear. **Desynthesis cannot be undone**, so each button
asks twice — the first click arms it for four seconds, the second does it. The list above the button
is exactly what will be destroyed.

**Desynthesis unlocks per crafting class at level 30**, and which class *made* an item decides which
of yours applies. Items whose class you have not levelled that far are greyed out with the reason —
`Carpenter is level 12 — desynthesis unlocks at 30` — and left out of runs. That comes from
`PlayerState.GetClassJobLevel`, so it is the game's own answer rather than a guess.

If the game declines something anyway, the run does not stop: it skips that item, remembers it for
the session and carries on, reporting both counts — `Desynthesised 24 item(s), skipped 3 the game
would not do`. **Retry them** clears that memory.

`/bb desynth bags` · `/bb desynth armoury` · `/bb desynth all` · `/bb desynth stop`

#### The ledger

Every desynthesis is measured against your inventory immediately before and after — bags **and the
crystals container**, since shards and crystals do not live in a bag slot and are often most of the
yield. The game also reports its own yield through `AgentSalvage.DesynthResults`; Buddy's Desynther
records the **inventory**, cross-checks it against the game's claim, and counts the disagreements
rather than quietly picking one. That cross-check is what caught the missing crystals.

Yields are random, so one run says nothing and a hundred say a great deal. The run count is always
shown, and once there is enough of it the **Worth it?** column fills in: expected yield minus what a
vendor would pay for the item itself, from *your* ledger.
It uses vendor prices — a floor, not a valuation. Real market prices would come from a market plugin
over IPC; the ledger is already in the right shape for it.

The ledger lives in the plugin's own folder (`desynth-ledger.json`), not in the plugin config, and it
is the only record of what hundreds of destroyed items actually produced.

`/bb desynth` reports state · `/bb desynth probe` writes a diagnostic report (see below).

#### If something misbehaves

Three details of the desynthesis flow could not be settled from the game's assemblies — which
callback dismisses the dialog, how the results window closes, and what two of `SalvageItem`'s
arguments want. The current answers are educated guesses, and they are guesses that **cannot strand
the runner**: every step is judged by whether the source stack actually got smaller, not by a return
code, and each has a timeout that stops with a plain reason.

`/bb desynth probe` writes what the game says right now — agent state, addon state, the last yield,
your desynthesis levels — to `pluginConfigs/BuddysBuddy/desynth-probe-<timestamp>.txt`. Run it at
each stage of a real desynthesis and those three questions answer themselves. Details in
[knowledge/verified.md](knowledge/verified.md).

### Buddy's Ledger

Every trade you make, and — the actual point — **what each person has handed over in total, across
every trade you have ever done with them.** One trade is a receipt; a hundred is a picture of who
actually supplies you.

Three views:

- **Players** — one row per person: how many trades, items in, items out, net gil, when you last
  dealt with them. Pick a row for the full item-by-item breakdown both ways.
- **History** — every trade, newest first, with a note you can attach after the fact.
- **Items** — every item that has ever crossed the table, both directions, everyone together.

A player is identified by **name *and* world**, not name alone — the same name exists on every other
world, and cross-world trades are ordinary.

#### How a trade is read

Out of the game's own trade window, through `InventoryManager` — `TradeItemsLocal`,
`TradeItemsRemote`, `TradePartnerNameString` and the `TradeState` machine. No chat parsing, so it is
not affected by your chat settings or your language.

**What gets written down is the change in your own bags**, measured either side of the trade. The
window says what was *offered*; only your bags show what actually *arrived*, so the window is used to
*check* the record rather than to be it. Every row's numbers come from the same place; the marker
only says how well corroborated that row is:

| Row | Means |
| --- | --- |
| *(nothing)* | The window and your bags agreed. |
| `!` | They disagreed. Your bags were recorded, and the log holds both versions. |
| `~` | The window was never seen with both sides locked in, so there was nothing to check against. The amounts are no less accurate. |

Same arrangement as the desynthesis ledger, for the same reason: a quietly wrong ledger is worse than
no ledger. Only trades made while the plugin was running can be counted — there is no trade history
anywhere in the game to read back, which is also why the ledger saves the moment a trade lands rather
than on a timer (measured at 66 ms after the game's own "Trade complete").

`trade-ledger.json` in the plugin's own folder · **Export to CSV** for a spreadsheet ·
`/bb ledger <name>` for one person · `/bb ledger probe` writes a diagnostic report.

### Buddy's Jobs

A shared board: post *"I need 500 iron ore"*, and anyone with the same board key can see it, take it,
and mark it delivered. Built for the case where several people are gathering or crafting for the same
person and nobody can tell what is already covered.

**This is the one module that talks to a server**, because it has to. Dalamud's IPC reaches other
plugins on the same client, never other players — so two people's plugins genuinely cannot see each
other without something in the middle. [`server/`](server/) is that something: a Cloudflare Worker
and a D1 database you deploy once, free, in about five minutes. Its README has the steps.

It is **off by default** and sends nothing at all until you enter a board key.

- **Partial deliveries are the normal case.** Anything short of the full amount adds to a running
  total and puts the remainder back on the board, so three people can fill one order of 500 between
  them and the next person sees what is actually left.
- **Two people claiming at once is settled by the database**, not the client — one conditional
  `UPDATE`, one winner, and the loser is told who beat them rather than silently overwriting.
- **"You have"** reads the same `IItemSource` spine as Buddy's Stash, so a job shows how many you
  already own before you decide whether to take it.
- A board key shorter than 16 characters is refused by the server. It is the only thing standing
  between a stranger and the board.

What leaves your machine: the character name and world you act as, and the items you ask for or
deliver. Nothing else, and only to the server you configured.

`/bb jobs` lists what is open · `/bb jobs refresh` · `/bb jobs test` checks the connection.

**Auto-deliver** on a job claimed by you sends materials to its requester, using Friends List estate
teleportation or a saved Lifestream housing alias. It handles repeated trades and records verified
deliveries on the board. See [the delivery guide](docs/DELIVERY.md) for setup and testing.

**Waiting for delivery** on a job you posted accepts the named claimant's materials and pays the
numeric Reward as a prorated total. Payments respect the 1,000,000-gil trade cap, including gil-only
follow-up trades when needed. Both players must use the updated build for automatic coordination.

## Installing in game

Dalamud settings → **Experimental** → *Custom Plugin Repositories*:

```
https://raw.githubusercontent.com/NoctusDev/DalamudPlugins/main/pluginmaster.json
```

That one URL serves **every** NoctusDev plugin, so it only ever needs adding once — anything
published later shows up on its own. It is generated from this repo's own
[`repo.json`](repo.json), which stays the source of truth for this plugin's entry.

## Commands

| Command | Effect |
| --- | --- |
| `/bb` | Toggle the main window |
| `/bb settings` | Open settings |
| `/bb help` | List available module commands |
| `/bb <module> [args]` | Send arguments to a module |

`/buddysbuddy` is an alias for `/bb`.

## Other plugins

[MarketTraveler](https://github.com/NoctusDev/MarketTraveler) — buys from the market board across
worlds. The same repository URL above serves it.

## Building it yourself

See [`docs/DEVELOPING.md`](docs/DEVELOPING.md) — building, loading it as a dev plugin, and how to
write a module of your own.

## License

MIT — see [LICENSE](LICENSE).
