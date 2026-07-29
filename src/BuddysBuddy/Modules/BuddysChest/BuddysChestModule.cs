using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysChest;

/// <summary>
/// Tallies the whole free company chest and withdraws every unit of a chosen item.
/// </summary>
/// <remarks>
/// The chest loads one compartment at a time, so the tally is accumulated across compartments
/// rather than read in one pass — see <see cref="ChestTally"/>. The UI always shows how many
/// compartments have been seen, so an incomplete count is never mistaken for a complete one.
/// </remarks>
public sealed class BuddysChestModule : ModuleBase
{
    public override string Name => "Buddy's Chest";

    public override string InternalName => "chest";

    public override string Description => "Tallies the free company chest and withdraws whole items.";

    public override int Order => 20;

    private const int StallLimit = 5;

    private BuddysChestConfig _settings = null!;
    private readonly ChestTally _tally = new();

    private ItemKey? _withdrawing;
    private string _withdrawName = string.Empty;
    private DateTime _nextStepAt;
    private int _movedThisRun;
    private int _lastRemaining = -1;
    private int _stalls;
    private string _status = "Idle.";

    private string _filter = string.Empty;
    private bool _wasOpen;

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysChestConfig>(InternalName);
        Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnUnload()
    {
        Framework.Update -= OnFrameworkUpdate;
        _withdrawing = null;
        _tally.Clear();
    }

    public bool IsWithdrawing => _withdrawing is not null;

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        var open = FreeCompanyChest.IsWindowOpen();

        // Contents can change between visits, so a fresh open starts from a clean tally.
        if (open && !_wasOpen && _settings.ResetOnOpen)
            _tally.Clear();

        _wasOpen = open;

        if (!open)
        {
            if (IsWithdrawing)
                StopWithdrawing("Stopped — the chest window closed.");
            return;
        }

        if (DateTime.UtcNow < _nextStepAt)
            return;

        _nextStepAt = DateTime.UtcNow.AddMilliseconds(
            IsWithdrawing ? Math.Max(BuddysChestConfig.MinDelayMs, _settings.DelayMs) : _settings.ScanIntervalMs);

        try
        {
            // Always keep scanning: the user clicking through compartments is what completes the tally.
            _tally.Refresh();

            if (IsWithdrawing)
                StepWithdrawing();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Buddy's Chest threw while running; stopping.");
            StopWithdrawing("Stopped after an error — see the Dalamud log.");
        }
    }

    // ── Withdrawal ───────────────────────────────────────────────────────────

    internal void StartWithdrawing(ItemTotal item)
    {
        if (IsWithdrawing)
            return;

        if (!FreeCompanyChest.IsWindowOpen())
        {
            SetStatus("Open the free company chest first.");
            return;
        }

        _withdrawing = item.Key;
        _withdrawName = item.Name;
        _movedThisRun = 0;
        _lastRemaining = -1;
        _stalls = 0;
        _nextStepAt = DateTime.UtcNow;
        SetStatus($"Taking {item.Name}…");
    }

    public void StopWithdrawing(string reason)
    {
        if (!IsWithdrawing)
            return;

        var key = _withdrawing!.Value;
        _withdrawing = null;
        SetStatus(reason);

        // The compartments we pulled from have changed; force a re-read.
        foreach (var page in FreeCompanyChest.AllPages.ToList())
            if (FreeCompanyChest.IsPageLoaded(page))
                _tally.Invalidate(page);

        _tally.Refresh();

        if (!_settings.AnnounceInChat)
            return;

        var stranded = _tally.UnreachableQuantity(key);
        var note = stranded > 0
            ? $" {stranded} more in compartments that are not open — visit them and run it again."
            : string.Empty;

        Print($"{reason} Took {_movedThisRun} stack(s) of {_withdrawName}.{note}");
    }

    private void StepWithdrawing()
    {
        var key = _withdrawing!.Value;

        var stacks = ChestTally.FindLiveStacks(key);
        if (stacks.Count == 0)
        {
            StopWithdrawing($"Done — no more {_withdrawName} in the open compartments.");
            return;
        }

        // Progress is measured by stacks disappearing, not by the move call, which returns nothing.
        if (_lastRemaining >= 0 && stacks.Count >= _lastRemaining)
        {
            if (++_stalls >= StallLimit)
            {
                StopWithdrawing($"Stopped — {stacks.Count} stack(s) of {_withdrawName} would not move.");
                return;
            }
        }
        else
        {
            _stalls = 0;
        }

        _lastRemaining = stacks.Count;

        if (!FreeCompanyChest.TryFindFreePlayerSlot(out var dstBag, out var dstSlot))
        {
            StopWithdrawing($"Stopped — inventory is full, {stacks.Count} stack(s) left.");
            return;
        }

        var source = stacks[0];
        if (!FreeCompanyChest.MoveOut(source.Page, source.Slot, dstBag, dstSlot))
        {
            StopWithdrawing("Stopped — the chest agent went away.");
            return;
        }

        _movedThisRun++;
        SetStatus($"Taking {_withdrawName} — {_movedThisRun} stack(s) moved, {stacks.Count - 1} to go…");
    }

    private void SetStatus(string status)
    {
        _status = status;
        Log.Debug("Buddy's Chest: {Status}", status);
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        var open = FreeCompanyChest.IsWindowOpen();

        if (!open)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "The free company chest is not open.");
            ImGui.TextWrapped(
                "Open it at an FC chest or estate. Buddy's Chest tallies each compartment as you " +
                "view it — the counts below are whatever has been seen so far.");
            ImGui.Spacing();

            if (ImGui.Button("Try to open the chest"))
                FreeCompanyChest.RequestOpenWindow();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        DrawScanState(open);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawTotals(open);

        ImGui.Spacing();
        ImGui.TextUnformatted(_status);
    }

    private void DrawScanState(bool open)
    {
        var scanned = _tally.ScannedCount;
        var total = _tally.TotalPageCount;

        if (_tally.IsComplete)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                $"All {total} compartments scanned — the tally below is complete.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                $"Scanned {scanned} of {total} compartments — this tally is INCOMPLETE.");

            var missing = string.Join(", ", _tally.UnscannedPages.Select(FreeCompanyChest.PageLabel));
            ImGui.TextWrapped($"Not yet seen: {missing}");
            ImGui.TextDisabled("Click each compartment tab in the chest window to include it.");
        }

        var gil = FreeCompanyChest.GetGil();
        if (gil > 0)
            ImGui.TextDisabled($"FC gil: {gil:N0}");

        ImGui.Spacing();

        using (ImRaii.Disabled(!open))
        {
            if (ImGui.Button("Rescan") && open)
            {
                _tally.Clear();
                _tally.Refresh();
                SetStatus("Tally reset.");
            }
        }

        if (!_settings.EnableAutoTabSwitch)
            return;

        ImGui.SameLine();
        using (ImRaii.Disabled(!open || _tally.IsComplete))
        {
            if (ImGui.Button("Load all compartments") && open)
            {
                for (var i = 0; i < FreeCompanyChest.ItemPages.Length; i++)
                    FreeCompanyChest.RequestPage(i);

                SetStatus("Asked the chest to cycle compartments.");
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Experimental and unverified. If nothing happens, click the tabs by hand.");
    }

    private void DrawTotals(bool open)
    {
        var totals = _tally.Totals;

        if (totals.Count == 0)
        {
            ImGui.TextDisabled("Nothing tallied yet.");
            return;
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##filter", "Search items…", ref _filter, 64);

        var filtered = string.IsNullOrWhiteSpace(_filter)
            ? totals
            : totals.Where(t => t.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ImGui.SameLine();
        ImGui.TextDisabled($"{filtered.Count} of {totals.Count}");

        ImGui.Spacing();

        if (!ImGui.BeginTable("##chesttally", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 320)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("##take", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var item in filtered)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Key.HighQuality ? $"{item.Name} (HQ)" : item.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Quantity.ToString("N0"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{item.Stacks} stack(s)");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(item.Pages.Count == 1
                ? FreeCompanyChest.PageLabel(item.Pages[0])
                : $"{item.Pages.Count} compartments");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", item.Pages.Select(FreeCompanyChest.PageLabel)));

            ImGui.TableNextColumn();
            using var id = ImRaii.PushId((int)item.Key.ItemId + (item.Key.HighQuality ? 1 << 24 : 0));

            using (ImRaii.Disabled(!open || IsWithdrawing))
            {
                if (ImGui.SmallButton("Take all") && open && !IsWithdrawing)
                    StartWithdrawing(item);
            }
        }

        ImGui.EndTable();

        if (!IsWithdrawing)
            return;

        ImGui.Spacing();
        if (ImGui.Button("Stop"))
            StopWithdrawing("Stopped.");
    }

    public override void DrawSettings()
    {
        var delay = _settings.DelayMs;
        if (ImGui.SliderInt("Delay between withdrawals (ms)", ref delay, BuddysChestConfig.MinDelayMs, 1500))
        {
            _settings.DelayMs = delay;
            Config.Save();
        }

        var scan = _settings.ScanIntervalMs;
        if (ImGui.SliderInt("Rescan interval (ms)", ref scan, 100, 2000))
        {
            _settings.ScanIntervalMs = scan;
            Config.Save();
        }

        var reset = _settings.ResetOnOpen;
        if (ImGui.Checkbox("Clear the tally each time the chest opens", ref reset))
        {
            _settings.ResetOnOpen = reset;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Contents can change between visits; clearing avoids showing stale counts.");

        var auto = _settings.EnableAutoTabSwitch;
        if (ImGui.Checkbox("Show the experimental \"load all compartments\" button", ref auto))
        {
            _settings.EnableAutoTabSwitch = auto;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tries to switch compartment tabs for you. Unverified — may do nothing.");

        var announce = _settings.AnnounceInChat;
        if (ImGui.Checkbox("Report the result in chat", ref announce))
        {
            _settings.AnnounceInChat = announce;
            Config.Save();
        }
    }

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Length == 0)
        {
            ReportTally();
            return true;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        switch (split[0].ToLowerInvariant())
        {
            case "stop":
                if (IsWithdrawing)
                    StopWithdrawing("Stopped.");
                else
                    Print("Not running.");
                return true;

            case "rescan":
                _tally.Clear();
                _tally.Refresh();
                Print($"Tally reset — {_tally.ScannedCount}/{_tally.TotalPageCount} compartments visible.");
                return true;

            case "find":
                if (split.Length < 2)
                {
                    Print("Usage: /bb chest find <item name>");
                    return true;
                }

                ReportMatches(split[1]);
                return true;

            default:
                return false;
        }
    }

    private void ReportTally()
    {
        var totals = _tally.Totals;
        var state = _tally.IsComplete
            ? "all compartments scanned"
            : $"{_tally.ScannedCount}/{_tally.TotalPageCount} compartments scanned — INCOMPLETE";

        Print($"{totals.Count} distinct item(s), {totals.Sum(t => t.Quantity):N0} units ({state}).");
    }

    private void ReportMatches(string search)
    {
        var matches = _tally.Totals
            .Where(t => t.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (matches.Count == 0)
        {
            Print($"No match for \"{search}\" in what has been scanned so far.");
            return;
        }

        foreach (var m in matches)
        {
            var where = string.Join(", ", m.Pages.Select(FreeCompanyChest.PageLabel));
            Print($"{m.Name}{(m.Key.HighQuality ? " (HQ)" : "")}: {m.Quantity:N0} in {m.Stacks} stack(s) — {where}");
        }

        if (!_tally.IsComplete)
            Print($"Note: only {_tally.ScannedCount}/{_tally.TotalPageCount} compartments have been scanned.");
    }
}

/// <summary>Persisted settings for <see cref="BuddysChestModule"/>.</summary>
public sealed class BuddysChestConfig : IModuleConfig
{
    public const int MinDelayMs = 100;

    /// <summary>Milliseconds between withdrawals; each move needs a moment to apply.</summary>
    public int DelayMs { get; set; } = 300;

    /// <summary>How often to re-read whichever compartment is currently loaded.</summary>
    public int ScanIntervalMs { get; set; } = 400;

    public bool ResetOnOpen { get; set; } = true;

    public bool EnableAutoTabSwitch { get; set; } = false;

    public bool AnnounceInChat { get; set; } = true;
}
