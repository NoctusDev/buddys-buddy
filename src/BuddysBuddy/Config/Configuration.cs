using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace BuddysBuddy.Config;

/// <summary>
/// The single saved configuration for the host and every module.
/// </summary>
/// <remarks>
/// Dalamud serialises plugin configs with Newtonsoft using <c>TypeNameHandling.Objects</c>, so the
/// polymorphic <see cref="ModuleConfigs"/> dictionary round-trips concrete module config types.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Schema version. Bump when a migration is needed and handle it in <see cref="Migrate"/>.</summary>
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 1;

    /// <summary>Open the main window automatically when the character logs in.</summary>
    public bool OpenOnLogin { get; set; } = false;

    /// <summary>Per-module enable state, keyed by module internal name.</summary>
    public Dictionary<string, bool> ModuleEnabled { get; set; } = new();

    /// <summary>Per-module settings blobs, keyed by module internal name.</summary>
    public Dictionary<string, IModuleConfig> ModuleConfigs { get; set; } = new();

    /// <summary>Internal name of the module selected in the main window, restored across sessions.</summary>
    public string? LastSelectedModule { get; set; }

    /// <summary>
    /// Whether <paramref name="internalName"/> should be loaded, falling back to the module's own
    /// default the first time it is seen.
    /// </summary>
    public bool IsEnabled(string internalName, bool defaultEnabled)
        => ModuleEnabled.TryGetValue(internalName, out var enabled) ? enabled : defaultEnabled;

    public void SetEnabled(string internalName, bool enabled)
    {
        ModuleEnabled[internalName] = enabled;
        Save();
    }

    /// <summary>
    /// Returns the module's settings blob, creating and storing a default one if absent.
    /// </summary>
    public T GetModuleConfig<T>(string internalName) where T : class, IModuleConfig, new()
    {
        if (ModuleConfigs.TryGetValue(internalName, out var existing) && existing is T typed)
            return typed;

        // Either missing, or a stale/unresolvable type from an older build — reset to defaults.
        var created = new T();
        ModuleConfigs[internalName] = created;
        return created;
    }

    /// <summary>Applies any schema migrations. Returns true if the config changed and should be saved.</summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        // No migrations yet — future upgrades step Version forward one at a time here.
        Version = CurrentVersion;
        return true;
    }

    public void Save() => Services.DalamudServices.PluginInterface.SavePluginConfig(this);
}
