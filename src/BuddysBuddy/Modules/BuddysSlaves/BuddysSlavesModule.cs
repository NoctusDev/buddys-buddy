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
/// Tallies every retainer and withdraws requested amounts of one or more items.
/// </summary>
/// <remarks>
/// Only the retainer open at a bell is readable, so both the tally and the withdrawal work across
/// visits. A withdrawal is always a queue — a single "take X" is simply a queue of one — and the
/// queue drains greedily: everything the open retainer can supply is taken before waiting for the
/// next retainer, so each retainer is visited once rather than once per item.
/// </remarks>
public sealed class BuddysSlavesModule : ModuleBase
{
    public override string Name => "Buddy's Slaves";

    public override string InternalName => "slaves";

    public override string Description => "Tallies all retainers and withdraws a shopping list of items.";

    public override int Order => 30;

    private enum Step
    {
        /// <summary>Nothing reachable here — waiting for a retainer holding something still wanted.</summary>
        Waiting,

        Take,
        Confirm,
        Verify,
    }

    /// <summary>One line of the shopping list.</summary>
    private sealed class QueueEntry
    {
        public required RetainerItemKey Key { get; init; }

        public required string Name { get; init; }

        public int Requested { get; set; }

        public int Taken { get; set; }

        public int Remaining => Math.Max(0, Requested - Taken);

        public bool Done => Remaining == 0;
    }

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(5);

    private BuddysSlavesConfig _settings = null!;
    private readonly RetainerTally _tally = new();

    private readonly List<QueueEntry> _queue = [];
    private bool _running;

    /// <summary>Items the open retainer has run out of; cleared whenever a different one is opened.</summary>
    private readonly HashSet<RetainerItemKey> _exhaustedHere = [];

    private QueueEntry? _current;
    private Step _step = Step.Waiting;
    private DateTime _nextStepAt;
    private DateTime _stepDeadline;
    private int _pendingQuantity;
    private int _countBefore;
    private string _status = "Idle.";

    private string _filter = string.Empty;
    private ulong _lastActiveId;

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
        _running = false;
        _queue.Clear();
        _tally.Clear();
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < _nextStepAt)
            return;

        _nextStepAt = DateTime.UtcNow.AddMilliseconds(
            _running ? Math.Max(BuddysSlavesConfig.MinDelayMs, _settings.DelayMs) : _settings.ScanIntervalMs);

        try
        {
            var activeId = Retainers.GetActive()?.Id ?? 0;

            // Snapshots are never dropped here: GetActiveRetainer reports a retainer while the bell
            // list is merely being browsed, and Refresh replaces contents that actually changed.
            if (activeId != _lastActiveId)
            {
                _lastActiveId = activeId;

                // A new retainer may hold what the last one ran out of, so give every entry
                // another chance and resume a paused queue.
                _exhaustedHere.Clear();

                if (_running && _step == Step.Waiting)
                    _step = Step.Take;
            }

            _tally.Refresh();

            if (_running)
                StepQueue();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Buddy's Slaves threw while running; stopping.");
            StopQueue("Stopped after an error — see the Dalamud log.");
        }
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    private void AddToQueue(RetainerItemTotal item, int amount)
    {
        if (amount <= 0)
            return;

        var existing = _queue.FirstOrDefault(e => e.Key.Equals(item.Key));
        if (existing is not null)
        {
            existing.Requested += amount;
            SetStatus($"{existing.Name}: now wanting {existing.Requested:N0}.");
            return;
        }

        _queue.Add(new QueueEntry { Key = item.Key, Name = item.Name, Requested = amount });
        SetStatus($"Queued {amount:N0} × {item.Name}.");
    }

    private void StartQueue()
    {
        if (_running || _queue.Count == 0)
            return;

        if (_queue.All(e => e.Done))
        {
            SetStatus("Everything queued is already satisfied.");
            return;
        }

        _running = true;
        _exhaustedHere.Clear();
        _step = Step.Take;
        _nextStepAt = DateTime.UtcNow;
        SetStatus($"Working through {_queue.Count(e => !e.Done)} item(s)…");
    }

    public void StopQueue(string reason)
    {
        if (!_running)
            return;

        _running = false;
        _current = null;
        _step = Step.Waiting;
        SetStatus(reason);

        _tally.Refresh();

        if (!_settings.AnnounceInChat)
            return;

        var done = _queue.Count(e => e.Done);
        Print($"{reason} {done}/{_queue.Count} item(s) complete, " +
              $"{_queue.Sum(e => e.Taken):N0} unit(s) taken.");
    }

    private void StepQueue()
    {
        if (_queue.Count == 0 || _queue.All(e => e.Done))
        {
            StopQueue("Queue complete.");
            return;
        }

        if (!Retainers.IsInventoryOpen())
        {
            _step = Step.Waiting;
            SetStatus(DescribeWait());
            return;
        }

        switch (_step)
        {
            case Step.Waiting:
            case Step.Take:
                StepTake();
                break;
            case Step.Confirm:
                StepConfirm();
                break;
            case Step.Verify:
                StepVerify();
                break;
        }
    }

    private void StepTake()
    {
        // Take everything this retainer can supply before moving on: pick the first outstanding
        // entry it has not already run out of.
        var entry = _queue.FirstOrDefault(e => !e.Done && !_exhaustedHere.Contains(e.Key));
        if (entry is null)
        {
            _step = Step.Waiting;
            SetStatus(DescribeWait());
            return;
        }

        var stacks = RetainerTally.FindLiveStacks(entry.Key);
        if (stacks.Count == 0)
        {
            // This retainer has no more of this item; try the next entry on the next tick.
            _exhaustedHere.Add(entry.Key);
            return;
        }

        if (!PlayerInventory.TryFindFreeSlot(out var dstBag, out var dstSlot))
        {
            StopQueue("Stopped — your inventory is full.");
            return;
        }

        var source = stacks[0];

        _current = entry;
        _pendingQuantity = Math.Min(entry.Remaining, source.Quantity);
        _countBefore = PlayerInventory.CountItem(entry.Key.ItemId, entry.Key.HighQuality);

        Retainers.MoveOut(source.Page, source.Slot, dstBag, dstSlot);

        _step = Step.Confirm;
        _stepDeadline = DateTime.UtcNow + PromptTimeout;
        SetStatus($"{entry.Name}: taking {_pendingQuantity:N0}…");
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

        if (DateTime.UtcNow <= _stepDeadline)
            return;

        _step = Step.Verify;
        _stepDeadline = DateTime.UtcNow + VerifyTimeout;
    }

    private void StepVerify()
    {
        var entry = _current;
        if (entry is null)
        {
            _step = Step.Take;
            return;
        }

        var gained = PlayerInventory.CountItem(entry.Key.ItemId, entry.Key.HighQuality) - _countBefore;

        if (gained >= _pendingQuantity)
        {
            entry.Taken += gained;
            _step = Step.Take;
            SetStatus($"{entry.Name}: {entry.Taken:N0}/{entry.Requested:N0}…");
            return;
        }

        if (gained > 0 && DateTime.UtcNow > _stepDeadline)
        {
            entry.Taken += gained;
            _step = Step.Take;
            Log.Warning("Expected {Expected} of {Item} but {Gained} arrived.", _pendingQuantity, entry.Name, gained);
            return;
        }

        if (DateTime.UtcNow > _stepDeadline)
            StopQueue($"Stopped — {entry.Name} never arrived in your inventory.");
    }

    /// <summary>Explains what the queue is waiting for, naming a retainer when the tally knows one.</summary>
    private string DescribeWait()
    {
        var outstanding = _queue.Where(e => !e.Done).ToList();
        if (outstanding.Count == 0)
            return "Queue complete.";

        var activeId = Retainers.GetActive()?.Id;

        var next = outstanding
            .SelectMany(e => _tally.RemainingElsewhere(e.Key, activeId))
            .OrderBy(h => h.SortIndex)
            .FirstOrDefault();

        var summary = $"{outstanding.Count} item(s) outstanding, {outstanding.Sum(e => e.Remaining):N0} unit(s)";

        return next is null
            ? $"{summary} — open a retainer holding them."
            : $"{summary} — next in {next.RetainerName}.";
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
        DrawTakePanel(open);
        DrawQueue(open);

        ImGui.Spacing();
        ImGui.TextUnformatted(_status);
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

                if (active?.Id == retainer.Id && open)
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

        ImGui.SameLine();
        var queueMode = _settings.QueueMode;
        if (ImGui.Checkbox("Queue mode", ref queueMode))
        {
            _settings.QueueMode = queueMode;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Build a shopping list of several items, then run it.\n" +
                "Each retainer is drained of everything on the list before moving to the next.");
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
                new Vector2(0, 260)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("##take", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var label = _settings.QueueMode ? "Add" : "Take X";

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

            // Queue mode lets items be added while a run is in progress; direct mode does not.
            using (ImRaii.Disabled(!_settings.QueueMode && (_running || !open)))
            {
                if (ImGui.SmallButton(label))
                {
                    _promptItem = item;
                    _promptAmount = _settings.LastAmount <= 0
                        ? item.Quantity
                        : Math.Min(item.Quantity, _settings.LastAmount);
                }
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The amount panel shown after picking an item.
    /// </summary>
    /// <remarks>
    /// Inline rather than an ImGui popup: popups are keyed by the id stack at the moment they are
    /// opened, so one opened from inside a table cell never matches a <c>BeginPopup</c> outside the
    /// table and silently fails to render.
    /// </remarks>
    private void DrawTakePanel(bool open)
    {
        if (_promptItem is null)
            return;

        var item = _promptItem;
        var queueMode = _settings.QueueMode;

        ImGui.Spacing();
        using var child = ImRaii.Child("##takepanel", new Vector2(0, 124), true);
        if (!child)
            return;

        ImGui.TextUnformatted(item.Key.HighQuality ? $"{item.Name} (HQ)" : item.Name);
        ImGui.TextDisabled($"{item.Quantity:N0} available across {item.Holdings.Count} retainer(s)");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Amount", ref _promptAmount);
        _promptAmount = Math.Clamp(_promptAmount, 1, Math.Max(1, item.Quantity));

        ImGui.SameLine();
        if (ImGui.SmallButton("All"))
            _promptAmount = item.Quantity;

        ImGui.Spacing();

        var canAct = queueMode || (open && !_running);

        using (ImRaii.Disabled(!canAct))
        {
            if (ImGui.Button(queueMode ? "Add to list" : "Take", new Vector2(120, 0)) && canAct)
            {
                _settings.LastAmount = _promptAmount;
                Config.Save();

                if (queueMode)
                {
                    AddToQueue(item, _promptAmount);
                }
                else
                {
                    // A direct take is just a one-entry queue.
                    _queue.Clear();
                    AddToQueue(item, _promptAmount);
                    StartQueue();
                }

                _promptItem = null;
                return;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _promptItem = null;
            return;
        }

        if (!queueMode && !open)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "Open a retainer first.");
        }
    }

    private void DrawQueue(bool open)
    {
        if (!_settings.QueueMode && _queue.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Shopping list ({_queue.Count(e => e.Done)}/{_queue.Count} complete)");

        if (_queue.Count == 0)
        {
            ImGui.TextDisabled("Empty — add items from the list above.");
            return;
        }

        if (ImGui.BeginTable("##queue", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 160)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var activeId = Retainers.GetActive()?.Id;
            QueueEntry? remove = null;

            foreach (var entry in _queue)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Key.HighQuality ? $"{entry.Name} (HQ)" : entry.Name);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{entry.Taken:N0} / {entry.Requested:N0}");

                ImGui.TableNextColumn();
                if (entry.Done)
                {
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "done");
                }
                else if (_running && ReferenceEquals(entry, _current))
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "taking…");
                }
                else if (_exhaustedHere.Contains(entry.Key))
                {
                    var elsewhere = _tally.RemainingElsewhere(entry.Key, activeId).FirstOrDefault();
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                        elsewhere is null ? "none left" : elsewhere.RetainerName);
                }
                else
                {
                    ImGui.TextDisabled(_running ? "queued" : "waiting");
                }

                ImGui.TableNextColumn();
                using var id = ImRaii.PushId((int)entry.Key.ItemId + (entry.Key.HighQuality ? 1 << 24 : 0));
                if (ImGui.SmallButton("x"))
                    remove = entry;
            }

            ImGui.EndTable();

            if (remove is not null)
                _queue.Remove(remove);
        }

        ImGui.Spacing();

        if (_running)
        {
            if (ImGui.Button("Stop", new Vector2(120, 0)))
                StopQueue("Stopped.");
        }
        else
        {
            using (ImRaii.Disabled(!open || _queue.All(e => e.Done)))
            {
                if (ImGui.Button("Run list", new Vector2(120, 0)) && open)
                    StartQueue();
            }

            if (!open)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "Open a retainer to start.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear list", new Vector2(120, 0)))
        {
            if (_running)
                StopQueue("Stopped.");

            _queue.Clear();
            SetStatus("List cleared.");
        }
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

        var queueMode = _settings.QueueMode;
        if (ImGui.Checkbox("Queue mode", ref queueMode))
        {
            _settings.QueueMode = queueMode;
            Config.Save();
        }

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

            if (_queue.Count > 0)
                Print($"List: {_queue.Count(e => e.Done)}/{_queue.Count} complete.");

            return true;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        switch (split[0].ToLowerInvariant())
        {
            case "run":
            case "start":
                StartQueue();
                return true;

            case "stop":
                if (_running)
                    StopQueue("Stopped.");
                else
                    Print("Not running.");
                return true;

            case "clear":
                _queue.Clear();
                Print("List cleared.");
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

    /// <summary>Build a multi-item shopping list instead of taking one item immediately.</summary>
    public bool QueueMode { get; set; } = false;

    public bool AnnounceInChat { get; set; } = true;

    /// <summary>Remembered amount, so repeat withdrawals of the same size are quick.</summary>
    public int LastAmount { get; set; } = 0;
}
