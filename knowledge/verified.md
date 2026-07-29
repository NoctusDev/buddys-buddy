# Verified environment notes

What is **confirmed working**, what is **confirmed broken**, and how each was checked. Append a
dated entry whenever something is proven either way — the point is to never rediscover the same
fact twice.

Machine: Windows 10 Pro 19045 · .NET SDK 10.0.103 · Dalamud **15.0.3.0** · GitHub `NoctusDev`

---

## ⚠ The live Dalamud is NOT in %AppData% — read this first

XIVLauncher on this machine runs with a custom roaming path (a multibox setup):

```
XIVLauncher.exe --roamingPath="d:\Multibox\Glumishxiv3"
```

| | Live | Stale decoy |
| --- | --- | --- |
| Root | `d:\Multibox\Glumishxiv3` | `%AppData%\XIVLauncher` |
| Assemblies | `addon\Hooks\dev` *and* `addon\Hooks\15.0.3.0` (identical) | `addon\Hooks\dev` |
| **API level** | **15** (83 plugins) | 14 (68 plugins) |
| Last written | today | 2026-03-01 |
| Log | `dalamud.log` — live | `dalamud.log` — five months stale |

**Both paths exist and both look plausible.** The `%AppData%` one is a five-month-old install that
the SDK picks by default, and building against it produces a plugin the running Dalamud rejects as
outdated (`DevPlugin BuddysBuddy is outdated, not loading automatically`). Everything in this file
dated before this section was originally derived from the wrong one.

[`Directory.Build.props`](../Directory.Build.props) pins `DALAMUD_HOME` to the live path, and both
verify scripts resolve it the same way. A real `DALAMUD_HOME` environment variable overrides both.

**To confirm the live path on any machine** — ask the running game, do not guess:

```powershell
Get-Process ffxiv_dx11 | ForEach-Object { $_.Modules } |
  Where-Object ModuleName -eq "Dalamud.dll" | Select-Object FileName
Get-CimInstance Win32_Process -Filter "Name='XIVLauncher.exe'" | Select-Object CommandLine
```

**The log to read when a plugin misbehaves is the live one:**
`d:\Multibox\Glumishxiv3\dalamud.log`

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

### Dev Plugin Locations format

Confirmed by reading `%AppData%\XIVLauncher\dalamudConfig.json` → `DevPluginLoadLocations`, which
holds this machine's already-working entries:

```
E:\MarketTraveler\bin\Debug\MarketTraveler.dll
D:\Anenmmesis\DragAndDropTexturing\DragAndDropTexturing.dll
```

Both are absolute paths ending in `.dll`. That is the required shape.

---

## Build

| Fact | Status | Evidence |
| --- | --- | --- |
| `Dalamud.NET.Sdk/15.0.0` → `DalamudApiLevel: 15` | ✅ **correct** | Matches the live install (83 plugins on API 15) |
| `Dalamud.NET.Sdk/14.0.2` → `DalamudApiLevel: 14` | ❌ rejected as outdated | Derived from the stale `%AppData%` install |
| `Dalamud.NET.Sdk/13.0.0` → `DalamudApiLevel: 13` | ❌ rejected as outdated | The first mistake |
| `net10.0-windows` | ✅ correct TFM | `Dalamud.deps.json` declares `.NETCoreApp,Version=v10.0` |

**The live Dalamud 15.0.3.0 uses API level 15.** Version and API level are different numbers, so
derive the level from installed plugin manifests — but *from the live install*, per the warning at
the top of this file. Counting the wrong directory is what produced two rejected builds in a row.

An API mismatch **builds perfectly cleanly** and only fails in game.
`dotnet run scripts/verify.cs` checks it against the live install.

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

## APIs confirmed present in this Dalamud

Checked against `%AppData%\XIVLauncher\addon\Hooks\dev\Dalamud.xml` before use:

- `Dalamud.Interface.Windowing` — `WindowSystem`, `Window`, `WindowSizeConstraints`
- `Dalamud.Interface.Utility.Raii.ImRaii` — `Child`, `PushId`, `PushIndent`
- `Dalamud.Bindings.ImGui` — Dalamud's own ImGui bindings (**not** `ImGuiNET`)
- `IDalamudPluginInterface.Create<T>()` — used to inject `DalamudServices`
- Newtonsoft.Json is bundled, so polymorphic module configs round-trip

---

## Plugin config serialization — do not rely on nested `$type`

Dalamud saves plugin configs with `TypeNameHandling.Objects`, so the written file **does** contain
`$type` discriminators. It does **not** honour them for nested values when loading. A
`Dictionary<string, IModuleConfig>` therefore saves cleanly and then fails on the next startup:

```
JsonSerializationException: Could not create an instance of type BuddysBuddy.Config.IModuleConfig.
Type is an interface or abstract class and cannot be instantiated.
Path 'ModuleConfigs.sample.GreetOnLoad'
```

Dictionary-level `$type` *is* honoured (`Dictionary<string, bool>` round-trips fine) — it is only
the polymorphic element type that breaks.

Three rules that came out of this:

1. **Store module settings as `Dictionary<string, JObject>`**, and convert to the concrete type on
   demand. No polymorphism in the declared shape, no dependency on serializer settings.
2. **Never reuse a property name whose old `$type` was poisonous.** A file written by the broken
   build still says `ModuleConfigs` is a `Dictionary<string, IModuleConfig>`, and that metadata is
   honoured on load — so the property was renamed to `ModuleSettings`. The stale key is then an
   unknown property, skipped, and dropped on the next save.
3. **Wrap `GetPluginConfig()` in try/catch.** Dalamud deserialises inside the plugin constructor, so
   an unreadable config kills the entire plugin. A settings file is never worth a dead plugin.

---

## Local verification (CI substitute)

```sh
dotnet build BuddysBuddy.slnx -c Debug
dotnet run scripts/verify.cs          # structure: API level, entry point, modules
dotnet run scripts/verify-config.cs   # config actually round-trips
```

Both are .NET 10 file-based apps (single `.cs`, no project). Written because Actions cannot run
here — they are the replacement for CI, not an extra. Last run: **all checks passed**.

| Script | Checks | Would have caught |
| --- | --- | --- |
| [`verify.cs`](../scripts/verify.cs) | Manifest beside DLL, API level vs the **live** install, one `IDalamudPlugin`, module constructors | The API 13/14 mismatches |
| [`verify-config.cs`](../scripts/verify-config.cs) | Deserialises the real saved config, then a full save/load round-trip with module settings | The `IModuleConfig` crash |

They are deliberately separate: `verify.cs` uses a reflection-only load and can never run a
serializer, so it is structurally incapable of catching a config bug. `verify-config.cs` really
loads the assembly and really runs Newtonsoft with Dalamud's settings.

---

## GitHub Actions — blocked at the account level

**Actions cannot run on this account at all.** The exact error, from the job annotation API:

> The job was not started because your account is locked due to a billing issue.

| Attempt | Result |
| --- | --- |
| Private repo, real build workflow | ❌ `startup_failure`, <1s, no logs |
| Private repo, nine-line `echo ok` on `ubuntu-latest` | ❌ identical failure → workflow file is **not** at fault |
| Public repo, real build workflow | ❌ `failure`, 0 steps ran, billing-lock annotation |

Public repos normally get unlimited free Actions minutes, so the fact that going public did **not**
help is what proves this is an account-wide lock rather than a minutes quota. The repo was returned
to **private** afterwards, since the only reason to make it public had evaporated.

Fix is outside this repo: **github.com/settings/billing** — an unpaid invoice or declined payment
method, typically from Copilot, Pro, private-repo Actions overage, or Packages/LFS storage. The
billing REST API needs a `user` token scope, which the current token lacks.

`.github/workflows/build.yml` is left in place and is believed correct — the
`dalamud-distrib/latest.zip` URL it depends on was verified reachable (HTTP 200, 58 MB) — but it
has **never executed a single step**, so treat it as unproven until Actions is unlocked.

---

## Log

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
