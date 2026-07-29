# Verified environment notes

What is **confirmed working**, what is **confirmed broken**, and how each was checked. Append a
dated entry whenever something is proven either way — the point is to never rediscover the same
fact twice.

Machine: Windows 10 Pro 19045 · .NET SDK 10.0.103 · Dalamud **15.0.3.0** · GitHub `NoctusDev`

---

## Paths

| Path | Status | Notes |
| --- | --- | --- |
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
| `Dalamud.NET.Sdk/14.0.2` → `DalamudApiLevel: 14` | ✅ required | Matches installed Dalamud |
| `Dalamud.NET.Sdk/13.0.0` → `DalamudApiLevel: 13` | ❌ rejected as outdated | Was the initial mistake |
| `Dalamud.NET.Sdk/15.0.0` | ⚠️ untested, likely too new | Would emit API 15; Dalamud 15.0.3.0 is API 14 |
| `net10.0-windows` | ✅ correct TFM | `Dalamud.deps.json` declares `.NETCoreApp,Version=v10.0` |

**Dalamud 15.0.3.0 uses API level 14.** The Dalamud *version* and the *API level* are different
numbers — do not assume 15.0.3.0 means API 15. Verified across `%AppData%\XIVLauncher\installedPlugins`:
**68 plugins report API 14**, one stale plugin still reports 13, none report 15.

This is the failure mode to watch for: an API mismatch **builds perfectly cleanly** and only fails
in game, where Dalamud rejects the plugin as outdated. `dotnet run scripts/verify.cs` checks it.

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

## APIs confirmed present in this Dalamud

Checked against `%AppData%\XIVLauncher\addon\Hooks\dev\Dalamud.xml` before use:

- `Dalamud.Interface.Windowing` — `WindowSystem`, `Window`, `WindowSizeConstraints`
- `Dalamud.Interface.Utility.Raii.ImRaii` — `Child`, `PushId`, `PushIndent`
- `Dalamud.Bindings.ImGui` — Dalamud's own ImGui bindings (**not** `ImGuiNET`)
- `IDalamudPluginInterface.Create<T>()` — used to inject `DalamudServices`
- Newtonsoft.Json is bundled, so polymorphic module configs round-trip

---

## Local verification (CI substitute)

```sh
dotnet build BuddysBuddy.slnx -c Debug
dotnet run scripts/verify.cs
```

[`scripts/verify.cs`](../scripts/verify.cs) is a .NET 10 file-based app (single `.cs`, no project)
that reflection-only-loads the built DLL and asserts the things Dalamud checks at load time:
manifest sits beside the DLL, API level matches the installed Dalamud, exactly one
`IDalamudPlugin` entry point, and every module has the parameterless constructor
`ModuleManager` needs. Last run: **all checks passed**.

Written because Actions cannot run here — this is the replacement, not an extra.

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

### 2026-07-28 — initial scaffold
- Built clean against SDK 13.0.0, but **API level 13 was wrong** — Dalamud needs 14. Fixed by
  pinning `Dalamud.NET.Sdk/14.0.2`.
- README originally said to add the build *folder* as a dev plugin location. Wrong: Dalamud
  requires the DLL path. Corrected.
- `/xldev` opens the Dalamud settings window, not a plugin installer. The dev-plugin path field
  lives under the **Experimental** tab; enabling the plugin happens in the plugin installer.
