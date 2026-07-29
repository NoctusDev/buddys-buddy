using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Modules;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BuddysBuddy.UI;

/// <summary>
/// The host window: a list of loaded modules on the left, the selected module's pane on the right.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ModuleManager _modules;
    private readonly Configuration _config;
    private readonly Action _openSettings;

    // Draw exceptions are shown in place instead of spamming the log every frame.
    private readonly Dictionary<string, string> _drawErrors = new(StringComparer.OrdinalIgnoreCase);

    private string? _selected;

    public MainWindow(ModuleManager modules, Configuration config, Action openSettings)
        : base("Buddy's Buddy###BuddysBuddyMain")
    {
        _modules = modules;
        _config = config;
        _openSettings = openSettings;
        _selected = config.LastSelectedModule;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(760, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var loaded = _modules.LoadedModules.ToList();

        DrawSidebar(loaded);
        ImGui.SameLine();
        DrawContent(loaded);
    }

    private void DrawSidebar(IReadOnlyList<IModule> loaded)
    {
        using var child = ImRaii.Child("##modulelist", new Vector2(180, 0), true);
        if (!child)
            return;

        if (loaded.Count == 0)
        {
            ImGui.TextWrapped("No modules enabled.");
        }
        else
        {
            foreach (var module in loaded)
            {
                if (ImGui.Selectable(module.Name, IsSelected(module)))
                {
                    _selected = module.InternalName;
                    _config.LastSelectedModule = _selected;
                    _config.Save();
                }

                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(module.Description))
                    ImGui.SetTooltip(module.Description);
            }
        }

        // Pin the settings button to the bottom of the sidebar.
        var buttonHeight = ImGui.GetFrameHeight();
        var remaining = ImGui.GetContentRegionAvail().Y - buttonHeight;
        if (remaining > 0)
            ImGui.Dummy(new Vector2(0, remaining));

        if (ImGui.Button("Settings", new Vector2(-1, 0)))
            _openSettings();
    }

    private void DrawContent(IReadOnlyList<IModule> loaded)
    {
        using var child = ImRaii.Child("##modulepane", new Vector2(0, 0), false);
        if (!child)
            return;

        var module = loaded.FirstOrDefault(IsSelected) ?? loaded.FirstOrDefault();
        if (module is null)
        {
            ImGui.TextWrapped(
                "Nothing to show yet. Open Settings to enable a module, or add one under " +
                "src/BuddysBuddy/Modules/.");
            return;
        }

        ImGui.TextUnformatted(module.Name);
        if (!string.IsNullOrEmpty(module.Description))
            ImGui.TextDisabled(module.Description);

        ImGui.Separator();
        ImGui.Spacing();

        if (_drawErrors.TryGetValue(module.InternalName, out var error))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This module failed while drawing:");
            ImGui.TextWrapped(error);
            if (ImGui.Button("Retry"))
                _drawErrors.Remove(module.InternalName);
            return;
        }

        _modules.DrawModule(module, ex =>
        {
            _drawErrors[module.InternalName] = ex.ToString();
            Services.DalamudServices.Log.Error(ex, "Module {Name} threw while drawing.", module.InternalName);
        });
    }

    private bool IsSelected(IModule module)
        => string.Equals(module.InternalName, _selected, StringComparison.OrdinalIgnoreCase);
}
