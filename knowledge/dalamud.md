# Dalamud environment notes — shared

Facts about this machine and about Dalamud itself, true regardless of which plugin is being built.
Plugin-specific findings live in each plugin repo's own `knowledge/verified.md`.

> ⚠️ **This file is duplicated byte-for-byte across every plugin repo.** Edit it in one, then copy
> it to the others in the same session. It is short on purpose so that stays cheap. If it ever grows
> past the point where copying is reliable, promote it to its own repo and reference it instead.

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

## Dev Plugin Locations format

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

## Dalamud API 15 moved the local character off `IClientState`

Confirmed by compiling against the live Dalamud on 2026-08-05: `IClientState.LocalPlayer` and
`IClientState.LocalContentId` **no longer exist** and fail with CS1061.

| Was | Now |
| --- | --- |
| `ClientState.LocalContentId` | `IPlayerState.ContentId` |
| `ClientState.LocalPlayer?.Name.TextValue` | `IPlayerState.CharacterName` (a plain `string`) |
| `ClientState.LocalPlayer` (the object) | `IObjectTable.LocalPlayer` |

`IPlayerState` is a `[PluginService]` like any other and is injected alongside the rest in each
plugin's service holder. It also carries `IsLoaded`,
`ClassJob`, `GrandCompany` and a pile of other character state that used to need ClientStructs.

`IClientState.Logout` takes **two ints** — `(int type, int code)`, per `IClientState.LogoutDelegate`.
A parameterless handler will not bind.

---

## ImGui: popups opened from inside a table never render

`ImGui.OpenPopup(id)` and `ImGui.BeginPopup(id)` are matched on the **id stack at the time of each
call**, not on the string alone. Opening a popup from inside a table cell — under a per-row
`ImRaii.PushId` — and then calling `BeginPopup` after `EndTable` puts the two on different id
stacks. The popup opens and nothing ever draws it: no window, no error, no log line.

For a per-row "prompt for a value" affordance, the row button should only **record the selection**,
with an inline panel drawn afterwards from the selected item. No id-scope coupling, and it cannot
silently fail to appear. Reach for a popup only when the open and begin calls are provably at the
same scope.

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

## Local verification (alongside CI, not instead of it)

```sh
dotnet build BuddysBuddy.slnx -c Debug
dotnet run scripts/verify.cs          # structure: API level, entry point, modules
dotnet run scripts/verify-config.cs   # config actually round-trips
```

Both are .NET 10 file-based apps (single `.cs`, no project). Originally written because Actions
could not run; Actions works now (see below), so treat these as the fast local check before pushing
rather than the only check. Last run: **all checks passed**.

`verify-config.cs` is Buddy's Buddy-specific (it round-trips that plugin's config and item-source
store) and lives only in that repo. `verify.cs` is generic: it takes the plugin name as its third
argument and ships in every plugin repo.

| Script | Checks | Would have caught |
| --- | --- | --- |
| [`verify.cs`](../scripts/verify.cs) | Manifest beside DLL, API level vs the **live** install, one `IDalamudPlugin`, module constructors | The API 13/14 mismatches |
| `verify-config.cs` (Buddy's Buddy only) | Deserialises the real saved config, a full save/load round-trip with module settings, and a round-trip of the remembered item sources | The `IModuleConfig` crash |

They are deliberately separate: `verify.cs` uses a reflection-only load and can never run a
serializer, so it is structurally incapable of catching a config bug. `verify-config.cs` really
loads the assembly and really runs Newtonsoft with Dalamud's settings.

---

## ✅ GitHub Actions works — 2026-08-09

**Re-checked and the old "blocked at the account level" entry below is out of date.** The `Build`
workflow on `NoctusDev/buddys-buddy` has run **10 times and succeeded 10 times**, most recently on
"Publish: add plugin repository manifest, drop the sample module" in 1m 18s. Run times of 1–1.5
minutes are real Windows builds, not a runner refusing to start.

Verified from the public Actions page, which needs no token:
`https://github.com/NoctusDev/buddys-buddy/actions/workflows/build.yml`

The `startup_failure` runs still visible in the repo's Actions tab are the **older** ones, from the
period the entry below describes. They were never cleaned up, so the tab shows both eras at once and
reads worse than the situation is. Sort by workflow rather than scanning the mixed list.

**So: CI is available. Use it.** New plugin repos should get the same `build.yml`, and the emitted
API level should be asserted there rather than only in `verify.cs`.

### The stale entry, kept for the shape of the mistake

Actions *was* locked at the account level, with the job annotation:

> The job was not started because your account is locked due to a billing issue.

That was real, and the diagnosis at the time was sound — a nine-line `echo` job failed identically
to a full build, and going public did not help, which correctly ruled out the workflow file and a
minutes quota. What went wrong is that **the entry was never re-checked after the billing issue was
resolved**, so a fixed problem kept being treated as current and a working CI went unused.

The lesson is about this file, not about GitHub: **a "confirmed broken" entry about something
outside the codebase has a shelf life.** Anything depending on an account, a subscription, a service
or a game patch should be re-checked before it is relied on, and re-dated when it is. Confirmed-good
facts about the game's own APIs do not rot the same way; external blockers do.

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
