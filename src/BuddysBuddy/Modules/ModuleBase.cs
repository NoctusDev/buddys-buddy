using BuddysBuddy.Config;
using BuddysBuddy.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace BuddysBuddy.Modules;

/// <summary>
/// Convenience base for modules: sensible defaults for everything optional, so a small module only
/// overrides <see cref="IModule.Name"/>, <see cref="IModule.InternalName"/> and <see cref="Draw"/>.
/// </summary>
public abstract class ModuleBase : IModule
{
    public abstract string Name { get; }

    public abstract string InternalName { get; }

    public virtual string Description => string.Empty;

    public virtual bool EnabledByDefault => true;

    public virtual int Order => 0;

    public bool IsLoaded { get; private set; }

    // Shorthands so module code reads cleanly.
    protected static IPluginLog Log => DalamudServices.Log;

    protected static IChatGui Chat => DalamudServices.ChatGui;

    protected static IFramework Framework => DalamudServices.Framework;

    protected static Configuration Config => Plugin.Config;

    /// <summary>Prints a chat message tagged with this module's name.</summary>
    protected void Print(string message) => Chat.Print($"[{Name}] {message}");

    void IModule.Load()
    {
        if (IsLoaded)
            return;

        OnLoad();
        IsLoaded = true;
    }

    void IModule.Unload()
    {
        if (!IsLoaded)
            return;

        // Flip first so a throwing OnUnload cannot leave the module stuck "loaded".
        IsLoaded = false;
        OnUnload();
    }

    /// <summary>Acquire resources here. See <see cref="IModule.Load"/>.</summary>
    protected virtual void OnLoad()
    {
    }

    /// <summary>Release everything <see cref="OnLoad"/> acquired. See <see cref="IModule.Unload"/>.</summary>
    protected virtual void OnUnload()
    {
    }

    public virtual void Draw()
        => ImGui.TextUnformatted($"{Name} has no interface.");

    public virtual void DrawSettings()
        => ImGui.TextDisabled("No settings.");

    public virtual bool HandleCommand(string args) => false;

    public virtual void Dispose()
    {
        ((IModule)this).Unload();
    }
}
