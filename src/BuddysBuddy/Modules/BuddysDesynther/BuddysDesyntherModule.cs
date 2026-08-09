using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysDesynther;

/// <summary>
/// Desynthesises everything you own that can be desynthesised, and records what it yielded.
/// </summary>
/// <remarks>
/// There is nothing to configure. <c>Item.Desynth &gt; 0</c> is the game's own answer to "can this
/// be desynthesised", and the required level is the item's level — the same <c>skill/required</c>
/// pair the Desynthesis window shows, in the same colours. Rows you cannot do are listed and marked
/// rather than hidden, and skipped by a run.
///
/// The game's Desynthesis window does not need to be open.
/// </remarks>
public sealed class BuddysDesyntherModule : ModuleBase
{
    public override string Name => "Buddy's Desynther";

    public override string InternalName => "desynth";

    public override string Description => "Desynthesises everything you own that can be, and tracks the yields.";

    public override int Order => 40;

    /// <summary>Where one desynthesis is in its request-confirm-collect cycle.</summary>
    private enum Step
    {
        /// <summary>Find the next eligible item and ask for it.</summary>
        Request,

        /// <summary>Wait for the confirmation dialog, then press Desynthesize.</summary>
        Confirm,

        /// <summary>Wait for the source stack to actually shrink, then record what arrived.</summary>
        Collect,

        /// <summary>Wait for the windows to close before starting the next one.</summary>
        Settle,
    }

    /// <summary>
    /// How long to wait for the confirmation dialog before treating the item as refused.
    /// </summary>
    /// <remarks>
    /// Short, because this is the normal path for an item the game will not desynthesise, not an
    /// error case — nothing appears at all. Waiting longer only makes skipping feel like hanging.
    /// </remarks>
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan CollectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long to spend trying to shut the results window before carrying on regardless.
    /// </summary>
    /// <remarks>
    /// Short on purpose. The results window is cosmetic; a run must not end because of it.
    /// </remarks>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the "click again" confirmation stays armed.</summary>
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);

    private static readonly Vector4 Warn = new(1f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 Danger = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Good = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Ready = new(0.95f, 0.9f, 0.4f, 1f);

    private BuddysDesyntherConfig _settings = null!;
    private readonly DesynthLedger _ledger = new();

    private bool _running;

    /// <summary>Which pile the current run is working through; null means everything.</summary>
    private DesynthSource? _runSource;

    private Step _step = Step.Request;
    private DateTime _nextStepAt;
    private DateTime _stepDeadline;

    /// <summary>What the two-click confirmation is armed for, and until when.</summary>
    private DesynthSource? _confirmSource;
    private bool _confirmAll;
    private DateTime _confirmUntil;

    /// <summary>Escalation counter for shutting the results window; see <see cref="Salvage.CloseResult"/>.</summary>
    private int _closeAttempts;

    private ItemKey _current;
    private string _currentName = string.Empty;
    private Dictionary<ItemKey, int> _before = new();

    // The exact slot being desynthesised, watched to tell whether it actually happened.
    private InventoryType _currentBag;
    private ushort _currentSlot;
    private int _sourceCountBefore;

    private int _doneThisRun;
    private int _skippedThisRun;
    private string _status = "Idle.";

    /// <summary>
    /// Items the game would not desynthesise, learned by trying.
    /// </summary>
    /// <remarks>
    /// What exactly makes an item undesynthesisable beyond <c>Item.Desynth &gt; 0</c> is not known —
    /// every rule guessed at so far has been wrong. Rather than guess a fourth time, the runner
    /// tries, notices a refusal, and remembers it for the rest of the session. This is correct
    /// whatever the underlying rule turns out to be.
    /// </remarks>
    private readonly HashSet<ItemKey> _refused = [];

    private List<DesynthItem> _items = [];
    private DateTime _nextScanAt;
    private ItemKey? _ledgerDetail;

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysDesyntherConfig>(InternalName);
        _ledger.Load();
        Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnUnload()
    {
        Framework.Update -= OnFrameworkUpdate;
        Stop("Stopped — the module was switched off.");
        _ledger.Save();
    }

    /// <summary>
    /// The items a run over <paramref name="source"/> would destroy — everything in that pile.
    /// </summary>
    /// <remarks>
    /// Excluded only for reasons the game can be asked about directly: the crafting class has not
    /// reached level 30, so desynthesis is not unlocked for it. Item level is *not* a condition —
    /// being under-levelled affects yield, not permission.
    ///
    /// The refused set is a backstop, not a prediction: anything the game declines for a reason not
    /// modelled here gets skipped once and remembered, so a run can never hang on it.
    /// </remarks>
    private List<DesynthItem> Eligible(DesynthSource? source)
        => _items.Where(i => i.Blocked is null
                             && !_refused.Contains(i.Key)
                             && (source is null || i.Source == source))
                 .ToList();

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        if (now >= _nextScanAt)
        {
            _nextScanAt = now.AddMilliseconds(500);

            try
            {
                _items = DalamudServices.ClientState.IsLoggedIn ? DesynthScan.Scan() : [];
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Buddy's Desynther threw while scanning.");
            }
        }

        if (!_running || now < _nextStepAt)
            return;

        _nextStepAt = now.AddMilliseconds(Math.Max(BuddysDesyntherConfig.MinDelayMs, _settings.DelayMs));

        try
        {
            StepRun();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Buddy's Desynther threw while running; stopping.");
            Stop("Stopped after an error — see the Dalamud log.");
        }
    }

    // ── The runner ───────────────────────────────────────────────────────────

    private void Start(DesynthSource? source)
    {
        if (_running)
            return;

        if (!Salvage.IsAvailable())
        {
            SetStatus("The game is not ready to desynthesise yet.");
            return;
        }

        var pending = Eligible(source);
        if (pending.Count == 0)
        {
            SetStatus("Nothing to do.");
            return;
        }

        _running = true;
        _runSource = source;
        _step = Step.Request;
        _doneThisRun = 0;
        _skippedThisRun = 0;
        _closeAttempts = 0;
        _confirmUntil = default;
        _nextStepAt = DateTime.UtcNow;
        SetStatus($"Desynthesising {pending.Count} item(s)…");
    }

    public void Stop(string reason)
    {
        if (!_running)
            return;

        _running = false;
        _confirmUntil = default;
        SetStatus(reason);
        _ledger.Save();

        if (_settings.AnnounceInChat)
            Print($"{reason} Desynthesised {_doneThisRun} item(s)" +
                  (_skippedThisRun > 0 ? $", skipped {_skippedThisRun} the game would not do" : string.Empty) +
                  ".");
    }

    private void StepRun()
    {
        switch (_step)
        {
            case Step.Request:
                StepRequest();
                break;
            case Step.Confirm:
                StepConfirm();
                break;
            case Step.Collect:
                StepCollect();
                break;
            case Step.Settle:
                StepSettle();
                break;
        }
    }

    private void StepRequest()
    {
        // Re-scan every time: the previous desynthesis shrank a stack and may have moved others.
        _items = DesynthScan.Scan();

        var pending = Eligible(_runSource);
        if (pending.Count == 0)
        {
            Stop($"Done — nothing left.");
            return;
        }

        // A results window left over from the previous item is nudged, not waited on.
        if (Salvage.IsResultOpen())
            Salvage.CloseResult(_closeAttempts++);

        var next = pending[0];
        _current = next.Key;
        _currentName = next.Name;
        _currentBag = next.Bag;
        _currentSlot = next.Slot;
        _before = PlayerInventory.Snapshot(PlayerInventory.Receivable);
        _sourceCountBefore = Salvage.SlotQuantity(next.Bag, next.Slot, next.ItemId);

        if (!Salvage.RequestDesynth(next.Bag, next.Slot, next.ItemId))
        {
            Skip("the game would not accept the request");
            return;
        }

        _step = Step.Confirm;
        _stepDeadline = DateTime.UtcNow + DialogTimeout;
        SetStatus($"{next.Name}: confirming… ({pending.Count} left)");
    }

    private void StepConfirm()
    {
        if (Salvage.IsDialogOpen())
        {
            if (Salvage.ConfirmDialog())
            {
                _step = Step.Collect;
                _stepDeadline = DateTime.UtcNow + CollectTimeout;
            }

            return;
        }

        // The result window can appear without the dialog ever landing on a frame we looked at.
        if (Salvage.IsResultOpen() || SourceConsumed())
        {
            _step = Step.Collect;
            _stepDeadline = DateTime.UtcNow + CollectTimeout;
            return;
        }

        // No dialog and no consumption: the game silently declined this one. Move on.
        if (DateTime.UtcNow > _stepDeadline)
            Skip("the game will not desynthesise it");
    }

    /// <summary>
    /// The one unambiguous signal that a desynthesis happened: that slot holds less than it did.
    /// </summary>
    /// <remarks>
    /// Watches the slot rather than a whole-inventory count, so it works for the armoury chest as
    /// well as the bags.
    /// </remarks>
    private bool SourceConsumed()
        => Salvage.SlotQuantity(_currentBag, _currentSlot, _current.ItemId) < _sourceCountBefore;

    private void StepCollect()
    {
        if (!SourceConsumed())
        {
            // Confirmed, but the item is still there. Nothing to record, so skip rather than
            // invent a yield — and leave any stray dialog for the settle step to clear.
            if (DateTime.UtcNow > _stepDeadline)
                Skip("it was never consumed, so there was nothing to record");

            return;
        }

        // Crystals do not live in a bag slot, so this has to span the crystals container too.
        var after = PlayerInventory.Snapshot(PlayerInventory.Receivable);
        var gained = PlayerInventory.Gained(_before, after);

        // The source can also be a result, which would corrupt its own average.
        gained.Remove(_current);

        _ledger.Record(_current, gained, Salvage.ReadResults());
        _doneThisRun++;

        if (_settings.AnnounceInChat && gained.Count > 0)
            Print($"{_currentName} → " + string.Join(", ",
                gained.Select(kv => $"{kv.Value}x {GameItems.Name(kv.Key.ItemId)}")));

        _closeAttempts = 0;
        Salvage.CloseResult(_closeAttempts++);

        _step = Step.Settle;
        _stepDeadline = DateTime.UtcNow + SettleTimeout;
        SetStatus($"{_doneThisRun} done…");
    }

    /// <summary>
    /// Waits for the windows to clear before the next item.
    /// </summary>
    /// <remarks>
    /// Only the confirmation dialog genuinely blocks the next request. The results window is
    /// cosmetic, so it gets escalating attempts for a couple of seconds and is then ignored — the
    /// first version treated it as fatal and stopped every run after one item.
    /// </remarks>
    private void StepSettle()
    {
        if (!Salvage.IsDialogOpen() && !Salvage.IsResultOpen())
        {
            _step = Step.Request;
            return;
        }

        // A dialog still up after a skip is backed out of, not confirmed.
        if (Salvage.IsDialogOpen())
            Salvage.CancelDialog();

        if (Salvage.IsResultOpen())
            Salvage.CloseResult(_closeAttempts++);

        // Every escalation gets a turn before giving up — the last of them hides the addon outright,
        // so in practice this always clears.
        if (DateTime.UtcNow <= _stepDeadline || _closeAttempts <= Salvage.CloseAttempts)
            return;

        // Out of patience. A stuck confirmation dialog is fatal; a stuck results window is not.
        if (Salvage.IsDialogOpen())
        {
            Stop($"Stopped — the confirmation dialog would not close (open: {Salvage.OpenWindows()}).");
            return;
        }

        Log.Warning("Results window would not close after {Attempts} attempts; carrying on.", _closeAttempts);
        _step = Step.Request;
    }

    /// <summary>
    /// Gives up on the current item and moves to the next, rather than ending the run.
    /// </summary>
    /// <remarks>
    /// An item the game will not desynthesise is an ordinary occurrence, not a failure of the run.
    /// The first version stopped everything on the first such item, which made a "desynthesise
    /// everything" button useless the moment one awkward item was in the pile.
    /// </remarks>
    private void Skip(string why)
    {
        _refused.Add(_current);
        _skippedThisRun++;

        Log.Information("Buddy's Desynther skipped {Item}: {Why}", _currentName, why);

        if (_settings.AnnounceInChat)
            Print($"Skipped {_currentName} — {why}");

        // Via settle, so any half-open dialog is cleared before the next request.
        _closeAttempts = 0;
        _step = Step.Settle;
        _stepDeadline = DateTime.UtcNow + SettleTimeout;
        SetStatus($"{_doneThisRun} done, {_skippedThisRun} skipped…");
    }

    private void SetStatus(string status)
    {
        _status = status;
        Log.Debug("Buddy's Desynther: {Status}", status);
    }

    // ── Value ────────────────────────────────────────────────────────────────

    /// <summary>Expected vendor value of one desynthesis, from the ledger's averages.</summary>
    private static long ExpectedValue(LedgerEntry entry)
    {
        if (entry.Runs == 0)
            return 0;

        long total = 0;

        foreach (var (yieldKey, quantity) in entry.Yield)
        {
            var item = DesynthLedger.FromKey(yieldKey);
            total += (long)(quantity / (double)entry.Runs * GameItems.VendorPrice(item.ItemId));
        }

        return total;
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        if (!Salvage.IsAvailable())
        {
            ImGui.TextColored(Warn, "Not available yet — log in first.");
            return;
        }

        if (ImGui.BeginTabBar("##desynthtabs"))
        {
            if (ImGui.BeginTabItem("Desynthesise"))
            {
                DrawRunner();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"Ledger ({_ledger.TotalRuns})"))
            {
                DrawLedger();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(_status);
    }

    private void DrawRunner()
    {
        DrawLevels();

        ImGui.Spacing();

        if (_items.Count == 0)
        {
            ImGui.TextDisabled("Nothing in your bags or armoury chest can be desynthesised.");
            return;
        }

        var blocked = _items.Where(i => i.Blocked is not null).ToList();
        ImGui.TextUnformatted($"{_items.Count - blocked.Count} you can desynthesise");

        if (blocked.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {blocked.Count} locked");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Desynthesis unlocks per crafting class at level 30.\n\n" +
                                 string.Join("\n", blocked.Select(i => i.Blocked).Distinct().Take(8)));
        }

        var under = _items.Count(i => i.Blocked is null && i.UnderLevelled);
        if (under > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {under} above your skill");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Perfectly desynthesisable — being under-levelled means poorer\n" +
                                 "yields and no skill gain, not a refusal. Nothing is skipped for it.");
        }

        ImGui.Spacing();
        DrawItems();

        ImGui.Spacing();
        DrawRunControls();
    }

    private static void DrawLevels()
    {
        // Locked classes read as a dash rather than a zero — nothing to do with them is possible.
        var parts = Salvage.Crafters.Select(c => Salvage.IsUnlocked(c.Id)
            ? $"{c.Name[..3]} {Salvage.DesynthLevel(c.Id):F0}"
            : $"{c.Name[..3]} —");

        ImGui.TextDisabled(string.Join("   ", parts));

        if (!ImGui.IsItemHovered())
            return;

        var detail = Salvage.Crafters.Select(c =>
            $"{c.Name,-14} class {Salvage.ClassJobLevel(c.Id),3}   desynthesis " +
            (Salvage.IsUnlocked(c.Id) ? $"{Salvage.DesynthLevel(c.Id):F1}" : "locked"));

        ImGui.SetTooltip(
            "Desynthesis unlocks per crafting class at level 30, and its skill is a separate\n" +
            "number from the class level. Which class made an item decides which applies.\n\n" +
            string.Join("\n", detail));
    }

    private void DrawItems()
    {
        if (!ImGui.BeginTable("##desynthitems", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Skill", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Worth it?", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var item in _items)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var name = item.HighQuality ? $"{item.Name} (HQ)" : item.Name;

            if (item.Blocked is { } why)
            {
                ImGui.TextDisabled(name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(why);
            }
            else
            {
                ImGui.TextUnformatted(name);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Quantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextDisabled(DesynthScan.Label(item.Source));

            ImGui.TableNextColumn();

            // Your skill against the item's level. A hint about yield, never a permission check.
            if (item.Blocked is not null)
                ImGui.TextDisabled("locked");
            else
                ImGui.TextColored(item.UnderLevelled ? Warn : Ready, $"{item.Skill:F0}/{item.Required}");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(item.Blocked ??
                                 $"{item.ClassJobName} made it, so your {item.ClassJobName} " +
                                 $"desynthesis skill applies.\n" +
                                 (item.UnderLevelled
                                     ? "You are under-levelled for it: still desynthesisable, but\n" +
                                       "expect poorer yields and no skill gain."
                                     : "At or above this item's level."));

            ImGui.TableNextColumn();
            DrawVerdict(item);
        }

        ImGui.EndTable();
    }

    /// <summary>The whole point, in one cell: is desynthesising this better than selling it?</summary>
    private void DrawVerdict(DesynthItem item)
    {
        var entry = _ledger.Find(item.Key);

        if (entry is null || entry.Runs == 0)
        {
            ImGui.TextDisabled($"{item.VendorPrice:N0} to vendor");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No desynthesis of this recorded yet — the ledger has\n" +
                                 "nothing to compare against. Do a few and it will.");
            return;
        }

        var expected = ExpectedValue(entry);
        var profit = expected - item.VendorPrice;

        ImGui.TextColored(profit >= 0 ? Good : Warn, $"{profit:+#,##0;-#,##0}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Over {entry.Runs} desynthesis/es: yields worth about {expected:N0} gil\n" +
                             $"against a vendor value of {item.VendorPrice:N0}.\n" +
                             (entry.Runs < 10 ? "Only a handful of runs — treat this as a hint." : string.Empty));
    }

    private void DrawRunControls()
    {
        if (_running)
        {
            var skipped = _skippedThisRun > 0 ? $", {_skippedThisRun} skipped" : string.Empty;
            ImGui.TextColored(Warn,
                $"Running — {_doneThisRun} done{skipped}, {Eligible(_runSource).Count} left.");

            if (ImGui.Button("Stop"))
                Stop("Stopped.");

            return;
        }

        if (_refused.Count > 0)
        {
            ImGui.TextDisabled($"{_refused.Count} item(s) the game refused this session are hidden.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tried and declined, so they are not tried again. Reload the plugin\n" +
                                 "or press the button below to give them another go.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Retry them"))
                _refused.Clear();
        }

        foreach (var source in new DesynthSource?[] { DesynthSource.Bags, DesynthSource.Armoury })
        {
            var count = Eligible(source).Count;
            if (count == 0)
                continue;

            DrawRunButton($"Desynthesise {DesynthScan.Label(source!.Value).ToLowerInvariant()} ({count})",
                source, all: false, count);
        }

        var total = Eligible(null).Count;
        if (total > 0 && Eligible(DesynthSource.Bags).Count != total)
            DrawRunButton($"Desynthesise everything ({total})", null, all: true, total);
    }

    /// <summary>
    /// A run button that asks twice.
    /// </summary>
    /// <remarks>
    /// The only guard left, and deliberately the only one: the list above says exactly what will be
    /// destroyed, so a second click is enough. It disarms itself after a few seconds.
    /// </remarks>
    private void DrawRunButton(string label, DesynthSource? source, bool all, int count)
    {
        var armed = DateTime.UtcNow < _confirmUntil && _confirmAll == all && _confirmSource == source;

        if (armed)
        {
            ImGui.TextColored(Danger, $"This destroys {count} item(s). There is no undo.");

            if (ImGui.Button($"Click again — {label}"))
            {
                _confirmUntil = default;
                Start(source);
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _confirmUntil = default;

            return;
        }

        if (!ImGui.Button(label))
            return;

        _confirmSource = source;
        _confirmAll = all;
        _confirmUntil = DateTime.UtcNow + ConfirmWindow;
    }

    private void DrawLedger()
    {
        if (_ledger.TotalRuns == 0)
        {
            ImGui.TextWrapped(
                "Nothing recorded yet. Every desynthesis is measured against your inventory before " +
                "and after — bags and crystals both — and averaged here. After enough runs, the " +
                "\"worth it?\" column starts telling you something real.");
            return;
        }

        ImGui.TextDisabled($"{_ledger.TotalRuns} desynthesis/es recorded");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(DesynthLedger.Location);

        var disagreements = _ledger.Entries.Sum(e => e.Disagreements);
        if (disagreements > 0)
            ImGui.TextColored(Warn,
                $"{disagreements} run(s) where the game's reported yield differed from what arrived. " +
                "The inventory was recorded.");

        ImGui.Spacing();

        if (!ImGui.BeginTable("##ledger", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 280)))
            return;

        ImGui.TableSetupColumn("Desynthesised", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Yield per run", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var entry in _ledger.Entries.OrderByDescending(e => e.Runs))
        {
            var key = new ItemKey(entry.SourceItemId, entry.SourceHighQuality);

            ImGui.TableNextRow();
            using var id = ImRaii.PushId($"led-{entry.SourceItemId}-{entry.SourceHighQuality}");

            ImGui.TableNextColumn();
            var name = GameItems.Name(entry.SourceItemId) + (entry.SourceHighQuality ? " (HQ)" : string.Empty);
            var open = _ledgerDetail is { } d && d.Equals(key);
            if (ImGui.Selectable(name, open))
                _ledgerDetail = open ? null : key;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Runs.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{ExpectedValue(entry):N0} gil");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(entry.LastSeen.ToString("MM-dd HH:mm"));
        }

        ImGui.EndTable();

        DrawLedgerDetail();
    }

    private void DrawLedgerDetail()
    {
        if (_ledgerDetail is not { } key)
            return;

        var entry = _ledger.Find(key);
        if (entry is null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(
            $"{GameItems.Name(entry.SourceItemId)} — {entry.Runs} run(s) since {entry.FirstSeen:yyyy-MM-dd}");

        foreach (var (yieldKey, total) in entry.Yield.OrderByDescending(kv => kv.Value))
        {
            var item = DesynthLedger.FromKey(yieldKey);
            ImGui.TextUnformatted(
                $"{entry.PerRun(yieldKey),7:F2} per run   " +
                $"{GameItems.Name(item.ItemId)}{(item.HighQuality ? " (HQ)" : "")}   ({total:N0} total)");
        }

        if (ImGui.SmallButton("Close"))
            _ledgerDetail = null;
    }

    public override void DrawSettings()
    {
        var delay = _settings.DelayMs;
        if (ImGui.SliderInt("Delay between items (ms)", ref delay, BuddysDesyntherConfig.MinDelayMs, 2000))
        {
            _settings.DelayMs = delay;
            Config.Save();
        }

        var announce = _settings.AnnounceInChat;
        if (ImGui.Checkbox("Report each yield in chat", ref announce))
        {
            _settings.AnnounceInChat = announce;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("There is nothing to filter. The item sheet already says what can be");
        ImGui.TextDisabled("desynthesised and what level it needs — the list shows everything,");
        ImGui.TextDisabled("marks what you cannot do, and a run skips those.");

        ImGui.Spacing();

        if (ImGui.Button("Write a diagnostic report"))
            WriteProbe();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dumps what the game currently says about desynthesis to a text file.");

        ImGui.SameLine();
        if (ImGui.Button("Clear the ledger"))
        {
            _ledger.Clear();
            Print("Ledger cleared.");
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Length == 0)
        {
            Print($"{Eligible(null).Count} item(s) you can desynthesise " +
                  $"({Eligible(DesynthSource.Bags).Count} in bags). " +
                  $"{_ledger.TotalRuns} recorded. {_status}");
            return true;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "run":
            case "bags":
                Start(DesynthSource.Bags);
                return true;

            case "armoury":
            case "armory":
                Start(DesynthSource.Armoury);
                return true;

            case "all":
                Start(null);
                return true;

            case "stop":
                if (_running)
                    Stop("Stopped.");
                else
                    Print("Not running.");
                return true;

            case "probe":
                WriteProbe();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Writes what the game says about desynthesis right now to a file.</summary>
    private void WriteProbe()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            var path = Path.Combine(directory.FullName, $"desynth-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Salvage.Report());

            Print($"Diagnostic report written to {path}");
            SetStatus("Diagnostic report written.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write the desynthesis diagnostic report.");
            Print("Could not write the report — see the Dalamud log.");
        }
    }
}

/// <summary>Persisted settings for <see cref="BuddysDesyntherModule"/>.</summary>
/// <remarks>
/// Deliberately almost empty. Eligibility comes from the item sheet, so there is nothing about
/// what-can-be-desynthesised left for anyone to configure.
/// </remarks>
public sealed class BuddysDesyntherConfig : IModuleConfig
{
    public const int MinDelayMs = 200;

    public int DelayMs { get; set; } = 500;

    public bool AnnounceInChat { get; set; } = true;
}
