# Working on Buddy's Buddy

Everything here is for building the plugin or writing a module. If you only want to use it, the
[README](../README.md) is the whole story.

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
[BuddysBuddy.csproj](../src/BuddysBuddy/BuddysBuddy.csproj) to match your installed Dalamud.

> **If XIVLauncher runs with a custom roaming path, the SDK will not find the right Dalamud.**
> It resolves `%AppData%\XIVLauncher` by default, and where a launcher has been pointed elsewhere
> that path can still hold an older install. Building against it produces a plugin the running
> Dalamud rejects as outdated — and it compiles perfectly cleanly, so the first sign is in game.
>
> [`Directory.Build.props`](../Directory.Build.props) pins `DALAMUD_HOME` for that reason. Set it
> to your own install, or export a real `DALAMUD_HOME` environment variable, which overrides it.
> See [knowledge/dalamud.md](../knowledge/dalamud.md).

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

These run alongside CI rather than instead of it. [`.github/workflows/build.yml`](../.github/workflows/build.yml)
builds on every push and **asserts the emitted `DalamudApiLevel`**, and
[`release.yml`](../.github/workflows/release.yml) publishes `BuddysBuddy.zip` on a `v*` tag.

An older note here claimed Actions was blocked account-wide for billing reasons. That was true once
and is now stale — the Build workflow has run and succeeded repeatedly. The correction, and the
original entry underneath it, are in [knowledge/dalamud.md](../knowledge/dalamud.md).

## Dev install

Dev Plugin Locations wants the **path to the DLL itself**, not the folder that contains it.
Pointing at the folder gives *"The entered value is not a valid path to a potential Dev Plugin."*

1. In game, run `/xlsettings` → **Experimental**
2. Under **Dev Plugin Locations**, enter exactly:

   ```
   <repo>\src\BuddysBuddy\bin\Debug\BuddysBuddy.dll
   ```

3. Press **+**, then **Save**
4. Open the plugin installer → **Dev Tools** / **Installed Plugins**, and enable **Buddy's Buddy**

`BuddysBuddy.json` must sit next to the DLL — the build puts it there automatically.

If you also have the released version installed, disable it first. Two copies of the same
`InternalName` conflict, and which one wins is not worth guessing at.

See [knowledge/verified.md](../knowledge/verified.md) for what has been confirmed about the game
APIs these modules use, and what is known broken.

## Adding a module

Add a folder under `src/BuddysBuddy/Modules/<YourModule>/` with a class deriving from
[`ModuleBase`](../src/BuddysBuddy/Modules/ModuleBase.cs). That's it — modules are discovered by
reflection at startup, so there is no registry to update.
[Buddy's Bag](../src/BuddysBuddy/Modules/BuddysBag/) is the simplest one to read as a worked example.

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

Override more of [`ModuleBase`](../src/BuddysBuddy/Modules/ModuleBase.cs) as needed:

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

### Publishing what your module knows

If a module tracks somewhere items live, implement
[`IItemSourceProvider`](../src/BuddysBuddy/Game/ItemSource.cs) and register it in `OnLoad`:

```csharp
protected override void OnLoad() => ItemSourceRegistry.Register(_tally);

protected override void OnUnload() => ItemSourceRegistry.Unregister(_tally);
```

Buddy's Stash and Buddy's Purse then answer for it with no further work, and it is remembered across
sessions automatically. A source must report `LastSeen` honestly and leave `IsLive` false for
anything it has not read this session — everything downstream depends on being able to tell a
current count from a remembered one.

Dalamud services are exposed as statics on
[`DalamudServices`](../src/BuddysBuddy/Services/DalamudServices.cs); `ModuleBase` re-exposes the common
ones as `Log`, `Chat`, `Framework` and `Config`. Add new services to that class as you need them.

## Layout

```
src/BuddysBuddy/
├── Plugin.cs              entry point: config, item sources, windows, commands, module manager
├── Config/                Configuration + IModuleConfig
├── Game/                  shared game interop: inventory, item names, IItemSource + registry
├── Modules/               IModule, ModuleBase, ModuleManager, and one folder per module
├── Services/              Dalamud service container + command routing
└── UI/                    MainWindow, SettingsWindow
```

A module that throws while loading or drawing is caught, logged, and reported in the UI — it will
not take down the host or the other modules.

## Sibling repositories

One repo per plugin, plus [DalamudPlugins](https://github.com/NoctusDev/DalamudPlugins) — which
holds no code, only the combined `pluginmaster.json` that gives every plugin a single install URL.

- [MarketTraveler](https://github.com/NoctusDev/MarketTraveler) — market board buying

Two files are deliberately duplicated across the plugin repos and must be copied by hand when
either changes: [`knowledge/dalamud.md`](../knowledge/dalamud.md) and
[`Directory.Build.props`](../Directory.Build.props).

