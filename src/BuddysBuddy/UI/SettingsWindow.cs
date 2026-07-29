using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Modules;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BuddysBuddy.UI;

/// <summary>
/// Host settings plus a per-module enable toggle and settings pane.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly ModuleManager _modules;
    private readonly Configuration _config;

    public SettingsWindow(ModuleManager modules, Configuration config)
        : base("Buddy's Buddy — Settings###BuddysBuddySettings")
    {
        _modules = modules;
        _config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawGeneral();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Modules");
        ImGui.TextDisabled("Toggle a module to load or unload it immediately.");
        ImGui.Spacing();

        if (_modules.Modules.Count == 0)
        {
            ImGui.TextWrapped("No modules found. Add one under src/BuddysBuddy/Modules/.");
            return;
        }

        foreach (var module in _modules.Modules)
            DrawModuleEntry(module);
    }

    private void DrawGeneral()
    {
        ImGui.TextUnformatted("General");
        ImGui.Spacing();

        var openOnLogin = _config.OpenOnLogin;
        if (ImGui.Checkbox("Open the main window on login", ref openOnLogin))
        {
            _config.OpenOnLogin = openOnLogin;
            _config.Save();
        }
    }

    private void DrawModuleEntry(IModule module)
    {
        using var id = ImRaii.PushId(module.InternalName);

        var enabled = _config.IsEnabled(module.InternalName, module.EnabledByDefault);
        if (ImGui.Checkbox("##enabled", ref enabled))
            _modules.SetEnabled(module, enabled);

        ImGui.SameLine();

        var open = ImGui.CollapsingHeader(module.Name);

        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(module.Description))
            ImGui.SetTooltip(module.Description);

        if (_modules.Failures.TryGetValue(module.InternalName, out var failure))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Failed to load.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(failure);
        }

        if (!open)
            return;

        using var indent = ImRaii.PushIndent();

        if (!string.IsNullOrEmpty(module.Description))
        {
            ImGui.TextWrapped(module.Description);
            ImGui.Spacing();
        }

        if (!module.IsLoaded)
        {
            ImGui.TextDisabled("Enable the module to change its settings.");
            return;
        }

        try
        {
            module.DrawSettings();
        }
        catch (System.Exception ex)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This module threw while drawing its settings.");
            ImGui.TextWrapped(ex.Message);
        }
    }
}
