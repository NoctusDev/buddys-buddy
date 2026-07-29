using System;
using System.Collections.Generic;
using BuddysBuddy.Services;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuddysBuddy.Config;

/// <summary>
/// The single saved configuration for the host and every module.
/// </summary>
/// <remarks>
/// Module settings are stored as raw <see cref="JObject"/> rather than as <see cref="IModuleConfig"/>.
/// Dalamud writes <c>$type</c> discriminators when saving but does not honour them for nested values
/// when loading, so a <c>Dictionary&lt;string, IModuleConfig&gt;</c> saves cleanly and then fails on
/// the next startup with "Could not create an instance of type IModuleConfig" — taking the whole
/// plugin down with it. Keeping the stored shape concrete removes that dependency entirely.
///
/// The live typed objects are cached in <see cref="_liveConfigs"/> and flushed back to JSON on save,
/// so modules can hold their config object and mutate it freely.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    /// <summary>Schema version. Bump when a migration is needed and handle it in <see cref="Migrate"/>.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Open the main window automatically when the character logs in.</summary>
    public bool OpenOnLogin { get; set; } = false;

    /// <summary>Per-module enable state, keyed by module internal name.</summary>
    public Dictionary<string, bool> ModuleEnabled { get; set; } = new();

    /// <summary>
    /// Per-module settings as raw JSON, keyed by module internal name.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> named <c>ModuleConfigs</c>. Configs written by the earlier broken
    /// build carry a <c>$type</c> of <c>Dictionary&lt;string, IModuleConfig&gt;</c> under that name,
    /// and Dalamud honours dictionary-level <c>$type</c> on load — so reusing the name would
    /// resurrect the original crash from files already on disk. Under a new name the stale key is
    /// simply an unknown property and is skipped, then dropped on the next save.
    /// </remarks>
    public Dictionary<string, JObject> ModuleSettings { get; set; } = new();

    /// <summary>Internal name of the module selected in the main window, restored across sessions.</summary>
    public string? LastSelectedModule { get; set; }

    /// <summary>Deserialised module configs handed out to modules; the source of truth while running.</summary>
    [JsonIgnore]
    private readonly Dictionary<string, IModuleConfig> _liveConfigs = new(StringComparer.OrdinalIgnoreCase);

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
    /// Returns the module's settings, deserialising the stored JSON on first use and creating
    /// defaults if absent. Unreadable stored settings are logged and replaced with defaults rather
    /// than being allowed to fail the load.
    /// </summary>
    public T GetModuleConfig<T>(string internalName) where T : class, IModuleConfig, new()
    {
        if (_liveConfigs.TryGetValue(internalName, out var cached) && cached is T alreadyLoaded)
            return alreadyLoaded;

        var result = new T();

        if (ModuleSettings.TryGetValue(internalName, out var stored) && stored is not null)
        {
            try
            {
                result = stored.ToObject<T>() ?? new T();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(
                    ex, "Could not read saved settings for module {Name}; using defaults.", internalName);
            }
        }

        _liveConfigs[internalName] = result;
        return result;
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

    public void Save()
    {
        // Flush live module objects back to their stored JSON form before writing.
        foreach (var (key, value) in _liveConfigs)
        {
            try
            {
                ModuleSettings[key] = JObject.FromObject(value);
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(ex, "Could not serialise settings for module {Name}.", key);
            }
        }

        DalamudServices.PluginInterface.SavePluginConfig(this);
    }
}
