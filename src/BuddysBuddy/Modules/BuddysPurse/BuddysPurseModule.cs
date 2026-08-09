using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using Dalamud.Bindings.ImGui;

namespace BuddysBuddy.Modules.BuddysPurse;

/// <summary>
/// Gil, everywhere it is.
/// </summary>
/// <remarks>
/// Another pure consumer of <see cref="ItemSourceRegistry"/>: character, retainers and free company
/// all already report their gil alongside their items. The only thing this adds is arithmetic and a
/// baseline to measure against.
///
/// A retainer that has never been opened cannot be read, so it is simply absent rather than counted
/// as zero — an unknown is not the same as nothing.
/// </remarks>
public sealed class BuddysPurseModule : ModuleBase
{
    public override string Name => "Buddy's Purse";

    public override string InternalName => "purse";

    public override string Description => "Totals your gil across character, retainers and free company.";

    public override int Order => 10;

    private static readonly Vector4 Stale = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Gain = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Loss = new(1f, 0.5f, 0.5f, 1f);

    private BuddysPurseConfig _settings = null!;

    protected override void OnLoad()
        => _settings = Config.GetModuleConfig<BuddysPurseConfig>(InternalName);

    /// <summary>Every source holding gil, richest first.</summary>
    private static List<IItemSource> Holders()
        => ItemSourceRegistry.Sources()
                             .Where(s => s.Gil > 0)
                             .OrderByDescending(s => s.Gil)
                             .ToList();

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        var holders = Holders();
        var total = holders.Sum(s => s.Gil);

        if (holders.Count == 0)
        {
            ImGui.TextDisabled("No gil recorded yet — log in, and visit a retainer or the FC chest.");
            return;
        }

        ImGui.TextUnformatted($"{total:N0} gil across {holders.Count} location(s)");
        DrawBaseline(total);

        var stale = holders.Count(s => !s.IsLive);
        if (stale > 0)
            ImGui.TextColored(Stale, $"{stale} of these were not read this session — treat them as approximate.");

        ImGui.Spacing();

        if (!ImGui.BeginTable("##purse", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260)))
            return;

        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Read", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var source in holders)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(source.Label);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(source.Group);

            ImGui.TableNextColumn();
            if (source.IsLive)
                ImGui.TextUnformatted(source.Gil.ToString("N0"));
            else
                ImGui.TextColored(Stale, source.Gil.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.TextDisabled(source.LastSeen is { } at ? at.ToString("MM-dd HH:mm") : "never");
        }

        ImGui.EndTable();
    }

    private void DrawBaseline(long total)
    {
        if (_settings.BaselineAt is null)
        {
            if (ImGui.SmallButton("Set baseline"))
                SetBaseline(total);

            ImGui.SameLine();
            ImGui.TextDisabled("Set a baseline to track gil earned from here on.");
            return;
        }

        var delta = total - _settings.BaselineGil;
        var since = _settings.BaselineAt.Value.ToString("yyyy-MM-dd HH:mm");

        if (delta == 0)
            ImGui.TextDisabled($"No change since {since}.");
        else
            ImGui.TextColored(delta > 0 ? Gain : Loss, $"{delta:+#,##0;-#,##0} since {since}");

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset baseline"))
            SetBaseline(total);
    }

    private void SetBaseline(long total)
    {
        _settings.BaselineGil = total;
        _settings.BaselineAt = DateTime.Now;
        Config.Save();
    }

    public override void DrawSettings()
    {
        ImGui.TextDisabled("Buddy's Purse reads what the other modules have already seen.");
        ImGui.TextDisabled("A retainer you have never opened cannot be counted, and is left out");
        ImGui.TextDisabled("rather than counted as zero.");

        ImGui.Spacing();

        if (_settings.BaselineAt is { } at)
        {
            ImGui.TextUnformatted($"Baseline: {_settings.BaselineGil:N0} gil, set {at:yyyy-MM-dd HH:mm}");
            if (ImGui.Button("Clear baseline"))
            {
                _settings.BaselineAt = null;
                _settings.BaselineGil = 0;
                Config.Save();
            }
        }
        else
        {
            ImGui.TextDisabled("No baseline set.");
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();
        var holders = Holders();
        var total = holders.Sum(s => s.Gil);

        if (trimmed.Equals("baseline", StringComparison.OrdinalIgnoreCase))
        {
            SetBaseline(total);
            Print($"Baseline set at {total:N0} gil.");
            return true;
        }

        if (trimmed.Length > 0)
            return false;

        if (holders.Count == 0)
        {
            Print("No gil recorded yet.");
            return true;
        }

        var stale = holders.Count(s => !s.IsLive);
        var note = stale == 0 ? string.Empty : $" ({stale} not read this session)";
        Print($"{total:N0} gil across {holders.Count} location(s){note}.");

        foreach (var source in holders.Take(10))
            Print($"  {source.Group} · {source.Label}: {source.Gil:N0}{(source.IsLive ? "" : " *")}");

        if (_settings.BaselineAt is { } at)
            Print($"{total - _settings.BaselineGil:+#,##0;-#,##0} since {at:yyyy-MM-dd HH:mm}.");

        return true;
    }
}

/// <summary>Persisted settings for <see cref="BuddysPurseModule"/>.</summary>
public sealed class BuddysPurseConfig : IModuleConfig
{
    /// <summary>Total gil when the baseline was taken.</summary>
    public long BaselineGil { get; set; }

    /// <summary>When the baseline was taken, or null if there is none.</summary>
    public DateTime? BaselineAt { get; set; }
}
