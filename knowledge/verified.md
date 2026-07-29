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
numbers — do not assume 15.0.3.0 means API 15. Verified across all 63 plugins in
`%AppData%\XIVLauncher\installedPlugins`: 63/63 report `DalamudApiLevel: 14`, 0 report 13 or 15.

To re-check after any SDK bump:

```sh
dotnet build BuddysBuddy.slnx -c Debug
# then confirm the generated manifest:
#   src/BuddysBuddy/bin/Debug/BuddysBuddy.json  ->  "DalamudApiLevel": 14
```

---

## APIs confirmed present in this Dalamud

Checked against `%AppData%\XIVLauncher\addon\Hooks\dev\Dalamud.xml` before use:

- `Dalamud.Interface.Windowing` — `WindowSystem`, `Window`, `WindowSizeConstraints`
- `Dalamud.Interface.Utility.Raii.ImRaii` — `Child`, `PushId`, `PushIndent`
- `Dalamud.Bindings.ImGui` — Dalamud's own ImGui bindings (**not** `ImGuiNET`)
- `IDalamudPluginInterface.Create<T>()` — used to inject `DalamudServices`
- Newtonsoft.Json is bundled, so polymorphic module configs round-trip

---

## GitHub Actions

| Fact | Status |
| --- | --- |
| Actions on this account's **private** repos | ❌ `startup_failure` in <1s, no logs |
| Repo made **public** on 2026-07-28 | ✅ public repos get free unlimited minutes |

Diagnosed by isolation: a nine-line `echo ok` job on `ubuntu-latest` failed identically to the real
build workflow, proving the workflow file was not at fault.

---

## Log

### 2026-07-28 — initial scaffold
- Built clean against SDK 13.0.0, but **API level 13 was wrong** — Dalamud needs 14. Fixed by
  pinning `Dalamud.NET.Sdk/14.0.2`.
- README originally said to add the build *folder* as a dev plugin location. Wrong: Dalamud
  requires the DLL path. Corrected.
- `/xldev` opens the Dalamud settings window, not a plugin installer. The dev-plugin path field
  lives under the **Experimental** tab; enabling the plugin happens in the plugin installer.
