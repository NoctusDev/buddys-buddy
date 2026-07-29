# Buddy's Buddy

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV that acts as a
**host for lots of small plugins**. Instead of shipping a dozen tiny plugins, each feature is a
self-contained *module* inside one assembly, with its own tab, its own settings, and its own
`/bb <module>` command — enable or disable each one independently, no game restart.

> Status: scaffold. The only module so far is `Sample`, which exists to be copied.

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

## Verify

```sh
dotnet run scripts/verify.cs
```

Reflection-only-loads the built plugin and asserts what Dalamud checks at load time — API level,
entry point, module discoverability — so a broken build is caught without launching the game. The
API level check matters most: a mismatch **compiles cleanly** and only fails in game.

```
API level
  PASS  manifest API level matches installed Dalamud — ours=14, installed=API 14x68
Modules
  PASS  SampleModule has a public parameterless constructor
All checks passed. The plugin is structurally loadable by Dalamud.
```

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
