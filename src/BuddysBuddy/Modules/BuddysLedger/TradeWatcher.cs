using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysLedger;

/// <summary>
/// Watches the trade window and records what actually changed hands.
/// </summary>
/// <remarks>
/// Two independent sources, deliberately. The window says what the deal <em>was</em>; the inventory
/// says what actually <em>moved</em>. They should agree, and when they do not the disagreement is
/// recorded on the trade rather than smoothed over — the same arrangement Buddy's Desynther uses,
/// and what caught the crystals it was silently missing.
///
/// **The inventory is the record and the window is the cross-check**, not the other way round. The
/// window describes what was *offered*; only the bags show what actually *arrived*, and those are
/// not the same claim. The window earns its place by saying whether the deal that landed is the
/// deal that was struck.
///
/// (An earlier version of this comment justified the choice by claiming the partner's slots carry
/// no high-quality flag. That was wrong — inferred from a trade that happened to contain no HQ
/// items, and disproved by the first one that did, which reported <c>flags=HighQuality</c>
/// correctly. The conclusion stands on the reason above; the original reason did not.)
///
/// The trade window empties the instant a trade completes, so nothing here reads it at commit time.
/// The last reading taken while the deal was on the table is kept and used, and the settle step
/// then waits for the inventory to catch up rather than assuming the same frame already shows it —
/// a state flipping back to <see cref="TradeState.NotTrading"/> proves the window closed, not that
/// the server has finished moving anything.
/// </remarks>
internal sealed class TradeWatcher
{
    /// <summary>How long to wait after the window closes for the inventory to settle.</summary>
    private const int SettleTimeoutMs = 4000;

    /// <summary>Consecutive identical readings that count as the inventory having settled.</summary>
    private const int StableReadings = 3;

    /// <summary>
    /// The combined <see cref="TradeState"/> rank at which both sides have locked their offers in.
    /// </summary>
    /// <remarks>
    /// A snapshot taken at or above this is a reading of the finished deal rather than of somebody
    /// still filling slots, which is what makes it worth comparing the inventory against.
    /// </remarks>
    private const int BothLockedInRank = (int)TradeState.LockedIn * 2;

    private enum Phase
    {
        Idle,
        Trading,
        Settling,
    }

    private readonly TradeLedger _ledger;
    private readonly Action<TradeRecord> _onRecorded;

    private Phase _phase = Phase.Idle;

    // What the window showed, and how far along the trade was when it showed it.
    private TradeSnapshot? _staged;
    private int _stagedRank;

    // Who, and as whom.
    private string _partner = string.Empty;
    private string? _partnerWorld;
    private string _character = string.Empty;
    private ulong _contentId;
    private DateTime _startedAt;

    // The inventory either side of the trade.
    private Dictionary<ItemKey, int> _before = new();
    private long _beforeGil;
    private DateTime _settleUntil;
    private Dictionary<ItemKey, int>? _lastDelta;
    private long _lastGilDelta;
    private int _stable;

    public TradeWatcher(TradeLedger ledger, Action<TradeRecord> onRecorded)
    {
        _ledger = ledger;
        _onRecorded = onRecorded;
    }

    /// <summary>The live trade, or null when there is not one. For display only.</summary>
    public TradeSnapshot? Current { get; private set; }

    /// <summary>Trades that ended with nothing moving, this session.</summary>
    public int CancelledThisSession { get; private set; }

    /// <summary>
    /// One tick of the state machine.
    /// </summary>
    /// <remarks>
    /// Runs every framework update rather than on an interval. The confirmed-to-closed window is a
    /// handful of frames wide and an interval long enough to be cheap is long enough to miss it —
    /// and the work is a few pointer reads until a trade is actually open.
    /// </remarks>
    public void Tick()
    {
        if (!DalamudServices.ClientState.IsLoggedIn)
        {
            Reset();
            return;
        }

        var live = Trade.Read();
        if (live is null)
            return;

        Current = live.IsTrading ? live : null;

        switch (_phase)
        {
            case Phase.Idle:
                if (live.IsTrading)
                    Begin(live);
                break;

            case Phase.Trading:
                if (live.IsTrading)
                    Observe(live);
                else
                    BeginSettling();
                break;

            case Phase.Settling:
                // A second trade starting is the hard deadline on settling the first one.
                if (live.IsTrading)
                {
                    Commit();
                    Begin(live);
                }
                else
                {
                    Settle();
                }

                break;
        }
    }

    /// <summary>Abandons any trade in progress without recording it. For logout and unload.</summary>
    public void Reset()
    {
        _phase = Phase.Idle;
        _staged = null;
        _stagedRank = 0;
        _lastDelta = null;
        Current = null;
    }

    private void Begin(TradeSnapshot live)
    {
        _phase = Phase.Trading;
        _staged = null;
        _stagedRank = 0;
        _startedAt = DateTime.Now;

        _partner = live.PartnerName;
        _partnerWorld = Trade.PartnerWorld(live.PartnerEntityId);
        _character = DalamudServices.PlayerState.CharacterName ?? string.Empty;
        _contentId = DalamudServices.PlayerState.ContentId;

        _before = PlayerInventory.Snapshot(PlayerInventory.Receivable);
        _beforeGil = Trade.CountGil();

        Observe(live);
    }

    private void Observe(TradeSnapshot live)
    {
        // A trade request arrives before the partner's details are populated, so both are taken as
        // soon as they can be read rather than only at the start.
        if (string.IsNullOrEmpty(_partner) && !string.IsNullOrEmpty(live.PartnerName))
            _partner = live.PartnerName;

        _partnerWorld ??= Trade.PartnerWorld(live.PartnerEntityId);

        // A later reading wins at the same rank or better, so taking an item back off the table is
        // picked up. The emptiness guard is what stops the window clearing on completion from
        // erasing the deal it just completed.
        if (_staged is null || (!live.IsEmpty && live.Rank >= _stagedRank))
        {
            _staged = live;
            _stagedRank = live.Rank;
        }
    }

    private void BeginSettling()
    {
        _phase = Phase.Settling;
        _settleUntil = DateTime.UtcNow.AddMilliseconds(SettleTimeoutMs);
        _lastDelta = null;
        _lastGilDelta = 0;
        _stable = 0;
    }

    private void Settle()
    {
        var delta = Delta(_before, PlayerInventory.Snapshot(PlayerInventory.Receivable));
        var gilDelta = Trade.CountGil() - _beforeGil;

        var moved = delta.Count > 0 || gilDelta != 0;
        var same = _lastDelta is not null && gilDelta == _lastGilDelta && Same(_lastDelta, delta);

        _stable = moved && same ? _stable + 1 : 0;
        _lastDelta = delta;
        _lastGilDelta = gilDelta;

        if (_stable >= StableReadings || DateTime.UtcNow >= _settleUntil)
            Commit();
    }

    /// <summary>
    /// Turns everything observed into a record, or decides there is nothing to record.
    /// </summary>
    /// <remarks>
    /// The inventory is what gets written down, always. It is measured from your own bags, so its
    /// quantities and high-quality flags are certain, where the partner's half of the trade window
    /// carries neither a container nor usable flags.
    ///
    /// The window's job is to say whether what landed is what was agreed. A trade where the two
    /// disagree is still recorded — from the inventory — and marked, because the disagreement is
    /// information and hiding it would make the ledger quietly wrong instead of loudly uncertain.
    /// </remarks>
    private void Commit()
    {
        var staged = _staged;
        var stagedRank = _stagedRank;
        var delta = _lastDelta ?? Delta(_before, PlayerInventory.Snapshot(PlayerInventory.Receivable));
        var gilDelta = _lastDelta is null ? Trade.CountGil() - _beforeGil : _lastGilDelta;

        _phase = Phase.Idle;
        _staged = null;
        _stagedRank = 0;
        _lastDelta = null;
        Current = null;

        var observedGiven = delta.Where(kv => kv.Value < 0).ToDictionary(kv => kv.Key, kv => -kv.Value);
        var observedReceived = delta.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var movedSomething = observedGiven.Count > 0 || observedReceived.Count > 0 || gilDelta != 0;

        // Whether the window ever showed a settled deal decides how much the record can be trusted,
        // never whether there is one — only the inventory can say a trade actually happened.
        var settled = staged is not null && !staged.IsEmpty && stagedRank >= BothLockedInRank;

        if (!movedSomething)
        {
            CancelledThisSession++;

            if (settled)
                DalamudServices.Log.Warning(
                    "Trade with {Partner} was locked in on both sides but nothing moved within {Timeout}ms. " +
                    "Treated as cancelled; if it was not, the settle window is too short.",
                    Partner(), SettleTimeoutMs);
            else
                DalamudServices.Log.Debug("Trade with {Partner} ended with nothing moved — not recorded.", Partner());

            return;
        }

        var record = new TradeRecord
        {
            At = _startedAt,
            Character = _character,
            ContentId = _contentId,
            Partner = Partner(),
            PartnerWorld = _partnerWorld,
            Evidence = settled ? TradeEvidence.Confirmed : TradeEvidence.Reconstructed,
        };

        record.Given.AddRange(Lines(observedGiven));
        record.Received.AddRange(Lines(observedReceived));
        record.GilGiven = gilDelta < 0 ? -gilDelta : 0;
        record.GilReceived = gilDelta > 0 ? gilDelta : 0;

        if (settled)
        {
            LogGilSources(staged!);

            if (!Agrees(staged!, observedGiven, observedReceived, gilDelta))
            {
                record.Evidence = TradeEvidence.Disputed;

                DalamudServices.Log.Warning(
                    "Trade with {Partner}: the window offered [{Window}] but the bags changed by [{Bags}]. " +
                    "Recording the bags.",
                    record.Partner, Summarise(staged!), Summarise(delta, gilDelta));
            }
        }

        _ledger.Add(record);
        _onRecorded(record);
    }

    private string Partner() => string.IsNullOrEmpty(_partner) ? "(unknown)" : _partner;

    /// <summary>
    /// Whether the inventory backs up what the window said.
    /// </summary>
    /// <remarks>
    /// Compared by item id and total units, not by stack and not by quality. The inventory reports
    /// one net figure per item where the window can spread the same item across five slots — a real
    /// trade here was 4,995 units as 5x999. Quality is folded away because a diff over your own bags
    /// and an offer in a window can legitimately split the same item differently between qualities;
    /// that is not worth calling a dispute over, and quality is recorded from the bags regardless.
    /// </remarks>
    private static bool Agrees(
        TradeSnapshot staged,
        IReadOnlyDictionary<ItemKey, int> observedGiven,
        IReadOnlyDictionary<ItemKey, int> observedReceived,
        long gilDelta)
    {
        if (staged.Received.Gil - staged.Given.Gil != gilDelta)
            return false;

        return Matches(staged.Given.Items, observedGiven)
               && Matches(staged.Received.Items, observedReceived);

        static bool Matches(
            IReadOnlyDictionary<ItemKey, int> offered, IReadOnlyDictionary<ItemKey, int> observed)
        {
            var expected = Fold(offered);
            var actual = Fold(observed);

            return expected.Count == actual.Count
                   && expected.All(kv => actual.GetValueOrDefault(kv.Key) == kv.Value);
        }

        static Dictionary<uint, long> Fold(IReadOnlyDictionary<ItemKey, int> items)
        {
            var folded = new Dictionary<uint, long>();

            foreach (var (key, quantity) in items)
                folded[key.ItemId] = folded.GetValueOrDefault(key.ItemId) + quantity;

            return folded;
        }
    }

    private static string Summarise(TradeSnapshot staged)
        => $"you {Describe(staged.Given.Items, staged.Given.Gil)}, them {Describe(staged.Received.Items, staged.Received.Gil)}";

    private static string Summarise(IReadOnlyDictionary<ItemKey, int> delta, long gilDelta)
        => string.Join(", ", delta.Select(kv => $"{kv.Value:+#;-#}x #{kv.Key.ItemId}")
                                  .Append($"{gilDelta:+#,##0;-#,##0;0} gil"));

    private static string Describe(IReadOnlyDictionary<ItemKey, int> items, long gil)
    {
        var parts = items.Select(kv => $"{kv.Value}x #{kv.Key.ItemId}").ToList();

        if (gil > 0)
            parts.Add($"{gil:N0} gil");

        return parts.Count == 0 ? "nothing" : string.Join(" + ", parts);
    }

    /// <summary>
    /// Notes when the two gil readings disagree.
    /// </summary>
    /// <remarks>
    /// Settled in game 2026-08-23, and the answer is **asymmetric**, which is exactly why both
    /// readings are kept rather than one being picked:
    ///
    /// - **Their gil** is in remote slot `[5]` as item id 1, agreeing with
    ///   <c>ReceiveGilCount</c> exactly (1,000,000 both ways). The slot is present even at zero.
    /// - **Your gil is not in local slot `[5]` at all** — it stayed empty while 69 gil was on the
    ///   table, and only <c>SendGilCount</c> knew about it.
    ///
    /// Reading the slots alone would silently record every gil payment you make as zero. This
    /// method is what surfaced that, at Information level, on the first trade that paid any.
    /// </remarks>
    private static void LogGilSources(TradeSnapshot staged)
    {
        foreach (var (side, offer) in new[] { ("given", staged.Given), ("received", staged.Received) })
        {
            if (offer.GilDisagrees)
                DalamudServices.Log.Warning(
                    "Trade gil ({Side}): slots say {Slots}, the trade array says {Array}. Using the slots.",
                    side, offer.GilInSlots, offer.GilInArray);
            else if (offer.GilInSlots < 0 && offer.GilInArray > 0)
                DalamudServices.Log.Information(
                    "Trade gil ({Side}): {Amount} came from the trade array only — no gil in the item slots.",
                    side, offer.GilInArray);
        }
    }

    private static IEnumerable<TradeLine> Lines(IReadOnlyDictionary<ItemKey, int> items)
        => items.Where(kv => kv.Value > 0).Select(kv => TradeLine.From(kv.Key, kv.Value));

    /// <summary>Signed change per item: negative left your bags, positive arrived in them.</summary>
    private static Dictionary<ItemKey, int> Delta(
        IReadOnlyDictionary<ItemKey, int> before, IReadOnlyDictionary<ItemKey, int> after)
    {
        var delta = new Dictionary<ItemKey, int>();

        foreach (var (key, quantity) in after)
        {
            var change = quantity - before.GetValueOrDefault(key);
            if (change != 0)
                delta[key] = change;
        }

        foreach (var (key, quantity) in before)
        {
            if (!after.ContainsKey(key))
                delta[key] = -quantity;
        }

        return delta;
    }

    private static bool Same(IReadOnlyDictionary<ItemKey, int> a, IReadOnlyDictionary<ItemKey, int> b)
        => a.Count == b.Count && a.All(kv => b.GetValueOrDefault(kv.Key) == kv.Value);
}
