using System;
using System.Collections.Generic;
using System.Numerics;
using BuddysBuddy.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace BuddysBuddy.Modules.BuddysBag;

/// <summary>
/// Opens the chocobo saddlebag and moves every item out of it into the player inventory.
/// </summary>
/// <remarks>
/// Runs as a small state machine driven off the framework tick rather than a tight loop: the game
/// applies a move asynchronously, so the container still reports the old contents on the same
/// frame. One move per tick, spaced by <see cref="BuddysBagConfig.DelayMs"/>, lets each move
/// settle before the next slot is picked.
///
/// Progress is judged by watching the remaining count fall, not by the move's return code, so a
/// move the game silently refuses is still detected and the run aborts instead of spinning.
/// </remarks>
public sealed class BuddysBagModule : ModuleBase
{
    public override string Name => "Buddy's Bag";

    public override string InternalName => "bag";

    public override string Description => "Empties the chocobo saddlebag into your inventory.";

    public override int Order => 10;

    private enum Phase
    {
        Idle,
        OpeningWindow,
        Draining,
    }

    /// <summary>Consecutive ticks without the remaining count dropping before the run is abandoned.</summary>
    private const int StallLimit = 5;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    private BuddysBagConfig _settings = null!;

    private Phase _phase = Phase.Idle;
    private DateTime _nextStepAt;
    private DateTime _openDeadline;
    private int _movedThisRun;
    private int _lastRemaining = -1;
    private int _stalls;
    private string _status = "Idle.";
    private readonly List<string> _log = [];

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysBagConfig>(InternalName);
        Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnUnload()
    {
        Framework.Update -= OnFrameworkUpdate;
        _phase = Phase.Idle;
        _log.Clear();
    }

    public bool IsRunning => _phase != Phase.Idle;

    // ── Run control ──────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning)
            return;

        if (!Services.DalamudServices.ClientState.IsLoggedIn)
        {
            SetStatus("Not logged in.");
            return;
        }

        _movedThisRun = 0;
        _lastRemaining = -1;
        _stalls = 0;
        _log.Clear();

        if (Saddlebag.IsWindowOpen())
        {
            _phase = Phase.Draining;
            SetStatus("Emptying saddlebag…");
        }
        else
        {
            Saddlebag.RequestOpenWindow();
            _openDeadline = DateTime.UtcNow + OpenTimeout;
            _phase = Phase.OpeningWindow;
            SetStatus("Opening the saddlebag…");
        }

        _nextStepAt = DateTime.UtcNow;
    }

    public void Stop(string reason, bool announce = true)
    {
        if (!IsRunning)
            return;

        _phase = Phase.Idle;
        SetStatus(reason);

        if (announce && _settings.AnnounceInChat)
            Print(_movedThisRun > 0 ? $"{reason} Moved {_movedThisRun} item(s)." : reason);
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_phase == Phase.Idle || DateTime.UtcNow < _nextStepAt)
            return;

        _nextStepAt = DateTime.UtcNow.AddMilliseconds(Math.Max(BuddysBagConfig.MinDelayMs, _settings.DelayMs));

        try
        {
            switch (_phase)
            {
                case Phase.OpeningWindow:
                    StepOpening();
                    break;
                case Phase.Draining:
                    StepDraining();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Buddy's Bag threw while running; stopping.");
            Stop("Stopped after an error — see /xllog.");
        }
    }

    private void StepOpening()
    {
        if (Saddlebag.IsWindowOpen())
        {
            _phase = Phase.Draining;
            SetStatus("Emptying saddlebag…");
            return;
        }

        if (DateTime.UtcNow > _openDeadline)
        {
            // The game refuses to open it while occupied — in combat, a cutscene, a trade.
            Stop("Could not open the saddlebag. Open it manually and try again.");
            return;
        }

        Saddlebag.RequestOpenWindow();
    }

    private void StepDraining()
    {
        if (!Services.DalamudServices.ClientState.IsLoggedIn)
        {
            Stop("Stopped — logged out.");
            return;
        }

        // The game only permits saddlebag moves while its window is open.
        if (!Saddlebag.IsWindowOpen())
        {
            Stop("Stopped — the saddlebag window closed.");
            return;
        }

        var remaining = Saddlebag.CountItems(_settings.IncludePremium);
        if (remaining == 0)
        {
            Stop($"Saddlebag is empty. Moved {_movedThisRun} item(s).", announce: false);
            if (_settings.AnnounceInChat)
                Print($"Saddlebag emptied — {_movedThisRun} item(s) moved.");
            return;
        }

        // No progress since the last tick means the move was refused; do not spin forever.
        if (_lastRemaining >= 0 && remaining >= _lastRemaining)
        {
            if (++_stalls >= StallLimit)
            {
                Stop($"Stopped — {remaining} item(s) could not be moved.");
                return;
            }
        }
        else
        {
            _stalls = 0;
        }

        _lastRemaining = remaining;

        if (!Saddlebag.TryFindFreePlayerSlot(out var dstBag, out var dstSlot))
        {
            Stop($"Stopped — inventory is full, {remaining} item(s) left.");
            return;
        }

        if (!Saddlebag.TryFindNextItem(_settings.IncludePremium, out var srcBag, out var srcSlot, out var itemId))
        {
            Stop($"Saddlebag is empty. Moved {_movedThisRun} item(s).");
            return;
        }

        var result = Saddlebag.Move(srcBag, srcSlot, dstBag, dstSlot);
        var name = Saddlebag.ItemName(itemId);

        if (result == 0)
        {
            _movedThisRun++;
            AddLog($"Moved {name}");
            SetStatus($"Moved {_movedThisRun} — {remaining - 1} left…");
        }
        else
        {
            // Counted as a stall on the next tick if the item really did not move.
            AddLog($"Refused ({result}): {name}");
            Log.Warning("MoveItemSlot returned {Result} for {Item} ({Bag} slot {Slot}).", result, name, srcBag, srcSlot);
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        Log.Debug("Buddy's Bag: {Status}", status);
    }

    private void AddLog(string line)
    {
        _log.Add(line);
        if (_log.Count > 100)
            _log.RemoveAt(0);
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        var windowOpen = Saddlebag.IsWindowOpen();
        var remaining = Saddlebag.CountItems(_settings.IncludePremium);
        var free = Saddlebag.CountFreePlayerSlots();

        ImGui.TextUnformatted($"Saddlebag: {remaining} item(s)");
        ImGui.TextUnformatted($"Inventory: {free} free slot(s)");

        ImGui.Spacing();
        if (windowOpen)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "Saddlebag window is open.");
        else
            ImGui.TextDisabled("Saddlebag window is closed — it will be opened for you.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (IsRunning)
        {
            if (ImGui.Button("Stop", new Vector2(140, 0)))
                Stop("Stopped.");
        }
        else
        {
            var blocked = remaining == 0 || free == 0;
            using (ImRaiiDisabled(blocked))
            {
                if (ImGui.Button("Empty saddlebag", new Vector2(140, 0)) && !blocked)
                    Start();
            }

            if (remaining == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("Nothing to move.");
            }
            else if (free == 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Inventory is full.");
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(_status);

        if (_log.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled($"Recent ({_log.Count})");

        if (ImGui.BeginChild("##baglog", new Vector2(0, 160), true))
        {
            for (var i = _log.Count - 1; i >= 0; i--)
                ImGui.TextUnformatted(_log[i]);
        }

        ImGui.EndChild();
    }

    public override void DrawSettings()
    {
        var delay = _settings.DelayMs;
        if (ImGui.SliderInt("Delay between moves (ms)", ref delay, BuddysBagConfig.MinDelayMs, 1000))
        {
            _settings.DelayMs = delay;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each move needs a moment to apply. Lower is faster but more likely to be refused.");

        var premium = _settings.IncludePremium;
        if (ImGui.Checkbox("Include premium saddlebag pages", ref premium))
        {
            _settings.IncludePremium = premium;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only useful with a Companion premium subscription; harmless otherwise.");

        var announce = _settings.AnnounceInChat;
        if (ImGui.Checkbox("Report the result in chat", ref announce))
        {
            _settings.AnnounceInChat = announce;
            Config.Save();
        }
    }

    public override bool HandleCommand(string args)
    {
        var verb = args.Trim().ToLowerInvariant();

        switch (verb)
        {
            case "":
            case "empty":
            case "start":
                if (IsRunning)
                    Print("Already running.");
                else
                    Start();
                return true;

            case "stop":
                if (IsRunning)
                    Stop("Stopped.");
                else
                    Print("Not running.");
                return true;

            case "status":
                Print($"{_status} Saddlebag has {Saddlebag.CountItems(_settings.IncludePremium)} item(s).");
                return true;

            default:
                return false;
        }
    }

    /// <summary>Wraps ImGui's disabled scope so the button block reads cleanly.</summary>
    private static IDisposable ImRaiiDisabled(bool disabled)
        => Dalamud.Interface.Utility.Raii.ImRaii.Disabled(disabled);
}

/// <summary>Persisted settings for <see cref="BuddysBagModule"/>.</summary>
public sealed class BuddysBagConfig : IModuleConfig
{
    public const int MinDelayMs = 50;

    /// <summary>Milliseconds between moves. The game refuses moves issued too quickly.</summary>
    public int DelayMs { get; set; } = 200;

    public bool IncludePremium { get; set; } = true;

    public bool AnnounceInChat { get; set; } = true;
}
