using BuddysBuddy.Config;
using BuddysBuddy.Modules;
using BuddysBuddy.Services;
using BuddysBuddy.UI;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace BuddysBuddy;

/// <summary>
/// Entry point. Owns the config, the window system, the command handlers and the module manager;
/// modules own everything else.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    /// <summary>The live configuration. Modules reach it through <see cref="ModuleBase.Config"/>.</summary>
    public static Configuration Config { get; private set; } = null!;

    private readonly WindowSystem _windows = new("BuddysBuddy");
    private readonly ModuleManager _modules;
    private readonly CommandService _commands;
    private readonly MainWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<DalamudServices>();

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.Migrate())
            Config.Save();

        _modules = new ModuleManager(Config);

        _settingsWindow = new SettingsWindow(_modules, Config);
        _mainWindow = new MainWindow(_modules, Config, () => _settingsWindow.IsOpen = true);
        _windows.AddWindow(_mainWindow);
        _windows.AddWindow(_settingsWindow);

        pluginInterface.UiBuilder.Draw += _windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;

        _commands = new CommandService(_modules, ToggleMainWindow, ToggleSettingsWindow);

        // Modules load last so they can rely on everything above being ready.
        _modules.DiscoverAndLoad();

        DalamudServices.ClientState.Login += OnLogin;

        DalamudServices.Log.Information("Buddy's Buddy loaded with {Count} module(s).", _modules.Modules.Count);
    }

    private void ToggleMainWindow() => _mainWindow.Toggle();

    private void ToggleSettingsWindow() => _settingsWindow.Toggle();

    private void OnLogin()
    {
        if (Config.OpenOnLogin)
            _mainWindow.IsOpen = true;
    }

    public void Dispose()
    {
        // Reverse of construction: stop new work arriving, then tear down.
        DalamudServices.ClientState.Login -= OnLogin;

        _modules.Dispose();
        _commands.Dispose();

        DalamudServices.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;

        _windows.RemoveAllWindows();
    }
}
