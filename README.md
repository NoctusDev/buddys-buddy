# Buddy's Buddy

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV that acts as a
**host for lots of small plugins**. Instead of shipping a dozen tiny plugins, each feature is a
self-contained *module* inside one assembly, with its own tab, its own settings, and its own
`/bb <module>` command — enable or disable each one independently, no game restart.

## Modules

| Module | Command | What it does |
| --- | --- | --- |
| **Buddy's Bag** | `/bb bag` | Opens the chocobo saddlebag and moves every item out of it into your inventory |
| **Buddy's Chest** | `/bb chest` | Tallies the whole free company chest and withdraws every unit of a chosen item |
| Sample | `/bb sample` | Worked example — copy it to start a new module |

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

Search the list, then **Take all** on a row to pull every stack of that item into free inventory
slots. Anything sitting in a compartment that is not currently open cannot be moved, and the
shortfall is reported rather than silently dropped.

`/bb chest` reports the tally · `/bb chest find <name>` looks an item up · `/bb chest rescan`
clears it · `/bb chest stop` aborts a withdrawal.

Two caveats, both flagged in [knowledge/verified.md](knowledge/verified.md): withdrawal uses the
chest agent's own move function, which takes no quantity argument and so should bypass the game's
quantity prompt — believed, not yet confirmed in game. And the optional "load all compartments"
button guesses at the tab callback; it is off by default and clicking the tabs by hand always
works.

## Requirements

- FFXIV with [XIVLauncher / Dalamud](https://goatcorp.github.io/) installed
- [.NET SDK 10](https://dotnet.microsoft.com/download) (matches Dalamud's `.NETCoreApp v10.0` runtime)

## Build

```sh
dotnet build BuddysBuddy.slnx -c Debug
```

`Dalamud.NET.Sdk` resolves the Dalamud assemblies from your local install at
`%AppData%\XIVLauncher\addon\Hooks\dev`, so XIVLauncher must have run at least once. Output lands in
`src/BuddysBuddy/bin/Debug/` as `BuddysBuddy.dll` plus a generated `BuddysBuddy.json` manifest. A
Release build additionally packages `bin/Release/BuddysBuddy/latest.zip` for distribution.

If restore fails on the SDK version, bump `Sdk="Dalamud.NET.Sdk/13.0.0"` in
[BuddysBuddy.csproj](src/BuddysBuddy/BuddysBuddy.csproj) to match your installed Dalamud.

> **This machine's Dalamud is not in the default location.** XIVLauncher runs with a custom
> roaming path, so the live install is `d:\Multibox\Glumishxiv3`, while the SDK's default
> `%AppData%\XIVLauncher` holds a stale copy on an older API level. Building against the default
> produces a plugin the game rejects as outdated. [`Directory.Build.props`](Directory.Build.props)
> pins this; see [knowledge/verified.md](knowledge/verified.md).

## Verify

```sh
dotnet run scripts/verify.cs          # structure: API level, entry point, modules
dotnet run scripts/verify-config.cs   # the saved config still loads
```

Between them these cover the two ways this plugin has actually broken, both of which **compiled
cleanly** and only failed in game:

- an **API level** mismatch, so Dalamud refuses the plugin as outdated
- a **config** Dalamud cannot deserialise, which throws out of the plugin constructor and kills the
  whole plugin

```
API level
  PASS  manifest API level matches installed Dalamud — ours=15, installed=API 15x86
Modules
  PASS  BuddysBagModule has a public parameterless constructor
All checks passed. The plugin is structurally loadable by Dalamud.
```

They are separate on purpose: `verify.cs` uses a reflection-only load and so can never run a
serializer, while `verify-config.cs` really loads the assembly and runs Newtonsoft with Dalamud's
own settings against the config file on disk.

This exists because **GitHub Actions cannot run on this account** — jobs are refused with *"your
account is locked due to a billing issue"*, which is account-wide and unaffected by repo
visibility. [`.github/workflows/build.yml`](.github/workflows/build.yml) is left in place and is
believed correct, but has never executed a step, so treat it as unproven. Details and evidence in
[knowledge/verified.md](knowledge/verified.md).

## Dev install

Dev Plugin Locations wants the **path to the DLL itself**, not the folder that contains it.
Pointing at the folder gives *"The entered value is not a valid path to a potential Dev Plugin."*

1. In game, run `/xlsettings` → **Experimental**
2. Under **Dev Plugin Locations**, enter exactly:

   ```
   D:\Buddy's Buddy\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll
   ```

3. Press **+**, then **Save**
4. Open the plugin installer → **Dev Tools** / **Installed Plugins**, and enable **Buddy's Buddy**

`BuddysBuddy.json` must sit next to the DLL — the build puts it there automatically.

See [knowledge/verified.md](knowledge/verified.md) for what is confirmed working in this
environment and what is known broken.

## Commands

| Command | Effect |
| --- | --- |
| `/bb` | Toggle the main window |
| `/bb settings` | Open settings |
| `/bb help` | List available module commands |
| `/bb <module> [args]` | Send arguments to a module |

`/buddysbuddy` is an alias for `/bb`.

## Adding a module

Copy [src/BuddysBuddy/Modules/Sample/](src/BuddysBuddy/Modules/Sample/) to
`src/BuddysBuddy/Modules/<YourModule>/`, rename the class and change `InternalName`. That's it —
modules are discovered by reflection at startup, so there is no registry to update.

The smallest useful module:

```csharp
using BuddysBuddy.Modules;
using Dalamud.Bindings.ImGui;

namespace BuddysBuddy.Modules.Greeter;

public sealed class GreeterModule : ModuleBase
{
    public override string Name => "Greeter";
    public override string InternalName => "greeter";

    public override void Draw() => ImGui.TextUnformatted("Hello!");
}
```

Override more of [`ModuleBase`](src/BuddysBuddy/Modules/ModuleBase.cs) as needed:

| Member | Purpose |
| --- | --- |
| `OnLoad()` / `OnUnload()` | Acquire and release resources. Anything subscribed in `OnLoad` **must** be released in `OnUnload` — modules can be toggled at runtime. |
| `Draw()` | The module's pane in the main window |
| `DrawSettings()` | The module's section in the settings window |
| `HandleCommand(args)` | Handles `/bb <internalname> [args]`; return `false` for unrecognised input |
| `Description`, `Order`, `EnabledByDefault` | Presentation and default state |

### Settings

Declare a class implementing `IModuleConfig` and fetch it in `OnLoad`:

```csharp
private MyConfig _settings = null!;

protected override void OnLoad()
    => _settings = Config.GetModuleConfig<MyConfig>(InternalName);
```

All module settings live in the single host config at
`%AppData%\XIVLauncher\pluginConfigs\BuddysBuddy.json`, keyed by `InternalName`. Call `Config.Save()`
after a change. **Do not change `InternalName` after release** — it orphans saved settings.

### Services

Dalamud services are exposed as statics on
[`DalamudServices`](src/BuddysBuddy/Services/DalamudServices.cs); `ModuleBase` re-exposes the common
ones as `Log`, `Chat`, `Framework` and `Config`. Add new services to that class as you need them.

## Layout

```
src/BuddysBuddy/
├── Plugin.cs              entry point: config, windows, commands, module manager
├── Config/                Configuration + IModuleConfig
├── Modules/               IModule, ModuleBase, ModuleManager, and one folder per module
├── Services/              Dalamud service container + command routing
└── UI/                    MainWindow, SettingsWindow
```

A module that throws while loading or drawing is caught, logged, and reported in the UI — it will
not take down the host or the other modules.

## License

MIT — see [LICENSE](LICENSE).
