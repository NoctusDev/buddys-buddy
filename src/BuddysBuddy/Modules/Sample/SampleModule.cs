using System;
using BuddysBuddy.Config;
using Dalamud.Bindings.ImGui;

namespace BuddysBuddy.Modules.Sample;

/// <summary>
/// Reference module — copy this folder to start a new one.
/// </summary>
/// <remarks>
/// Demonstrates the full surface a module can use: persisted settings, a main pane, a settings
/// pane, a subcommand, and framework subscriptions acquired in <see cref="OnLoad"/> and released in
/// <see cref="OnUnload"/>. Nothing here is required — a module can be five lines.
/// </remarks>
public sealed class SampleModule : ModuleBase
{
    public override string Name => "Sample";

    public override string InternalName => "sample";

    public override string Description => "A worked example. Copy it to start a new module.";

    public override int Order => 100;

    private SampleConfig _settings = null!;
    private DateTime _loadedAt;
    private int _clicks;

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<SampleConfig>(InternalName);
        _loadedAt = DateTime.Now;

        // Anything subscribed here must be unsubscribed in OnUnload.
        Framework.Update += OnFrameworkUpdate;

        if (_settings.GreetOnLoad)
            Print("loaded.");
    }

    protected override void OnUnload()
    {
        Framework.Update -= OnFrameworkUpdate;
        _clicks = 0;
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Per-frame work goes here. Keep it cheap — this runs on the game thread every tick.
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Loaded {(DateTime.Now - _loadedAt).TotalSeconds:F0}s ago.");
        ImGui.Spacing();

        if (ImGui.Button("Click me"))
            _clicks++;

        ImGui.SameLine();
        ImGui.TextUnformatted($"{_clicks} click(s)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "This pane is drawn by SampleModule.Draw(). Copy src/BuddysBuddy/Modules/Sample/ to " +
            "Modules/<YourModule>/, rename the class and InternalName, and it appears in this list " +
            "on the next build — no registration needed.");
    }

    public override void DrawSettings()
    {
        var greet = _settings.GreetOnLoad;
        if (ImGui.Checkbox("Announce in chat when loaded", ref greet))
        {
            _settings.GreetOnLoad = greet;
            Config.Save();
        }

        var label = _settings.Label;
        if (ImGui.InputText("Label", ref label, 64))
        {
            _settings.Label = label;
            Config.Save();
        }
    }

    public override bool HandleCommand(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Print($"hello. Label is \"{_settings.Label}\".");
            return true;
        }

        if (args.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _clicks = 0;
            Print("click counter reset.");
            return true;
        }

        // Unrecognised — let the host print help.
        return false;
    }
}

/// <summary>Persisted settings for <see cref="SampleModule"/>.</summary>
public sealed class SampleConfig : IModuleConfig
{
    public bool GreetOnLoad { get; set; } = false;

    public string Label { get; set; } = "buddy";
}
