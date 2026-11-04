# Buddy's Buddy — verified notes

What is **confirmed working**, what is **confirmed broken**, and how each was checked. Append a
dated entry whenever something is proven either way — the point is to never rediscover the same
fact twice. Disproven entries are worth as much as confirmed ones.

Machine-wide and Dalamud-wide facts (the API-level trap, the `%AppData%` decoy install, the API 15
migration table, Actions being locked) live in [`dalamud.md`](dalamud.md). **Read that first.**

---

## Paths

| Path | Status | Notes |
| --- | --- | --- |
| `d:\Multibox\Glumishxiv3\addon\Hooks\dev` | ✅ **the live Dalamud** | Build against this; see the warning above |
| `%AppData%\XIVLauncher\addon\Hooks\dev` | ❌ stale decoy | The SDK's default. Five months old, wrong API level |
| `d:\Multibox\Glumishxiv3\dalamud.log` | ✅ the live log | Where plugin load errors actually appear |
| `d:\Multibox\Glumishxiv3\pluginConfigs\BuddysBuddy.json` | ✅ live plugin config | |
| `D:\CLAUDE\Buddy's Buddy\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll` | ✅ **the dev-plugin path** (2026-08-23) | Build output; `BuddysBuddy.json` sits beside it |
| `D:\Buddy's Buddy\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll` | ❌ **gone — the repo moved into `D:\CLAUDE\`** | Was correct until 2026-08-23. The old folder is now `D:\Buddy's Buddy.rar` |
| `…\bin\Debug` (either root) | ❌ rejected by Dalamud | Folder path → *"not a valid path to a potential Dev Plugin"*. Dalamud wants the **DLL** |
| `%AppData%\XIVLauncher\devPlugins\` | ❌ deprecated | The folder's own `DONT_USE_THIS_FOLDER.txt` says to use Dev Plugin Locations instead |
| `%AppData%\XIVLauncher\addon\Hooks\dev\` | ✅ Dalamud dev assemblies | What `Dalamud.NET.Sdk` resolves against; build log prints `Dalamud.NET.Sdk: root at …` |
| `src/BuddysBuddy/bin/Debug/` | ✅ flat output | The Dalamud SDK does **not** nest under a `net10.0-windows/` TFM folder |
| `src/BuddysBuddy/bin/Release/BuddysBuddy/latest.zip` | ✅ packaged output | Release only; produced by DalamudPackager |

---

## Saddlebag API (Buddy's Bag)

Confirmed by reflecting over the installed `FFXIVClientStructs.dll` — not guessed. Re-probe rather
than assume if a Dalamud update changes anything.

| Symbol | Value / signature |
| --- | --- |
| `InventoryType.SaddleBag1` / `SaddleBag2` | `4000` / `4001` |
| `InventoryType.PremiumSaddleBag1` / `2` | `4100` / `4101` — only loaded with a Companion subscription |
| `InventoryType.Inventory1..4` | `0..3` — the player's four bag pages |
| `InventoryManager.Instance()` | may return null; always check |
| `InventoryManager.GetInventoryContainer(InventoryType)` | `InventoryContainer*` |
| `InventoryManager.MoveItemSlot(src, ushort srcSlot, dst, ushort dstSlot, bool)` | returns `int`, `0` = success |
| `InventoryContainer` | `Items`, `Type`, `Size`, `IsLoaded`, `GetInventorySlot(int)` |
| `InventoryItem` | `ItemId` (`0` = empty slot), `Quantity`, `Flags`, `Slot` |
| `AgentId.InventoryBuddy` | `300` |
| Saddlebag addon name | **`InventoryBuddy`** (type `AddonInventoryBuddy`) |

**The saddlebag window must be open** for reads and moves to work — with it closed the containers
report unloaded and moves silently fail. Gate everything on the addon being visible.

**Moves apply asynchronously.** The container still reports the old contents on the same frame, so
a tight loop reads stale data and issues duplicate moves. Do one move per framework tick with a
delay (default 200 ms) and judge progress by watching the remaining count fall — *not* by the
return code, so a silently refused move is still detected.

**Only ever move into an empty destination slot.** `MoveItemSlot` onto an occupied slot *swaps* the
two items, which would push an inventory item into the saddlebag — the exact opposite of the goal.
The cost is that partial stacks do not merge.

### Addon lookup — use the wrapper, not a cast

```csharp
// WRONG — CS0030, AtkUnitBasePtr is a Dalamud wrapper, not a raw pointer
var addon = (AtkUnitBase*)GameGui.GetAddonByName("InventoryBuddy", 1);

// RIGHT — the wrapper exposes what is needed, and needs no unsafe block
var addon = GameGui.GetAddonByName("InventoryBuddy", 1);
return !addon.IsNull && addon.IsReady && addon.IsVisible;
```

### Item names

`DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(id)?.Name` returns a `ReadOnlySeString`;
call `.ToString()`. `Lumina.Excel.Sheets.Item` exposes `Name`, `RowId`, `StackSize` as properties.

---

## Free company chest API (Buddy's Chest)

Confirmed by reflecting over the live `FFXIVClientStructs.dll`.

| Symbol | Value / signature |
| --- | --- |
| `InventoryType.FreeCompanyPage1..5` | `20000`–`20004` |
| `InventoryType.FreeCompanyCrystals` | `22001` |
| `InventoryType.FreeCompanyGil` | `22000` (use `InventoryManager.GetFreeCompanyGil()`) |
| `AgentId.FreeCompanyChest` | `85` |
| `AgentFreeCompanyChest.Instance()` | |
| `AgentFreeCompanyChest.MoveItemInChest(InventoryType src, uint srcSlot, InventoryType dst, uint dstSlot)` | returns void |
| Addon name | **`FreeCompanyChest`** |
| Quantity prompt addon | `InputNumeric` |
| `AtkUnitBase.FireCallbackInt(int)` | for tab switching |

Addon names cross-checked by grepping the installed plugin DLLs: `FreeCompanyChest` appears in
AutoRetainer, CriticalCommonLib and Underlings; `InputNumeric` in Artisan and ECommons.

**Compartments load one at a time.** The server only sends the compartment being viewed; the others
report `IsLoaded == false` — the "loading" state visible in the UI. A single pass can therefore
never see the whole chest. `ChestTally` keeps a per-compartment snapshot, refreshes whichever one is
loaded, and reports how many have been seen so a partial tally is *visibly* partial.

**A move alone does NOT withdraw anything — the quantity prompt must be confirmed.** Tested in game
2026-07-28. `MoveItemInChest` takes no quantity argument, and I wrongly assumed that meant it moved
whole stacks directly. It does not: the game raises the `InputNumeric` prompt and nothing leaves the
chest until that prompt is confirmed. The first build appeared to do nothing for exactly this reason.

The prompt **already defaults to the full stack**, so confirming is all that is needed:

```csharp
var value = stackalloc AtkValue[1];
value->Type = AtkValueType.Int;      // note: AtkValueType, not ValueType
value->Int = quantity;
addon->FireCallback(1, value, true); // close: true — the OK button's path
```

**Confirm each stack actually arrived.** `InventoryManager.GetInventoryItemCount(itemId, isHq,
checkEquipped: false, checkArmory: false, 0)` before and after gives proof of delivery. Neither the
move nor the callback returns anything meaningful, so inventory count is the only real signal — the
withdrawal is a take → confirm → verify cycle per stack, and a stack is only counted once its units
are present in the player's bags.

**Withdrawal only reaches loaded compartments**, and works through them in tab order: pages 1-5,
then crystals. Stacks in unopened compartments cannot be moved; `UnreachableQuantity` reports them
so the shortfall is explained rather than silent.

**The chest is never opened programmatically.** Withdrawal is gated on `IsChestReady()` — window
visible *and* agent active — so it only ever acts on a chest the player already opened and targeted.

Compartment tab switching was removed: compartments load correctly when clicked by hand, so the
unverified `FireCallbackInt(tabIndex)` guess was not worth keeping.

---

## Retainer API (Buddy's Slaves)

Confirmed by reflecting over the live `FFXIVClientStructs.dll`.

| Symbol | Value / signature |
| --- | --- |
| `InventoryType.RetainerPage1..7` | `10000`–`10006` |
| `InventoryType.RetainerCrystals` | `12001` |
| `InventoryType.RetainerGil` / `RetainerMarket` / `RetainerEquippedItems` | `12000` / `12002` / `11000` |
| `RetainerManager.Instance()->IsReady` | guard before reading anything |
| `RetainerManager.GetRetainerCount()` | `byte` |
| `RetainerManager.GetRetainerBySortedIndex(uint)` | **bell display order — "top to bottom"** |
| `RetainerManager.GetActiveRetainer()` | the retainer whose inventory is loaded |
| `Retainer.NameString` | `string` property (the raw field is `_name`, a `FixedSizeArray32`) |
| `Retainer.RetainerId` / `ItemCount` / `Gil` | |
| Addon names | **`InventoryRetainer`** and **`InventoryRetainerLarge`** (expanded view) |

**One retainer is readable at a time** — only the retainer open at a bell has loaded containers, so
the tally accumulates per retainer across visits, exactly like the FC chest compartments. Unlike the
chest, *all* of a retainer's pages load together, so one visit captures that retainer completely.

⚠️ **`GetActiveRetainer()` reports a retainer while the bell list is merely being browsed**, not
only while an inventory is open. Anything keyed on "the active retainer changed" will therefore fire
during ordinary list navigation — dropping cached snapshots there wiped every previously scanned
retainer. Gate reads on `IsInventoryOpen()`, and let a re-read replace a snapshot rather than
invalidating ahead of time.

**A withdrawal request outlives a single retainer.** "Take 500" pulls what it can from the open
retainer, then *pauses* rather than failing, and resumes automatically when the next retainer is
opened — draining them in bell order via `GetRetainerBySortedIndex`.

**Partial amounts come from the quantity prompt**, confirmed at exactly the remainder rather than
the stack size, so a request is never overshot. The same `QuantityPrompt` helper serves the chest
(which always confirms the full stack) and retainers (which may confirm less).

⚠️ Retainer withdrawal uses `InventoryManager.MoveItemSlot`, not an agent call. Whether it raises
the quantity prompt the same way the FC chest does is **assumed, not yet confirmed in game**. If it
moves whole stacks without prompting, a partial request could overshoot by up to one stack — the
verify step counts what actually arrived, so the report stays truthful either way.

---

## Item sources — the shared spine

[`IItemSource`](../src/BuddysBuddy/Game/ItemSource.cs) is the one abstraction the modules always
implied: a labelled bag of `ItemKey → quantity`, plus gil, plus **when it was last read**.
`ItemSourceRegistry` collects providers; Buddy's Stash and Buddy's Purse are pure consumers of it
and touch no game API of their own.

Facts worth keeping:

- **Live beats stored, then most recent wins.** The same location can be reported both by a live
  tally and by the snapshot restored from disk. The registry keys on `Id` and prefers `IsLive`, then
  the later `LastSeen`. Without the tie-break, an empty freshly-created source can wipe out a good
  stored reading purely by provider ordering.
- **Character sources are keyed by content id** (`char:{contentId:X}:bags`). On this multibox setup
  several characters share one store file, and unkeyed ids would have them overwrite each other.
- **FC compartments are keyed by compartment index only** (`fc:0`…`fc:5`). The game offers no cheap
  free company handle, so belonging to two FCs blurs their *stored* tallies together. The live
  reading is always right; this is a known, accepted limit.
- **Gil is attributed to exactly one source per owner** — the character's bags, each retainer, and FC
  compartment 1 — so summing gil across sources never double-counts.
- Player gil is read as **item id 1 out of the `Currency` container**, not through
  `GetInventoryItemCount` — that searches bag slots, and gil does not occupy one. ⚠️ Not yet
  confirmed in game; if the character line in Buddy's Purse reads 0, this is the thing to check.
- `Retainer.Gil` is readable from the bell roster, but is only recorded when that retainer's
  inventory is actually read. A retainer never visited is **absent**, not zero — an unknown is not
  the same as nothing.

### The store is its own file, on purpose

`pluginConfigs/BuddysBuddy/sources.json` (the plugin's `ConfigDirectory`), **not** the plugin config.
It grows with every retainer and compartment ever seen, and Dalamud deserialises the plugin config
inside the plugin constructor — an unreadable config there kills the whole plugin, which already
happened once (see the log below). A corrupt store costs the store and nothing else; `Load` catches
and starts empty.

`SourceStore.Serialize`/`Deserialize` are deliberately pure and free of any Dalamud service so
`scripts/verify-config.cs` can round-trip them outside the game — including the check that a
restored source reports `IsLive == false`.

---

## Trade API (Buddy's Ledger)

`[metadata]` — read off the live `FFXIVClientStructs.dll` (7.55.1.8875) with `MetadataLoadContext`
on **2026-08-23**. Signatures and visibility are exact; behaviour is marked where it is not.

The whole trade is on **`InventoryManager`**, which needs no addon open and no pointer into the UI.

| Symbol | Signature / value | Public? |
| --- | --- | --- |
| `InventoryManager.TradeItemsLocal` / `TradeItemsRemote` | `Span<InventoryItem>` — **6 slots each** | ✅ |
| `InventoryManager.TradeLocalState` / `TradeRemoteState` | `TradeState` | ✅ |
| `InventoryManager.TradePartnerNameString` | `string` (raw field `_tradePartnerName`, `FixedSizeArray32<byte>`) | ✅ |
| `InventoryManager.TradePartnerEntityId` | `uint` | ✅ |
| `InventoryManager.TradeIsSyncPending` / `TradeWarnIfMovedTooFar` | `bool` | ✅ |
| `InventoryManager.TradeLockedIn` | `bool` | ❌ **private — CS0122** |
| `InventoryManager.SendTradeRequest(uint entityId)` / `SetTradeGilAmount(uint)` / `RefuseTrade()` | | ✅ |
| `TradeState` | `NotTrading=1, TradeRequestPending=2, SelectingTradeGoods=3, LockedIn=4, WaitingForConfirmation=5, Confirmed=6` | |
| `AgentId.Trade` | **36** (`NpcTrade` 38, `TradeMultiple` 149) | |
| Addon name | **`Trade`** (type `AddonTrade`) | |

Two **UI arrays** exist alongside it, both `Instance()` statics, valid only while the addon is up:

| Symbol | Notes |
| --- | --- |
| `TradeNumberArray.SendGilCount` / `ReceiveGilCount` | `uint` — **the gil** |
| `TradeNumberArray.SelfState` / `OtherState` / `IsLockedIn` | the readable stand-in for the private `TradeLockedIn` |
| `TradeNumberArray.ItemsGive` / `ItemsReviece` *(sic)* | `Span<TradeItem>` — **`IconId`, not `ItemId`**. Useless for identifying an item |
| `TradeStringArray.OwnName` / `TradePartnerName` | `CStringPointer` |

**`TradeNumberArray.TradeItem` carries an icon id, not an item id.** That is the trap: it looks like
the obvious place to read a trade from and cannot answer what the item *is*.
`InventoryManager.TradeItemsLocal/Remote` are real `InventoryItem`s — `ItemId`, `Quantity`, `Flags`
(HQ) — and are the only readable source of item identity.

**There is no `InventoryType` for trade.** Trade contents are not a container; the full enum was
dumped and checked.

### ❌ No installed plugin on this machine reads the trade window

`[observed]` **2026-08-23.** Grepping all 83 installed plugin DLLs for `AddonTrade`, `TradeState`,
`TradeNumberArray` and `TradePartnerNameString` returned **nothing** — including TrackyTrack, which
covers desynthesis and coffers but not trades. The method was sanity-checked first against
`BuddysBuddy.dll`, which correctly returned `AgentSalvage`, `InventoryManager` and `SalvageDialog`,
so the empty result is real and not a broken grep.

So the corroboration used for `FreeCompanyChest`, `InputNumeric` and `SalvageDialog` is **not
available here** — the metadata is the only source. Note that .NET string *literals* are UTF-16 in
the `#US` heap and will not match an ASCII grep; type and member names are UTF-8 in `#Strings` and
will.

### ✅ Settled in game — three live trades, 2026-08-23

`[in-game]` `/bb ledger probe` run three times across two real trades on this machine. Raw dumps in
`pluginConfigs/BuddysBuddy/trade-probe-20260823-*.txt`.

**1. ⚠ Gil in the trade slots is ASYMMETRIC — corrected 2026-08-23 by a later trade.**

*Their* gil is remote slot `[5]` as item id 1, agreeing with `ReceiveGilCount` exactly, and the slot
is present with quantity 0 when none is offered:

```
TradeItemsRemote:  [5] 1 x1000000        ReceiveGilCount : 1000000
TradeItemsRemote:  [5] 1 x0              ReceiveGilCount : 0
```

**Your own gil is not in your slots at all.** With 69 gil on the table from this side:

```
TradeItemsLocal:   [5] empty             SendGilCount    : 69
```

The first two probes only ever *received* gil, so "gil is trade slot 5" was recorded as a general
rule from one direction of evidence. It is half true. **Reading the slots alone would record every
gil payment you make as zero.** The two-source design is what caught it — the plugin logged it
itself on the first trade that paid any:

```
[INF] [BuddysBuddy] Trade gil (given): 69 came from the trade array only — no gil in the item slots.
```

So: prefer the slot where it exists (it outlives the addon), fall back to the array, and never
assume one covers both directions.

**2. ⚠ `TradeLocalState` is never observed as `Confirmed`.** The decisive dump:

```
TradeLocalState  : WaitingForConfirmation (5)
TradeRemoteState : Confirmed (6)
```

The trade then completed. **A `both == Confirmed` test never fires**, which is exactly what happened:
the first build marked both real trades `Reconstructed` because it was waiting for a state the local
side does not reach. Use **both `>= LockedIn`** as the "the deal is settled" line instead.

**3. The partner's half of the window carries no container, but it does carry flags.**

```
[0] 7018 x115  container=Invalid slot=0  flags=None            ← an NQ item
[0] 5074 x329  container=Invalid slot=0  flags=HighQuality     ← an HQ item, a later trade
```

Your own side reports real containers (`container=Inventory1 slot=33`); the partner's is always
`Invalid`, which makes sense — their items are not in any container of yours.

❌ **Corrected.** This entry first read *"there is no usable high-quality flag on the remote side"*,
inferred from `flags=None` across two trades that, as it turned out, simply contained no HQ items.
The first trade that did contain one reported `flags=HighQuality` correctly. **Absence of a flag was
read as absence of the feature** — the same shape of error as the desynthesis item-level rule, on a
smaller scale.

The design conclusion it was used to justify — inventory as the record, window as the cross-check —
still holds, but on the honest reason: the window says what was *offered*, and only your bags show
what actually *arrived*. The wrong reason was written into a doc comment as justification and has
been corrected there too.

**4. `TradeNumberArray.SelfState` / `OtherState` are not `TradeState`.** They ran 2/3 while
`TradeState` read 3/4, and 3/3 while it read 5/6 — a different, coarser scale. Do not compare them.
`IsLockedIn` did flip true when both sides locked in.

**5. Two further independent signals exist**, neither needed but both worth knowing:
- **`ConditionFlag.TradeOpen`** — visible in `dalamud.log` as BossModReborn's
  `Condition change: TradeOpen=True/False`. A Dalamud-level "is a trade window open" with no
  pointers involved.
- **"Trade complete." is a toast**, not only a chat line — Questionable logged
  `Normal Toast: Trade complete.` A third record beyond the window and the inventory.

**6. Timing: the inventory settles fast.** The record was saved **66 ms** after the "Trade complete."
toast (`20:46:29.105` → `20:46:29.171`), so three consecutive matching reads is plenty and the 4 s
settle cap is a generous backstop rather than a limit anyone will hit.

Both trades recorded the correct contents — 115x7018, 2x5095, 4x19938, 3x5061, 1x8148 + 1,000,000
gil, and 4,995x5116 across five stacks — proving the inventory-diff path independently of the window.

### ✅ The two-way trade — everything else confirmed, 2026-08-23 21:05

One trade closed every remaining gap at once. Gave 4,995x5116 **and** 69 gil, received 329x5074 HQ
and 273x5059 HQ. Recorded as:

```
EVIDENCE=Confirmed   gil given=69  gil received=0
GAVE:      item 5116 x4995 hq=False
RECEIVED:  item 5074 x329 hq=True
           item 5059 x273 hq=True
```

| Path | Result |
| --- | --- |
| `TradeEvidence.Confirmed` | ✅ The both-`LockedIn` fix works — window and bags agreed, no marker. |
| **Giving** items (negative deltas) | ✅ 4,995 units across five 999-stacks, recorded as one line. |
| **Giving gil** | ✅ 69, and only because the array was read — see the asymmetry above. |
| **HQ items** | ✅ Both received stacks recorded `hq=true`, off the bag diff. |
| Folding quality in `Agrees` | ✅ Vindicated. The window and the diff describe stacks differently; comparing by item id and total is what let a mixed HQ/NQ trade come out `Confirmed`. |

Still unseen, and neither can lose a trade:

- **`TradeEvidence.Disputed`** — no evidence it can be produced at all. That is the intended outcome.
- **Trading the same item in both directions at once** — the known limit of a net diff; it would
  cancel out. Rare enough to accept, and it would show as a dispute if the window disagreed.

### Design notes that came out of the above

- **The trade window empties on completion**, so nothing is read at commit time. The last reading
  taken at the highest combined `TradeState` rank is kept, guarded so an *empty* reading never
  replaces a non-empty one — that guard is what stops the window clearing erasing the trade.
- **State returning to `NotTrading` proves the window closed, not that the server moved anything.**
  The commit waits for the inventory diff to be identical three ticks running, with a 4 s cap.
  Same lesson as `FireCallbackInt` returning true without closing anything.
- **The window is preferred over the inventory when both sides confirmed** — it is the deal as
  struck, HQ flags included, where a diff only shows net movement and would fold a stack you gave
  into one you already had. The inventory still decides whether the trade happened at all.
- Ticked **every framework update**, not on an interval: the confirmed-to-closed window is a handful
  of frames wide, and it is a few pointer reads until a trade is actually open.
- `IObjectTable.SearchByEntityId(uint)` → `IPlayerCharacter.HomeWorld.ValueNullable?.Name` resolves
  the partner's world. Resolve it early — it is only answerable while they are loaded. A partner is
  keyed `Name@World`; a bare name is not an identity.

---

## Cross-player communication (Buddy's Jobs)

`[metadata]` **2026-08-23.** **Dalamud IPC is same-client only.** It reaches other plugins in the
same game process and nothing else, so no arrangement of `ICallGateProvider` can let two players'
plugins see each other. Anything shared between players needs a server. This is a premise worth
stating rather than rediscovering: several plausible-sounding designs die on it immediately.

Precedent on this machine: Mare-style sync plugins (`LightlessSync`) and `SonarPlugin` both run
central servers for exactly this reason.

`server/` is a Cloudflare Worker plus D1. Decisions worth keeping:

- **A board is a shared secret and nothing else** — no accounts. The server stores only the join
  code's SHA-256, so the database cannot be used to join a board.
- **Claim races are resolved by a conditional `UPDATE`** (`WHERE state = 'open'`), not by the
  client. Exactly one caller gets `changes === 1`; the loser gets a 409 naming the winner. No
  read-then-write window exists to lose.
- **Partial delivery is the normal case**, not an edge case: anything short of the target increments
  a running total and returns the job to `open` for the remainder.
- `/v1/health` answers **unauthenticated** on purpose, so "the server is down" and "the board key is
  wrong" cannot be mistaken for each other — the MarketTraveler lesson about probing whether a call
  actually answers.

### ⚠ `[external]` Wrangler 4 needs Node ≥ 22; this machine has **20.12.2** — but wrangler 3 is fine

`[observed]` **2026-08-23**, on this PC. `npx wrangler@latest` installs but warns
`EBADENGINE required: { node: '>=22.0.0' }`.

❌ **"Deploying the worker needs a Node upgrade first" was wrong.** That was written from the
`EBADENGINE` warning alone, without checking whether an earlier wrangler would do the job.
**`npx wrangler@3` runs on Node 20** — 3.114.17 verified, and `wrangler deploy --dry-run` bundles
the worker (9.17 KiB) and resolves the D1 binding with no complaints. So wrangler is pinned to
`^3.114.17` in `server/package.json` and no Node change is needed.

The shape of the mistake: a tool's *latest* version refusing to run is not the same as the *task*
being blocked, and one command settled it. Re-check the installed Node before trusting any of this.

### ✅ Deployed, 2026-08-24 — and three traps on the way

`[external]` Account **Noctusdev@gmail.com**, subdomain `noctusdev`, worker live at
**`https://buddys-jobs.noctusdev.workers.dev`**, D1 `buddys-jobs` = `13568d51-68dd-4742-a09b-d5a07e6c5d6a`
(region WNAM). 14/14 live end-to-end checks pass against it. All of this is account state and rots.

**1. ⚠ Cloudflare blocks generic scripting User-Agents with `error code: 1010`.** This is the one
worth remembering, because it makes a working server look broken:

| User-Agent | Result |
| --- | --- |
| `Python-urllib/3.13` | **403, `error code: 1010`** |
| `BuddysBuddy/1.0` (what the plugin sends) | 200 |
| `curl/8.0`, `Mozilla/5.0`, none at all | 200 |

The plugin is unaffected — `JobBoardClient` sets its own product header, which is exactly why it
passes. But **any diagnostic script must set a User-Agent** or it will report a dead board. A test
harness silently testing the bot-protection rules instead of the API is a very convincing wrong
answer.

**2. A new `workers.dev` subdomain has no TLS certificate for a few minutes.** The symptom is not an
HTTP error — nothing answers at all:

```
curl: (35) OpenSSL/1.1.1t: ... sslv3 alert handshake failure
SSL alert number 40
```

DNS resolves fine and other hosts work, so it reads like a broken deploy. `openssl s_client
-connect <host>:443 -servername <host>` shows the handshake dying with 7 bytes read and no cipher,
which is the tell: the server has no certificate for that SNI **yet**. It cleared within a minute.
Wait and retry rather than debugging the worker.

**3. The onboarding URL wrangler prints 404s.** `wrangler deploy` fails on a fresh account with
*"You need to register a workers.dev subdomain"* and links to `/workers/onboarding`, which no longer
exists in the current dashboard. There is nothing to choose: **opening Workers & Pages once creates
the subdomain automatically**, and Cloudflare's own API says so where wrangler does not —

```
GET /accounts/{id}/workers/subdomain
→ 10007: "Opening the Workers & Pages landing page for the first time will create one automatically."
```

Asking the API beat both the CLI's advice and guessing at dashboard menus.

It does not block development: `server/test/board.test.js` runs the real worker's `fetch` against a
D1-shaped shim over `sql.js` (SQLite as wasm, pure JS, fine on Node 20). 32 checks, all passing —
routing, board hashing, isolation between boards, the claim race, partial and over-delivery, and
permissions. `npm install && npm test` in `server/`.

---

## Desynthesis API (Buddy's Desynther)

Full signatures, read off the live `FFXIVClientStructs.dll` with `MetadataLoadContext` on
2026-08-05. Not guesses — but metadata proves a method *exists*, not what its arguments mean, so the
open questions at the bottom are real.

| Symbol | Signature / value |
| --- | --- |
| `AgentSalvage.Instance()` | the agent; `AgentId.Salvage` = **129** |
| `AgentSalvage.SalvageItem(InventoryItem* item, int addonId, byte a4)` | the desynthesise action — same as the right-click menu entry |
| **`AgentSalvage.DesynthResults`** | **`Span<SalvageResult>` — the game reports its own yield** |
| `SalvageResult` | `uint ItemId`, `int Quantity` |
| `AgentSalvage.DesynthItemId` / `DesynthItemSlot` | the item currently being desynthesised |
| `AgentSalvage.IsSalvageResultAddonOpen` | |
| `AgentSalvage.SalvageItemCategory` | `InventoryHousing`, `InventoryEquipment`, `Equipped`, `Armoury*` — the game's own grouping |
| `AddonSalvageDialog.BulkDesynthEnabled` (bool) | the "desynthesize multiple" checkbox, forced **off** so results stay per-item |
| `AddonSalvageDialog.DesynthesizeButton` / `CancelButtonNode` / `BulkDesynthCheckboxNode` | |
| `PlayerState.GetDesynthesisLevel(uint classJobId)` | returns **`float`** — the level is fractional |
| Addon names | `SalvageDialog`, `SalvageResult`, `SalvageItemSelector` |

**`DesynthResults` is the find.** The yield does not have to be inferred; the game states it. Buddy's
Desynther records the **inventory diff** anyway and counts the disagreements — the agent's claim is
cross-checked, not trusted, and the count is shown in the ledger.

### ❌ `AgentSalvage.ItemList` did not work — use the item sheet

Tried and abandoned 2026-08-05. `AgentSalvage.ItemList` / `ItemCount` looked like the authoritative
list of desynthesisable items, and `AgentSalvage.SelectedCategory` correctly reported the open tab —
but with the Desynthesis window open on **Armoury Chest (Main Hand/Off Hand)**, showing a dozen
items on screen, the read came back **empty**. `SelectedCategory` was right, `ItemCount` was not.

Cause unknown. `ItemListAdd` may populate the addon rather than the agent, or the agent's list may
be built lazily per tab. Not worth chasing, because the item sheet answers the same question without
needing any window open at all:

| Question | Column |
| --- | --- |
| Can it be desynthesised? | `Item.Desynth > 0` |
| What level does it need? | `Item.LevelItem.RowId` — its item level |
| Whose skill applies? | `Item.ClassJobRepair.RowId` — **the class that made it** |
| What is it worth? | `Item.PriceLow` (vendor) |

Building on the sheet also means the module works from bags *and* the armoury chest at once, with no
dependence on which tab the player happens to have selected.

### ❌ Item level does NOT gate desynthesis — and that red Skill column is another plugin

**Settled 2026-08-05, after getting it wrong twice in both directions.** Proof: a **Classical
Battleaxe** displayed as `32/580` in bright red was desynthesised successfully —

```
You desynthesize a classical battleaxe.
You obtain 2 bottles of grade 6 vitality alkahest.
You obtain 2 fire clusters.
You obtain an earth cluster.
```

Any desynthesisable item can be desynthesised at any skill level. Being under-levelled means poorer
yields and no skill gain; it is **not** a refusal. There is nothing here to filter on.

**The `skill/required` column does not exist in the base game.** It is added by SimpleTweaks'
*Extended Desynthesis Window* (`UiAdjustments@ExtendedDesynthesisWindow` — visible in `dalamud.log`
on this machine), and its red is that tweak flagging you as under-levelled. The vanilla window shows
Item, Quantity, Class and GearSet, and nothing else.

How this went wrong, because the shape of the error is the useful part:

1. Guessed a level requirement from two real sheet columns, and *hid* everything that failed it.
2. Told it was wrong, dropped it — correctly — but without asking for evidence either way.
3. Reinstated it from a screenshot, having read another plugin's UI as the game's.

Two rules out of that. **A screenshot of a modded client is not evidence about the game** — check
what else is loaded first; `dalamud.log` names every tweak. And **a rule that hides things needs
proof, where a rule that merely annotates them can run on a hunch.** The `skill/required` pair
survives as exactly that: an amber hint about yield, wired to nothing.

`AgentSalvage.ItemListAdd`'s leading `bool meetsLevelRequirement` presumably drives some vanilla
annotation. It is still not a refusal.

Also confirmed here: **desynthesis results are printed to the chat log** ("You obtain 2 fire
clusters"), which is a third independent record beyond the agent's `DesynthResults` and the
inventory diff, should either ever be doubted.

### ✅ Desynthesis is unlocked per crafting class at level 30 — ask the game

```csharp
PlayerState.Instance()->GetClassJobLevel((int)classJobId, shouldGetSynced: false) >= 30
```

That is the eligibility rule, and it was **one call away the whole time**. `Item.Desynth > 0` says an
item is desynthesisable in principle; whether *you* can do it depends on your level in
`Item.ClassJobRepair`, the class that made it. Item level is not involved.

`PlayerState` also exposes `ClassJobLevels`, `DesynthesisLevels` and
`GetDesynthesisLevel(classJobId)` (a `float`) — the desynthesis skill is a separate number from the
class level, which is the easy thing to conflate.

**How four passes were wasted not asking.** The rule was guessed from two real sheet columns
(`LevelItem` vs `GetDesynthesisLevel`), used to *hide* rows, corrected, reinstated from a screenshot
of a modded client, and finally replaced with trial-and-error — before anyone checked whether the
game would simply answer. Standing instruction from the user, 2026-08-05:

> if it's verifiable just verify what you think you know when it comes to any of the games classes
> and whatnot.

Reflecting over the live `FFXIVClientStructs.dll` with `MetadataLoadContext` needs no game session
and lists every field and signature. It is the cheap first move, not a last resort.

Trial-and-error survives as a **backstop, not a predictor**: anything the game declines for a reason
not modelled gets skipped once, remembered for the session, and the run carries on. A "desynthesise
everything" button must never hang on one awkward item.

`Salvage.Report()` also dumps `AgentSalvage.ItemList` raw — flags, class, container/slot, quantity,
item level — for whenever the next unexplained refusal turns up. Note the earlier "the list reads
empty" finding may have been a bad read rather than an empty list: the old code cross-checked each
row against its inventory slot and dropped any mismatch, which would look identical.

### ⚠ `GetInventoryItemCount` does not see the armoury chest

**Caught in game 2026-08-05.** `PlayerInventory.CountItem` calls it with `checkArmory: false`, so an
armoury item counted **0 both before and after** a desynthesis. The runner concluded nothing had
happened and stopped with *"Iron Cross-pein Hammer was never consumed, so nothing was recorded"* —
while the game had destroyed it and said so in chat.

`Salvage.SlotQuantity(bag, slot, itemId)` watches the **one slot** instead: it reads the container
directly, so it works for bags, armoury, or anywhere else, and it cannot be confused by a second
stack of the same item elsewhere. Any "did this action land" check should prefer the specific slot
over a whole-inventory count.

### ⚠ Crystals and shards are not in a bag slot

**Caught in game 2026-08-05, by the ledger's own cross-check.** The log said:

```
Desynth of Cannonballs: agent reported 2x Ishgardian Steel Ingot, 6x Fire Shard, 1x Earth Shard,
inventory gained 2x Ishgardian Steel Ingot. Recording the inventory.
```

Shards, crystals and clusters land in **`InventoryType.Crystals` (2001)**, not `Inventory1..4`. A
"what did I just gain" snapshot over the four bag pages silently misses all of them — and for
desynthesis they are often most of the yield, so the ledger was undercounting the thing it exists to
measure.

`PlayerInventory.Receivable` is the correct set for any gained-items diff: the four bags **plus**
crystals. `PlayerInventory.Bags` remains bags-only, because move destinations really are bag slots.

Worth noting the mechanism worked: recording both the agent's claim and the inventory diff, and
logging when they disagree, is what surfaced this. Neither number alone would have.

The crafting ClassJob ids are 8–15 (Carpenter through Culinarian).

### ⚠ `FireCallbackInt` returning true does not mean the window closed

**Caught in game 2026-08-05.** `CloseResult` was written as:

```csharp
if (addon->FireCallbackInt(-1))   // "did the call succeed", NOT "did it close"
    return true;
addon->Close(true);               // never reached
```

The callback fired happily and the `SalvageResult` window stayed up, so the fallback never ran and
every run stopped after **one item**. The same trap exists anywhere a `Fire*` return is mistaken for
an outcome — and it is the same lesson as the FC chest, where the move's return code proved nothing
and only the inventory did.

Two fixes, both worth copying:

1. **Escalate, and judge by the window.** `CloseResult(attempt)` steps through `FireCallbackInt(-1)`
   → `FireCallbackInt(0)` → `Close(true)` → `FireCloseCallback()` → `Hide(true, true, 0)`, and the
   caller keeps going until the addon is actually gone. `Hide` always works, so this terminates.
2. **Do not let cosmetic UI stall the work.** Only the confirmation dialog blocks the next
   desynthesis; the results window does not. It gets a couple of seconds of escalating attempts and
   is then ignored, with a warning logged rather than a stopped run.

### Still unconfirmed — settle these in game

- **`SalvageItem`'s second and third arguments.** Passing `0, 0` is a guess at "no opener addon".
- **Which callback dismisses `SalvageDialog`.** `FireCallbackInt(0)` is used, on the assumption that
  0 is the Desynthesize button and -1 is Cancel. This one is **working** — desynthesis completes —
  so the assumption is at least good enough.
- **How `SalvageResult` closes.** Unknown; `FireCallbackInt(-1)` does not do it. Handled by
  escalation rather than by knowing (see above).

None of these can strand the runner: every step is judged by **whether the source stack actually got
smaller**, not by a return code, and each has a timeout that stops the run with a plain-English
reason. `/bb desynth probe` writes the live agent and addon state to
`pluginConfigs/BuddysBuddy/desynth-probe-<timestamp>.txt` — run it at each stage of a real
desynthesis and these three questions answer themselves.

`SalvageDialog` also cross-checks against the installed plugins — it appears in every copy of
`ECommons` (Artisan, AutoRetainer, Questionable and a dozen others), the same corroboration used for
`FreeCompanyChest` and `InputNumeric`. `TrackyTrack` and `InventoryTools` are installed here and
cover desynth tracking and inventory search — useful references for how the finished thing should
feel.

### Armoury chest container ids, probed at the same time

`ArmoryOffHand` 3200, `ArmoryHead` 3201, `ArmoryBody` 3202, `ArmoryHands` 3203, `ArmoryWaist` 3204,
`ArmoryLegs` 3205, `ArmoryFeets` 3206, `ArmoryEar` 3207, `ArmoryNeck` 3208, `ArmoryWrist` 3209,
`ArmoryRings` 3300, `ArmorySoulCrystal` 3400, `ArmoryMainHand` 3500. Also `Currency` 2000,
`Crystals` 2001, `KeyItems` 2004.

Buddy's Desynther deliberately does **not** reach into the armoury or equipped gear. Desynthesis is
irreversible, and reaching into where the things you actually wear are kept is not a mistake worth
making possible.

---

## Log

### 2026-11-04 — Venue Audio 0.3: decluttered UI, first-run guide, floating live badge
- **Removed by Dedric's request:** Advanced hosting, Remember my choice, and every muted explanation line.
  The settings that only Advanced used were dropped from `Settings`: `RemoteServer`, `RelayAccessKey`,
  `AdvertisedHost`, `LocalHosting`, `AutomaticInternet`, `ShortCodes` and `RememberRole`. Config is now v6.
  The plugin always broadcasts with `automaticInternet: true`, and `Session` still supports the other modes
  for the tests. The role is always remembered.
- The Start broadcasting button now sits right under the HOST header, with the code box directly below it.
  "Announce at my venue" explains itself in a hover tooltip, using `ImGuiHoveredFlags.AllowWhenDisabled`,
  instead of a line of text.
- The listener checkboxes are now "Auto Discover Venues" (`WatchVenues`) and "Auto Join Venues" (`AutoJoin`).
- `GuideWindow`: four pages (Welcome / How to host / How to listen / Good to know). It opens when
  `!settings.SeenGuide`, `OnClose` sets the flag, and the title-bar question mark reopens it at the page for
  the current role. Updating users see it once as well.
- `LiveBadge`: a NoTitleBar auto-resize window shown via `DrawConditions()`, meaning
  `settings.LiveBadge && session.Broadcasting`. It shows the timer, listener count, the code (unless hidden),
  a music mute and an open-controls button. Its position is saved by imgui.ini under `###VenueAudioLive`.
- **Checked:** the Release build is clean (0 warnings) on `E:\dotnet\dotnet.exe` 10.0.401, and the
  Tests project builds. **Not checked in game:** the layout, the guide wrap width (400 px scaled), and
  dragging the badge.
- **Second pass (same day):**
  - "Add my microphone" checkbox removed. `MicrophoneId == ""` now means none, and the mic picker has a
    None entry. Config v7 resets `MicrophoneId` to "" once, because the old code always pre-picked a mic.
  - "Announce at my venue" is now "Announce broadcast".
  - The listener checkboxes carry hover tooltips. They share text with the guide through
    `GuideWindow.AutoDiscover` / `AutoJoin`.
  - **Third pass:** the motion now applies to every page, not just the guide. Shared `PageMotion`
    (used by both the main window and the guide):
    - Each page's height is measured from `GetCursorPosY()` after drawing. That value is in content
      space, so it is correct even when scrolled.
    - `PreDraw` eases toward the measured height. The main window locks its height with
      `SetNextWindowSizeConstraints((420, h), (inf, h))`, so the player can still drag the width. The
      guide sets its whole size.
    - The slide moves the page's finished vertices (`draw.VtxBuffer.Data[i].Pos`/`.Col`) by up to 90 px
      with an ease-out cubic plus an alpha fade. Layout never changes, so widgets don't squash mid-slide.
      An earlier `Indent` version would have narrowed every `GetContentRegionAvail` widget.
    - `ImDrawListPtr.VtxBuffer.Size`, `.Data[i]` (by ref), `ImDrawVert.Pos` (Vector2) and `.Col` (uint)
      compiled first try against the Dalamud.Bindings.ImGui shipped with API 15.
- **Dalamud window call order, read from the IL of `WindowHost.DrawInternal`** (net10 reflection probe
  in the scratchpad; PowerShell 5.1 cannot load these assemblies):
  - The order is `PreDraw` → `ApplyConditionals` → `Begin` → `Draw` → `PostDraw`.
  - `ApplyConditionals` calls `SetNextWindowSizeConstraints` only when `Window.SizeConstraints.HasValue`.
  - The other `SetNextWindowSizeConstraints(Zero, +inf)` in `DrawInternal` only runs under `hasError`.
  - So constraints set in `PreDraw` hold when `SizeConstraints` is null. `Flags` set in `PreDraw` are read
    afterwards, so they apply the same frame.
  - `Window.DrawInternal` no longer exists. It moved to `Dalamud.Interface.Windowing.WindowHost`.
- **Plugin icon:** `VenueAudio.Plugin/images/icon.png` is Dedric's 1254² pixel-art headphones,
  downscaled to 512² with System.Drawing using NearestNeighbor and PixelOffsetMode.Half. The alpha
  survived (corner alpha 0). `IconUrl` in `VenueAudio.json` points at raw.githubusercontent on main. The
  csproj copies it to `bin/Release/images/icon.png` for dev loads. Confirmed: the built manifest carries
  `IconUrl`.
- `E:\dotnet` is not on PATH in this shell. Plain `dotnet` exits with code 145 (no SDK), so call it by full path.

### 2026-11-04 — Venue Audio: short codes, walk-in join, test listener, mute and quality
- **`HousingManager` members confirmed by reflecting over the live `FFXIVClientStructs.dll`.** Its XML docs
  leave most of them out, so grepping `FFXIVClientStructs.xml` is not enough. Present: `IsInside()`,
  `GetCurrentIndoorHouseId()`, `GetCurrentWard/Plot/Room/Division()`. `HouseId` has `Id` (ulong; packs
  world, district, ward, plot and room), `WorldId`, `TerritoryTypeId`, `WardIndex`, `PlotIndex`,
  `RoomNumber` and `IsApartment`. The probe: a scratch net10 console app doing `Assembly.LoadFrom` with an
  `AssemblyResolve` pointed at `Hooks\dev`.
- **Not verified in game:** whether `GetCurrentIndoorHouseId().Id` is stable across visits and the same in
  every room of one house (basement, upstairs). Walk-in detection keys on it.
- Directory lives in the Buddy's Jobs worker (`/v1/venue/*`, table `venue_codes`). Worker tests: 45/45 with
  portable Node **22.12.0 at `E:\nodejs`** (checksum-verified, npm cache at `E:\nodejs\npm-cache`).
  **Deployed 2026-11-04**, version `680942c7-37e5-496f-ac7c-970deec21f15`, schema applied to remote D1
  `13568d51…` (only `jobs` existed before). Live round trip checked with the plugin's own `VenueDirectory`:
  publish, resolve, at-location, foreign-owner refresh refused, delete. `/v1/jobs` still answers 401 with no key.
  Wrangler auth lives at `E:\nodejs\wrangler-config` (set `XDG_CONFIG_HOME` to that; it works for wrangler 3
  on Windows). `wrangler login` times out after about 2 minutes, so a slow GitHub sign-in fails with
  "localhost can't be reached"; just rerun it.
- C# tests: 44/44 offline, 55/55 with `-Audio` (includes a real local broadcast joined by typed code).
- Gotcha: `private void OnUpdate(IFramework _)` makes every `_ = task` in the body an assignment to the
  parameter (CS0266), not a discard. Name the parameter.

### 2026-11-04 — Venue Audio: new UI from Dedric's "Venue Audio flow" canvas
- Window moved to `VenueWindow.cs`: role picker → Host / Listener, custom-drawn glow button,
  16-segment meter, now-listening card. Compiles clean (0 warnings) against the live Dalamud;
  **not yet seen in game.**
- Confirmed present in API 15 by compiling: `ImRaii.PushColor(..).Push(..)`, `ImRaii.PushStyle`,
  `ImRaii.Combo`, `ImRaii.TextWrapPos`, `ImRaii.PushFont(UiBuilder.IconFont)`,
  `UiBuilder.MonoFontHandle.Push()`, `ImGui.InputTextWithHint`, `ImGui.SliderInt(.., "%d%%")`,
  `ImDrawListPtr.AddText(Vector2, uint, string)`, `IPlayerState` via plugin constructor injection.
- Invite fragment is now `#k=<key>&v=<venue>&h=<host>`, labels capped at 48 characters with control
  characters stripped. The old `#k=` form still parses. Tests: 35/35.
- Short keys and walk-in venue detection are deferred; both need a directory service.

### 2026-11-04 — Venue Audio (`venue-audio/`): first review of the GPT-written plugin
- `venue-audio\native\Build.ps1 -Tests` builds and passes **32/32** offline checks (invites, AES-GCM
  tamper/replay, relay fan-out, WebSocket gateway). Not run: `-Audio` (hardware) and `-Internet`.
- Releases are `prerelease: true, make_latest: false`. Checked via the GitHub API: `releases/latest` is
  still `v0.4.3`, so Buddy's Buddy's `releases/latest/download/BuddysBuddy.zip` links are unaffected.
  `VenueAudio.zip` is **61.6 MB** (self-contained WebRelay + cloudflared).
- Every push to `venue-audio/native/**` on main cuts a release and has the bot commit `repo.json`.
- Not yet verified in game: long-session audio stability. The capture pump is clocked by a
  `PeriodicTimer` instead of by the capture device, and drift is handled by `ClearBuffer()` at 250 ms
  on both ends, so expect a periodic skip over a long set until it's tested.
- Local git refuses this repo ("dubious ownership", the folder's owner SID is from before the PC
  reset). Worked around per command with `git -c safe.directory=...`; global config left unchanged.

### 2026-08-23 — Buddy's Ledger, third pass: two of my own confirmed entries were wrong
A single two-way trade with HQ items and gil in both directions. The module came out **`Confirmed`**
with every figure correct — and disproved two things this file had recorded as settled one pass
earlier.

- **"Gil is trade slot 5"** — true for *their* gil, false for *yours*, which never appears in the
  local slots and lives only in `SendGilCount`. Recorded as a general rule from two trades that both
  happened to be receive-only. **A rule inferred from one direction of evidence is a rule about that
  direction.** No harm done, because the code read both sources rather than the one the note claimed
  was sufficient — and it logged the discrepancy itself the moment it mattered.
- **"The partner's slots carry no HQ flag"** — false. Inferred from `flags=None` on two trades that
  contained no HQ items; the first HQ trade reported `flags=HighQuality` correctly. Absence of a
  flag read as absence of the feature, which is the desynthesis item-level mistake in miniature.

Both had been written into doc comments as *justification for a design decision*, which is the part
worth flinching at: the decision (inventory as record, window as cross-check) is still right, but it
was being defended with a false premise. Corrected in the code and here.

The general form, and the reason this pass cost nothing: **the code hedged where the notes did not.**
Reading both gil sources and comparing them was justified in the notes as "not settled yet" — and
that hedge is what survived the note being wrong.

### 2026-08-23 — Buddy's Ledger, second pass: the probe paid for itself immediately
Two real trades, three probes. Every open question closed, and one design decision reversed.

- **Gil rides in trade slot `[5]` as item id 1**, agreeing exactly with `TradeNumberArray`. The guess
  was right, and it is now measured rather than assumed.
- **The both-confirmed test was wrong and would never have fired.** `TradeLocalState` sits at
  `WaitingForConfirmation` while the remote reads `Confirmed`, and the trade completes from there.
  Both real trades were consequently filed as `Reconstructed`. Changed to both `>= LockedIn`.
  **This is the value of a first run**: the logic was defensible on paper and simply false, and the
  fallback is the only reason no data was lost.
- **Reversed which source is authoritative.** The window was primary because it "is the deal as
  struck, HQ flags included". The probe showed the partner's slots report `flags=None` and
  `container=Invalid` — *there are no HQ flags to include*. The inventory diff reads your own bags,
  where quality is certain, so it is now the record and the window is the cross-check. This is the
  same conclusion the desynthesis ledger reached, arrived at from the opposite direction.
- The `!`/`~` markers now mean what they should: `~` is "no window reading to compare against", not
  "these numbers are shaky" — the numbers come from the same place on every row.

Lesson worth keeping: **an unverified premise dressed as a design rationale is still unverified.**
"The window has HQ flags" was written into a doc comment as justification and was never true.

### 2026-08-23 — the dev-plugin path went stale when the repo moved
**Symptom:** the in-game plugin showed six modules after two more were built and the build was
green. Nothing was wrong with the build.

**Cause:** the repo moved from `D:\Buddy's Buddy\` into `D:\CLAUDE\Buddy's Buddy\` (the old folder is
now `D:\Buddy's Buddy.rar`), and Dalamud's **Dev Plugin Locations** still pointed at the old path. A
dev plugin that cannot be found is not an error the player sees — Dalamud simply carries on with the
**installed release**, which was v0.2.0 and genuinely has six modules. A stale dev path and a
working-but-old plugin look identical in game.

**How it was found:** the live log said it in one line, which is the whole lesson —

```
[ERR] [PluginManager] Dev plugin path D:\Buddy's Buddy\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll does not exist
[INF] [BuddysBuddy] Buddy's Buddy loaded with 6 module(s).
```

**This is rule 4 again, in a new costume.** The dev path was recorded as a ✅ confirmed fact in this
file and in `buddys-buddy-overlay/knowledge/verified.md`, and it was true when written. Paths on
disk rot exactly like account and service state does — **anything naming an absolute path is an
environment fact with a shelf life**, and moving a folder invalidates it silently.

Also stale, found while checking: `dalamudConfig.json` → `DevPluginLoadLocations` now holds only the
one dead Buddy's Buddy entry. The MarketTraveler and DragAndDropTexturing entries recorded in
`d:\CLAUDE\knowledge\machine.md` are gone. Corrected there too.

**Check when a plugin looks out of date:** compare the version and module count in the log against
what was built, and confirm the dev path resolves, *before* suspecting the code.

### 2026-08-23 — Buddy's Ledger and Buddy's Jobs, first pass
- **The game hands over the whole trade.** `InventoryManager` carries both sides' items, both
  `TradeState`s, the partner's name and their entity id — no addon pointer, no chat parsing. Probed
  offline with `MetadataLoadContext` before a line of code was written, which is what turned up the
  two traps: `TradeNumberArray.TradeItem` holds an **icon** id rather than an item id, and
  `TradeLockedIn` is private (the only member of the set that is, and the one compile error in the
  first build).
- **No corroboration available.** Every other addon name here was cross-checked against installed
  plugins; nothing installed reads the trade window at all. Grep method verified against a known-good
  DLL first, so the empty result is a finding rather than a broken search.
- **Gil's location in the trade is still open** — the item array is 6 slots wide for a 5-slot window.
  Both candidate readings are kept side by side and compared rather than one being picked, which is
  the arrangement that caught the missing crystals in the desynthesis ledger.
- **Dalamud IPC cannot reach another player**, so the jobs board needed a server. Cloudflare Worker
  plus D1, with claim races decided by a conditional `UPDATE` rather than by the client.
- **Wrangler 4 will not run on this machine's Node 20.12.2.** Caught before deploy day. The worker is
  tested instead against real SQLite through a D1 shim — 32 checks, all passing.
- Shared out of the modules while writing them: `PlayerInventory.CountGil()` (was a private copy in
  `CharacterInventory`) and `GameItems.Search()`, which indexes the item sheet once because posting a
  job means naming something you do **not** own.

### 2026-08-05 — Buddy's Desynther, seventh pass
- **The eligibility rule was `GetClassJobLevel(classJobId, false) >= 30` all along.** Four passes
  went into guessing, correcting, re-guessing and then working around a question the game answers
  directly. Locked items are now greyed with the reason ("Carpenter is level 12 — desynthesis
  unlocks at 30") and excluded from runs.
- Standing instruction recorded: **verify anything verifiable about the game's classes and rules;
  do not infer it.** `MetadataLoadContext` over the live `FFXIVClientStructs.dll` costs nothing and
  needs no game session.

### 2026-08-05 — Buddy's Desynther, sixth pass
- **A refused item killed the whole run.** Some items are silently declined and I do not know why;
  after three wrong guesses at the rule, the runner now skips and remembers instead of predicting.
  A "desynthesise everything" button that dies on the first awkward item is not one.
- Every abandon path is now a skip, not a stop: request rejected, no dialog, or confirmed-but-never
  consumed. Skips route through the settle step so a half-open dialog gets cancelled first.
- The probe dumps `AgentSalvage.ItemList` raw, which is the most likely route to the real rule.

### 2026-08-05 — Buddy's Desynther, fifth pass
- **Only one item was ever eligible**, because item level was back in as a gate. It is not a gate;
  see above. Removed for good — nothing filters the list now.
- **"Never consumed, so nothing was recorded"** on an armoury item: `GetInventoryItemCount` ignores
  the armoury chest, so the before/after counts were both 0. Replaced with a single-slot watch.
- The pattern across passes three, four and five is the same: each stop had a *specific* cause that
  a general timeout message hid. The fix each time was to check the thing that actually changes —
  the slot, the window — rather than a proxy for it.

### 2026-08-05 — Buddy's Desynther, fourth pass
- **Runs stopped after one item.** `FireCallbackInt(-1)` returned true without closing the results
  window, so the fallbacks never ran and the settle step timed out. See the warning above. Fixed by
  escalating through five close paths and by demoting the results window from fatal to cosmetic.
- Both halves of that are general: never read a `Fire*` return as an outcome, and never let a window
  you do not control end a piece of work that is otherwise going fine.

### 2026-08-05 — Buddy's Desynther, third pass
- **`AgentSalvage.ItemList` reads empty.** The second pass hung the whole module off it; with the
  Desynthesis window open on an armoury tab full of items, the panel said "the game lists nothing in
  this category". Abandoned in favour of the item sheet, which needs no window open at all.
- **Item level really is the requirement**, settled by a screenshot of the game's own Skill column.
  See above. It is displayed, not filtered on.
- Design landed where it should have started: one table of everything desynthesisable in bags and
  the armoury, red rows for what you cannot do, and a button per pile that asks twice. No queue, no
  filters, no dependence on a game window.
- Lesson: "the game already knows what is desynthesisable" meant *stop making the user configure
  eligibility*. I heard *depend on the game's UI for everything*, and shipped something that showed
  an empty list. Check which problem is being solved before restructuring around it.

### 2026-08-05 — Buddy's Desynther, first pass
- **Item level was wrongly treated as a desynthesis requirement.** Corrected from in-game
  observation; see the warning above. The candidate list would have come out nearly empty for anyone
  who had not already levelled desynthesis, with a confident wrong reason on every excluded row.
  Metadata told me the columns existed and I invented the rule connecting them.
- The salvage API was probed **offline**, with `MetadataLoadContext` over the live
  `FFXIVClientStructs.dll` — no game session needed. Everything structural came out of that:
  signatures, field names, enum values, the item sheet columns. Worth remembering as the default
  first move; only *behaviour* actually requires the game.
- `AgentSalvage.DesynthResults` removed the need to infer yields from an inventory diff. The diff is
  still taken, and the two are compared.
- Three behavioural questions are left open (see above). `/bb desynth probe` exists to close them
  from inside a running game rather than by guessing harder.

### 2026-08-05 — Stash and Purse, and the spine under them
- Extracted `IItemSource` / `ItemSourceRegistry`; the chest and retainer tallies now publish what
  they already knew instead of keeping it to themselves. `ItemKey` is one type again — the chest's
  and the retainers' copies were identical.
- Building against API 15 turned up `IClientState.LocalPlayer` and `LocalContentId` being gone. See
  the section above; this compiles as CS1061, so unlike the API-level and config failures it was
  caught before ever reaching the game.
- `scripts/verify-config.cs` grew a third section for the new store file, on the same reasoning as
  the first two: it is data that persists, so it can break silently.

### 2026-07-28 — the plugin would not enable
Two independent causes, both from trusting the wrong Dalamud install:
- **API level.** Built as 14 against the stale `%AppData%` install; the live one is 15. The log said
  `DevPlugin BuddysBuddy is outdated`. Fixed with `Dalamud.NET.Sdk/15.0.0` plus
  `Directory.Build.props` pinning `DALAMUD_HOME` to the live path.
- **Config deserialization.** `Dictionary<string, IModuleConfig>` saved fine and then threw out of
  the plugin constructor on the next load. See the section above.

Lesson: `dalamud.log` is the first thing to read, and finding the *right* `dalamud.log` matters
more than reading it carefully. Five months of staleness on a file is the tell.

### 2026-07-28 — initial scaffold
- Built clean against SDK 13.0.0, but **API level 13 was wrong** — Dalamud needs 14. Fixed by
  pinning `Dalamud.NET.Sdk/14.0.2`.
- README originally said to add the build *folder* as a dev plugin location. Wrong: Dalamud
  requires the DLL path. Corrected.
- `/xldev` opens the Dalamud settings window, not a plugin installer. The dev-plugin path field
  lives under the **Experimental** tab; enabling the plugin happens in the plugin installer.
