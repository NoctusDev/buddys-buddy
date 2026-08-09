using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BuddysBuddy.Modules.BuddysStash;

/// <summary>
/// One search box over everything you own.
/// </summary>
/// <remarks>
/// Reads nothing from the game itself. Every other module already keeps a snapshot of somewhere
/// items live; this folds all of them — plus the character's own bags and whatever an earlier
/// session recorded — into a single answer, and is careful to say which parts of that answer are
/// current and which are only remembered.
/// </remarks>
public sealed class BuddysStashModule : ModuleBase
{
    public override string Name => "Buddy's Stash";

    public override string InternalName => "stash";

    public override string Description => "Searches everything you own, everywhere it is.";

    public override int Order => 5;

    private static readonly Vector4 Stale = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Fresh = new(0.4f, 0.9f, 0.4f, 1f);

    private BuddysStashConfig _settings = null!;

    private string _filter = string.Empty;
    private ItemKey? _expanded;

    protected override void OnLoad()
        => _settings = Config.GetModuleConfig<BuddysStashConfig>(InternalName);

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        var sources = ItemSourceRegistry.Sources();
        var entries = StashSearch.Build(sources);

        DrawSourceSummary(sources);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##stashfilter", "Search everything you own…", ref _filter, 64);

        var filtered = StashSearch.Filter(entries, _filter);

        ImGui.SameLine();
        ImGui.TextDisabled($"{filtered.Count} of {entries.Count}");

        ImGui.Spacing();

        if (entries.Count == 0)
        {
            ImGui.TextDisabled(
                "Nothing recorded yet. Open your bags, a retainer or the FC chest and it fills in.");
            return;
        }

        DrawEntries(filtered);
        DrawExpanded(filtered);
    }

    /// <summary>
    /// The honesty banner: how much of the picture is current, and what is missing from it.
    /// </summary>
    private void DrawSourceSummary(IReadOnlyList<IItemSource> sources)
    {
        var live = sources.Count(s => s.IsLive);
        var stale = sources.Where(s => !s.IsLive).ToList();

        if (stale.Count == 0)
        {
            ImGui.TextColored(Fresh, $"{live} location(s), all read this session.");
        }
        else
        {
            ImGui.TextColored(Stale,
                $"{live} location(s) read this session, {stale.Count} remembered from an earlier one.");
            ImGui.TextWrapped(
                "Remembered counts describe how things were, not how they are. Visit the retainer, " +
                "compartment or saddlebag to make it current.");

            if (ImGui.TreeNode($"Not read this session ({stale.Count})###stalesources"))
            {
                foreach (var source in stale)
                {
                    var seen = source.LastSeen is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "never";
                    ImGui.TextDisabled($"{source.Group} · {source.Label} — last seen {seen}");
                }

                ImGui.TreePop();
            }
        }
    }

    private void DrawEntries(List<StashEntry> entries)
    {
        if (!ImGui.BeginTable("##stash", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 320)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using var id = ImRaii.PushId((int)entry.Key.ItemId + (entry.Key.HighQuality ? 1 << 24 : 0));

            // Selecting a row only records the choice; the breakdown is drawn after the table.
            // A popup opened inside a table cell never renders — see knowledge/verified.md.
            var selected = _expanded is { } e && e.Equals(entry.Key);
            if (ImGui.Selectable(entry.Key.HighQuality ? $"{entry.Name} (HQ)" : entry.Name, selected))
                _expanded = selected ? null : entry.Key;

            ImGui.TableNextColumn();
            if (entry.HasStale)
            {
                ImGui.TextColored(Stale, entry.Total.ToString("N0"));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{entry.LiveTotal:N0} confirmed this session, " +
                                     $"{entry.Total - entry.LiveTotal:N0} remembered.");
            }
            else
            {
                ImGui.TextUnformatted(entry.Total.ToString("N0"));
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(entry.Where);
        }

        ImGui.EndTable();
    }

    private void DrawExpanded(List<StashEntry> entries)
    {
        if (_expanded is not { } key)
            return;

        var entry = entries.FirstOrDefault(e => e.Key.Equals(key));
        if (entry is null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(entry.Key.HighQuality ? $"{entry.Name} (HQ)" : entry.Name);

        foreach (var holding in entry.Holdings)
        {
            var seen = holding.LastSeen is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "never";
            var line = $"{holding.Quantity,8:N0}   {holding.Group} · {holding.Label}";

            if (holding.IsLive)
                ImGui.TextUnformatted(line);
            else
                ImGui.TextColored(Stale, $"{line}   (as of {seen})");
        }

        if (ImGui.SmallButton("Close"))
            _expanded = null;
    }

    public override void DrawSettings()
    {
        var max = _settings.ChatResultLimit;
        if (ImGui.SliderInt("Chat results per search", ref max, 1, 20))
        {
            _settings.ChatResultLimit = max;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Remembered locations are stored outside the plugin config, in");
        ImGui.TextDisabled("the plugin's own folder, so they can never break the plugin.");

        if (ImGui.Button("Forget everything remembered"))
        {
            Plugin.Sources.Clear();
            Print("Forgot every remembered location. What is open right now will be read again.");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears the saved snapshots. Anything currently open is re-read immediately.");
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Length == 0)
        {
            ReportSummary();
            return true;
        }

        if (trimmed.Equals("forget", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Sources.Clear();
            Print("Forgot every remembered location.");
            return true;
        }

        ReportMatches(trimmed);
        return true;
    }

    private void ReportSummary()
    {
        var sources = ItemSourceRegistry.Sources();
        var entries = StashSearch.Build(sources);
        var stale = sources.Count(s => !s.IsLive);

        var note = stale == 0 ? string.Empty : $" ({stale} remembered from an earlier session)";
        Print($"{entries.Count:N0} distinct item(s) across {sources.Count} location(s){note}. " +
              "Search with /bb stash <name>.");
    }

    private void ReportMatches(string search)
    {
        var matches = StashSearch.Filter(StashSearch.Build(ItemSourceRegistry.Sources()), search)
                                 .OrderByDescending(e => e.Total)
                                 .Take(Math.Max(1, _settings.ChatResultLimit))
                                 .ToList();

        if (matches.Count == 0)
        {
            Print($"Nothing matching \"{search}\" in anywhere that has been read.");
            return;
        }

        foreach (var entry in matches)
        {
            var name = entry.Key.HighQuality ? $"{entry.Name} (HQ)" : entry.Name;
            var mark = entry.HasStale ? " *" : string.Empty;
            Print($"{name}: {entry.Total:N0}{mark} — {entry.Where}");
        }

        if (matches.Any(m => m.HasStale))
            Print("* includes counts remembered from an earlier session, not confirmed now.");
    }
}

/// <summary>Persisted settings for <see cref="BuddysStashModule"/>.</summary>
public sealed class BuddysStashConfig : IModuleConfig
{
    /// <summary>How many matches <c>/bb stash &lt;name&gt;</c> prints.</summary>
    public int ChatResultLimit { get; set; } = 8;
}
