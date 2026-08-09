using System;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using BuddysBuddy.Modules;
using BuddysBuddy.Services;
using BuddysBuddy.UI;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BuddysBuddy;

/// <summary>
/// Entry point. Owns the config, the window system, the command handlers and the module manager;
/// modules own everything else.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    /// <summary>The live configuration. Modules reach it through <see cref="ModuleBase.Config"/>.</summary>
    public static Configuration Config { get; private set; } = null!;

    /// <summary>What every container held, remembered across sessions.</summary>
    public static SourceStore Sources { get; private set; } = null!;

    /// <summary>How often the character's own containers are re-read.</summary>
    private const int RefreshIntervalMs = 500;

    /// <summary>How often the remembered snapshots are flushed to disk while playing.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    private readonly WindowSystem _windows = new("BuddysBuddy");
    private readonly ModuleManager _modules;
    private readonly CommandService _commands;
    private readonly MainWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly CharacterInventory _characterInventory = new();

    private DateTime _nextRefreshAt;
    private DateTime _nextSaveAt;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<DalamudServices>();

        Config = LoadConfig(pluginInterface);
        if (Config.Migrate())
            Config.Save();

        // Registered before the modules load, so a module's own sources shadow anything restored.
        Sources = new SourceStore();
        Sources.Load();
        ItemSourceRegistry.Register(Sources);
        ItemSourceRegistry.Register(_characterInventory);

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

        _nextSaveAt = DateTime.UtcNow + SaveInterval;

        DalamudServices.ClientState.Login += OnLogin;
        DalamudServices.ClientState.Logout += OnLogout;
        DalamudServices.Framework.Update += OnFrameworkUpdate;

        DalamudServices.Log.Information("Buddy's Buddy loaded with {Count} module(s).", _modules.Modules.Count);
    }

    /// <summary>
    /// Reads the saved configuration, falling back to defaults if it cannot be deserialised.
    /// </summary>
    /// <remarks>
    /// Dalamud deserialises the config inside the plugin constructor, so a config it cannot read
    /// throws out of the constructor and the plugin fails to load entirely — with the only clue in
    /// the Dalamud log. A settings file is never worth a dead plugin: log it and start fresh.
    /// </remarks>
    private static Configuration LoadConfig(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            return pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Saved configuration could not be read; starting from defaults.");
            return new Configuration();
        }
    }

    private void ToggleMainWindow() => _mainWindow.Toggle();

    private void ToggleSettingsWindow() => _settingsWindow.Toggle();

    private void OnLogin()
    {
        if (Config.OpenOnLogin)
            _mainWindow.IsOpen = true;
    }

    /// <summary>Logging out is the last moment this character's containers can be recorded.</summary>
    private void OnLogout(int type, int code) => Sources.Save();

    /// <summary>
    /// Keeps the character's own containers current, and flushes the remembered snapshots.
    /// </summary>
    /// <remarks>
    /// The host owns this rather than a module so that "where is my stuff" still has a character to
    /// answer for when every optional module is switched off.
    /// </remarks>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        if (now < _nextRefreshAt)
            return;

        _nextRefreshAt = now.AddMilliseconds(RefreshIntervalMs);

        try
        {
            _characterInventory.Refresh();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Reading the character's containers failed.");
        }

        if (now < _nextSaveAt)
            return;

        _nextSaveAt = now + SaveInterval;
        Sources.Save();
    }

    public void Dispose()
    {
        // Reverse of construction: stop new work arriving, then tear down.
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DalamudServices.ClientState.Logout -= OnLogout;
        DalamudServices.ClientState.Login -= OnLogin;

        // Before the modules go, while their sources are still registered.
        Sources.Save();

        _modules.Dispose();
        _commands.Dispose();

        ItemSourceRegistry.Unregister(_characterInventory);
        ItemSourceRegistry.Unregister(Sources);

        DalamudServices.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;

        _windows.RemoveAllWindows();
    }
}
