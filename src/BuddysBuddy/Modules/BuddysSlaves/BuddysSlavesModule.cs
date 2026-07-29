using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace BuddysBuddy.Modules.BuddysSlaves;

/// <summary>
/// Tallies every retainer's inventory and withdraws a requested number of units of one item.
/// </summary>
/// <remarks>
/// Only the retainer open at a bell is readable, so both the tally and the withdrawal work across
/// visits: the tally accumulates per retainer, and a withdrawal request survives closing one
/// retainer and opening the next, draining them in bell order until the requested amount is met.
/// </remarks>
public sealed class BuddysSlavesModule : ModuleBase
{
    public override string Name => "Buddy's Slaves";

    public override string InternalName => "slaves";

    public override string Description => "Tallies all retainers and withdraws a set number of an item.";

    public override int Order => 30;

    private enum Step
    {
        /// <summary>No stacks reachable — waiting for a retainer holding the item to be opened.</summary>
        Waiting,

        Take,
        Confirm,
        Verify,
    }

    /// <summary>An outstanding "take N of this item" request, which outlives any one retainer.</summary>
    private sealed class Request
    {
        public required RetainerItemKey Key { get; init; }

        public required string Name { get; init; }

        public required int Requested { get; init; }

        public int Taken { get; set; }

        public int Remaining => Math.Max(0, Requested - Taken);
    }

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(5);

    private BuddysSlavesConfig _settings = null!;
    private readonly RetainerTally _tally = new();

    private Request? _request;
    private Step _step = Step.Waiting;
    private DateTime _nextStepAt;
    private DateTime _stepDeadline;
    private int _pendingQuantity;
    private int _countBefore;
    private string _status = "Idle.";

    private string _filter = string.Empty;
    private ulong _lastActiveId;

    // "Take X" popup state.
    private RetainerItemTotal? _promptItem;
    private int _promptAmount = 1;

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysSlavesConfig>(InternalName);
        Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnUnload()
    {
        Framework.Update -= OnFrameworkUpdate;
        _request = null;
        _tally.Clear();
    }

    public bool IsWithdrawing => _request is not null;

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < _nextStepAt)
            return;

        _nextStepAt = DateTime.UtcNow.AddMilliseconds(
            IsWithdrawing ? Math.Max(BuddysSlavesConfig.MinDelayMs, _settings.DelayMs) : _settings.ScanIntervalMs);

        try
        {
            var active = Retainers.GetActive();
            var activeId = active?.Id ?? 0;

            // A different retainer opening invalidates its cached contents.
            if (activeId != _lastActiveId)
            {
                if (activeId != 0 && _settings.RescanOnOpen)
                    _tally.Invalidate(activeId);

                _lastActiveId = activeId;

                // A paused request can resume the moment a new retainer is available.
                if (IsWithdrawing && _step == Step.Waiting)
                    _step = Step.Take;
            }

            _tally.Refresh();

            if (IsWithdrawing)
                StepWithdrawing();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Buddy's Slaves threw while running; stopping.");
            StopWithdrawing("Stopped after an error — see the Dalamud log.");
        }
    }

    // ── Withdrawal ───────────────────────────────────────────────────────────

    private void StartWithdrawing(RetainerItemTotal item, int amount)
    {
        if (IsWithdrawing || amount <= 0)
            return;

        _request = new Request { Key = item.Key, Name = item.Name, Requested = amount };
        _step = Step.Take;
        _nextStepAt = DateTime.UtcNow;
        SetStatus($"Taking {amount:N0} × {item.Name}…");
    }

    public void StopWithdrawing(string reason)
    {
        if (_request is null)
            return;

        var request = _request;
        _request = null;
        _step = Step.Waiting;
        SetStatus(reason);

        var active = Retainers.GetActive();
        if (active is not null)
            _tally.Invalidate(active.Value.Id);

        _tally.Refresh();

        if (_settings.AnnounceInChat)
            Print($"{reason} Took {request.Taken:N0} of {request.Requested:N0} {request.Name}.");
    }

    private void StepWithdrawing()
    {
        var request = _request!;

        if (request.Remaining == 0)
        {
            StopWithdrawing($"Done — took {request.Taken:N0} × {request.Name}.");
            return;
        }

        if (!Retainers.IsInventoryOpen())
        {
            _step = Step.Waiting;
            SetStatus($"{request.Name}: {request.Taken:N0}/{request.Requested:N0} — open the next retainer.");
            return;
        }

        switch (_step)
        {
            case Step.Waiting:
            case Step.Take:
                StepTake(request);
                break;
            case Step.Confirm:
                StepConfirm();
                break;
            case Step.Verify:
                StepVerify(request);
                break;
        }
    }

    private void StepTake(Request request)
    {
        var stacks = RetainerTally.FindLiveStacks(request.Key);
        if (stacks.Count == 0)
        {
            // This retainer is exhausted; wait for the next one rather than abandoning the request.
            var elsewhere = _tally.RemainingElsewhere(request.Key, Retainers.GetActive()?.Id);
            if (elsewhere.Count == 0 && _settings.StopWhenNoneLeft)
            {
                StopWithdrawing($"Stopped — no more {request.Name} in any scanned retainer.");
                return;
            }

            _step = Step.Waiting;
            var next = elsewhere.FirstOrDefault();
            SetStatus(next is null
                ? $"{request.Name}: {request.Taken:N0}/{request.Requested:N0} — open a retainer holding more."
                : $"{request.Name}: {request.Taken:N0}/{request.Requested:N0} — next in {next.RetainerName}.");
            return;
        }

        if (!PlayerInventory.TryFindFreeSlot(out var dstBag, out var dstSlot))
        {
            StopWithdrawing($"Stopped — inventory is full, {request.Remaining:N0} still wanted.");
            return;
        }

        var source = stacks[0];

        // Never take more than was asked for: a partial stack is confirmed at exactly the remainder.
        _pendingQuantity = Math.Min(request.Remaining, source.Quantity);
        _countBefore = PlayerInventory.CountItem(request.Key.ItemId, request.Key.HighQuality);

        Retainers.MoveOut(source.Page, source.Slot, dstBag, dstSlot);

        _step = Step.Confirm;
        _stepDeadline = DateTime.UtcNow + PromptTimeout;
        SetStatus($"{request.Name}: confirming {_pendingQuantity:N0}…");
    }

    private void StepConfirm()
    {
        if (QuantityPrompt.IsOpen())
        {
            if (QuantityPrompt.Confirm(_pendingQuantity))
            {
                _step = Step.Verify;
                _stepDeadline = DateTime.UtcNow + VerifyTimeout;
            }

            return;
        }

        // No prompt yet, or this move did not raise one. Verification settles which.
        if (DateTime.UtcNow <= _stepDeadline)
            return;

        _step = Step.Verify;
        _stepDeadline = DateTime.UtcNow + VerifyTimeout;
    }

    private void StepVerify(Request request)
    {
        var gained = PlayerInventory.CountItem(request.Key.ItemId, request.Key.HighQuality) - _countBefore;

        if (gained >= _pendingQuantity)
        {
            request.Taken += gained;
            _step = Step.Take;
            SetStatus($"{request.Name}: {request.Taken:N0}/{request.Requested:N0} taken…");
            return;
        }

        if (gained > 0 && DateTime.UtcNow > _stepDeadline)
        {
            request.Taken += gained;
            _step = Step.Take;
            Log.Warning("Expected {Expected} of {Item} but {Gained} arrived.", _pendingQuantity, request.Name, gained);
            return;
        }

        if (DateTime.UtcNow > _stepDeadline)
            StopWithdrawing($"Stopped — {request.Name} never arrived in your inventory.");
    }

    private void SetStatus(string status)
    {
        _status = status;
        Log.Debug("Buddy's Slaves: {Status}", status);
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        var open = Retainers.IsInventoryOpen();
        var active = Retainers.GetActive();

        DrawScanState(open, active);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawTotals(open);
        DrawTakePopup();

        ImGui.Spacing();
        ImGui.TextUnformatted(_status);

        if (!IsWithdrawing)
            return;

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
            StopWithdrawing("Cancelled.");
    }

    private void DrawScanState(bool open, RetainerInfo? active)
    {
        var all = Retainers.ListInDisplayOrder();

        if (all.Count == 0)
        {
            ImGui.TextDisabled("No retainers known yet — visit a bell.");
            return;
        }

        var scanned = all.Count(r => _tally.HasScanned(r.Id));

        if (scanned == all.Count)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                $"All {all.Count} retainers scanned — the tally below is complete.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                $"Scanned {scanned} of {all.Count} retainers — this tally is INCOMPLETE.");
            ImGui.TextDisabled("Open each retainer's inventory once to include it.");
        }

        ImGui.Spacing();

        if (ImGui.BeginTable("##retainers", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Scanned", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();

            foreach (var retainer in all)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var isActive = active?.Id == retainer.Id && open;
                if (isActive)
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"{retainer.Name} (open)");
                else
                    ImGui.TextUnformatted(retainer.Name);

                ImGui.TableNextColumn();
                ImGui.TextDisabled(retainer.ItemCount.ToString());

                ImGui.TableNextColumn();
                var at = _tally.ScannedAt(retainer.Id);
                if (at is null)
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "never");
                else
                    ImGui.TextDisabled(at.Value.ToString("HH:mm"));
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Rescan all"))
        {
            _tally.Clear();
            _tally.Refresh();
            SetStatus("Tally reset.");
        }
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

        if (!ImGui.BeginTable("##slavetally", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 130);
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
            ImGui.TextDisabled(item.Holdings.Count == 1
                ? item.Holdings[0].RetainerName
                : $"{item.Holdings.Count} retainers");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n",
                    item.Holdings.Select(h => $"{h.RetainerName}: {h.Quantity:N0}")));

            ImGui.TableNextColumn();
            using var id = ImRaii.PushId((int)item.Key.ItemId + (item.Key.HighQuality ? 1 << 24 : 0));

            using (ImRaii.Disabled(!open || IsWithdrawing))
            {
                if (ImGui.SmallButton("Take X") && open && !IsWithdrawing)
                {
                    _promptItem = item;
                    _promptAmount = Math.Min(item.Quantity, _settings.LastAmount <= 0 ? item.Quantity : _settings.LastAmount);
                    ImGui.OpenPopup("##takex");
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawTakePopup()
    {
        if (_promptItem is null)
            return;

        // The popup is opened from inside the table, so it is drawn against the same id stack.
        using var id = ImRaii.PushId((int)_promptItem.Key.ItemId + (_promptItem.Key.HighQuality ? 1 << 24 : 0));

        if (!ImGui.BeginPopup("##takex"))
            return;

        var item = _promptItem;

        ImGui.TextUnformatted(item.Key.HighQuality ? $"{item.Name} (HQ)" : item.Name);
        ImGui.TextDisabled($"{item.Quantity:N0} across {item.Holdings.Count} retainer(s)");
        ImGui.Separator();

        ImGui.SetNextItemWidth(140);
        ImGui.InputInt("Amount", ref _promptAmount);
        _promptAmount = Math.Clamp(_promptAmount, 1, Math.Max(1, item.Quantity));

        ImGui.Spacing();
        if (ImGui.Button("All"))
            _promptAmount = item.Quantity;

        ImGui.SameLine();
        if (ImGui.Button("Take"))
        {
            _settings.LastAmount = _promptAmount;
            Config.Save();
            StartWithdrawing(item, _promptAmount);
            _promptItem = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _promptItem = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Taken from retainers top to bottom.");

        ImGui.EndPopup();
    }

    public override void DrawSettings()
    {
        var delay = _settings.DelayMs;
        if (ImGui.SliderInt("Delay between withdrawals (ms)", ref delay, BuddysSlavesConfig.MinDelayMs, 1500))
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

        var rescan = _settings.RescanOnOpen;
        if (ImGui.Checkbox("Re-read a retainer each time it is opened", ref rescan))
        {
            _settings.RescanOnOpen = rescan;
            Config.Save();
        }

        var stop = _settings.StopWhenNoneLeft;
        if (ImGui.Checkbox("Stop when no scanned retainer holds any more", ref stop))
        {
            _settings.StopWhenNoneLeft = stop;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off keeps the request waiting so you can open an unscanned retainer.");

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
            var totals = _tally.Totals;
            Print($"{totals.Count} distinct item(s), {totals.Sum(t => t.Quantity):N0} units across " +
                  $"{_tally.ScannedCount} scanned retainer(s).");
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
                Print($"Tally reset — {_tally.ScannedCount} retainer(s) visible.");
                return true;

            case "find":
                if (split.Length < 2)
                {
                    Print("Usage: /bb slaves find <item name>");
                    return true;
                }

                ReportMatches(split[1]);
                return true;

            default:
                return false;
        }
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
            var where = string.Join(", ", m.Holdings.Select(h => $"{h.RetainerName} {h.Quantity:N0}"));
            Print($"{m.Name}{(m.Key.HighQuality ? " (HQ)" : "")}: {m.Quantity:N0} — {where}");
        }
    }
}

/// <summary>Persisted settings for <see cref="BuddysSlavesModule"/>.</summary>
public sealed class BuddysSlavesConfig : IModuleConfig
{
    public const int MinDelayMs = 100;

    public int DelayMs { get; set; } = 300;

    public int ScanIntervalMs { get; set; } = 400;

    public bool RescanOnOpen { get; set; } = true;

    public bool StopWhenNoneLeft { get; set; } = false;

    public bool AnnounceInChat { get; set; } = true;

    /// <summary>Remembered amount, so repeat withdrawals of the same size are quick.</summary>
    public int LastAmount { get; set; } = 0;
}
