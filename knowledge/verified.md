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
| `D:\Buddy's Buddy\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll` | ✅ correct dev-plugin path | Build output; `BuddysBuddy.json` sits beside it |
| `D:\Buddy's Buddy\src\BuddysBuddy\bin\Debug` | ❌ rejected by Dalamud | Folder path → *"not a valid path to a potential Dev Plugin"*. Dalamud wants the **DLL** |
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
