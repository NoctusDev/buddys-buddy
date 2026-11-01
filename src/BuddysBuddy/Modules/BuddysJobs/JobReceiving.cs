using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuddysBuddy.Game;
using BuddysBuddy.Modules.BuddysLedger;
using BuddysBuddy.Services;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>Explicitly armed receiving/payment for one claimed job and one character.</summary>
internal sealed unsafe class JobReceiving : IDisposable
{
    private enum Phase { Idle, Checking, Waiting, Trading, Settling }
    private Phase _phase;
    private readonly JobBoardClient _client;
    private readonly Func<string> _actor;
    private readonly Func<BuddysJobsConfig> _settings;
    private Job? _job;
    private string _owner = "";
    private ulong _ownerId;
    private string _server = "";
    private string _board = "";
    private long _total;
    private long _paid;
    private int _received;
    private int _units;
    private long _gil;
    private uint _target;
    private string _fingerprint = "";
    private DateTime _stable;
    private DateTime _next;
    private DateTime _pollAt;
    private DateTime _settleAt;
    private DateTime _deadline;
    private Task<bool>? _refresh;
    private TradeWatcher? _watcher;
    private TradeRecord? _receipt;
    private ReceivingJournal? _journal;
    private bool _stop;
    private int _beforeItems;
    private long _beforeGil;
    private TradeGilEntry _gilEntry = new();
    private GilEntryStep? _lastGilStep;
    private ItemKey Key => new(_job!.ItemId, _job.HighQuality);
    private long Owed => DeliveryPlan.Payment(_total, _job!.Quantity, _job.Delivered, _received) - _paid;

    public bool Active => _phase != Phase.Idle;
    public string Status { get; private set; } = "";

    public JobReceiving(JobBoardClient client, Func<string> actor, Func<BuddysJobsConfig> settings)
    {
        _client = client;
        _actor = actor;
        _settings = settings;
        DalamudServices.Framework.Update += Tick;
    }

    public void Start(Job job)
    {
        if (Active) return;
        if (ReceivingJournal.Exists) { Status = "Review the earlier receiving/payment record before waiting again."; return; }
        if (Trade.Read() is not { IsTrading: false }) { Status = "Close the current trade before waiting for delivery."; return; }
        if (!DeliveryPlan.TryTotalGil(job.Reward, out _total))
        { Status = "Automatic payment needs a numeric total Reward, for example 5,000,000 gil. Free-text rewards require manual payment."; return; }
        _job = job;
        _owner = _actor();
        _ownerId = DalamudServices.PlayerState.ContentId;
        _server = _settings().ServerUrl;
        _board = _settings().BoardKey;
        _paid = 0;
        _received = 0;
        _stop = false;
        _journal = null;
        _receipt = null;
        _watcher = null;
        _refresh = _client.Refresh();
        _deadline = DateTime.UtcNow.AddSeconds(25);
        _phase = Phase.Checking;
        Status = "Checking the claimed job before waiting…";
    }

    public void Stop()
    {
        if (!Active) return;
        _stop = true;
        if (_phase is Phase.Trading or Phase.Settling)
        {
            var manager = InventoryManager.Instance();
            if (manager is not null) manager->RefuseTrade();
            BeginSettling();
        }
        else Finish("Waiting for delivery stopped.");
    }

    private void Tick(IFramework framework)
    {
        if (!Active) return;
        try
        {
            if (!DalamudServices.ClientState.IsLoggedIn || _actor() != _owner
                || DalamudServices.PlayerState.ContentId != _ownerId
                || _settings().ServerUrl != _server || _settings().BoardKey != _board)
            { Refuse(); Finish("Receiving stopped because the character or board changed. Review any saved payment record."); return; }
            _watcher?.Tick();
            if (DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow.AddMilliseconds(250);
            if (_phase == Phase.Checking) { Prepare(); return; }
            if (_phase == Phase.Settling) { Settle(); return; }
            if (DateTime.UtcNow > _deadline) { Stop(); return; }
            var trade = Trade.Read();
            if (trade is null) return;
            if (_phase == Phase.Waiting)
            {
                PollBoard();
                if (!Active || !trade.IsTrading) return;
                if (!CorrectPartner(trade)) { Refuse(); Finish("Another player opened a trade. Waiting stopped without paying."); return; }
                _target = trade.PartnerEntityId;
                _units = 0;
                _gil = 0;
                _fingerprint = "";
                _gilEntry = new TradeGilEntry();
                _lastGilStep = null;
                _receipt = null;
                _beforeGil = PlayerInventory.CountGil();
                _beforeItems = PlayerInventory.Snapshot(PlayerInventory.Bags).GetValueOrDefault(Key);
                _phase = Phase.Trading;
                _deadline = DateTime.UtcNow.AddMinutes(3);
            }
            if (!trade.IsTrading) { BeginSettling(); return; }
            if (!CorrectPartner(trade) || trade.PartnerEntityId != _target || trade.Given.Items.Count != 0
                || trade.Received.Gil != 0 || trade.Given.GilDisagrees || trade.Received.GilDisagrees)
            { Stop(); return; }
            if (trade.Received.Items.Any(x => x.Key != Key)) { Stop(); return; }
            var units = trade.Received.Items.GetValueOrDefault(Key);
            if (units < 0 || units > _job!.Remaining - _received || (Owed > 0 && units > 0))
            { Stop(); return; }
            if (units == 0 && Owed == 0) { Status = "Waiting for the requested materials in the trade…"; return; }
            var due = units > 0 ? DeliveryPlan.Payment(_total, _job.Quantity, _job.Delivered + _received, units) : Owed;
            var payment = Math.Min(due, DeliveryPlan.MaxTradeGil);
            if (PlayerInventory.CountGil() < payment) { Stop(); Status = "Insufficient gil for this payment. Waiting stopped."; return; }
            var fingerprint = $"{units}:{payment}";
            if (_fingerprint != fingerprint)
            {
                _fingerprint = fingerprint;
                _stable = DateTime.UtcNow;
                _units = units;
                _gil = payment;
                if (_journal is null)
                {
                    _journal = new ReceivingJournal { JobId = _job.Id, From = _job.ClaimedBy, Owner = _owner,
                        PendingUnits = units, PendingGil = payment };
                    _journal.Save(create: true);
                }
                else { _journal.PendingUnits = units; _journal.PendingGil = payment; _journal.Save(); }
            }
            var manager = InventoryManager.Instance();
            if (manager is null) return;
            var stable = DateTime.UtcNow - _stable >= TimeSpan.FromSeconds(1);
            var step = _gilEntry.Next(DateTime.UtcNow, trade.Given.Gil, payment,
                stable && trade.LocalState == TradeState.SelectingTradeGoods && !manager->TradeIsSyncPending,
                QuantityPrompt.IsOpen());
            if (_lastGilStep != step)
            {
                DalamudServices.Log.Debug("Receiving payment: step={Step}, units={Units}, wanted={Wanted}, offered={Offered}, local={Local}, syncPending={SyncPending}",
                    step, units, payment, trade.Given.Gil, trade.LocalState, manager->TradeIsSyncPending);
                _lastGilStep = step;
            }
            switch (step)
            {
                case GilEntryStep.Open:
                    Status = $"Opening the trade's gil box for {payment:N0} gil…";
                    if (!TradeAutomation.OpenGilInput()) throw new InvalidOperationException("Could not open the trade's gil input");
                    return;
                case GilEntryStep.Confirm:
                    Status = $"Entering {_gilEntry.Amount:N0} gil; waiting for the trade offer to update…";
                    if (!QuantityPrompt.Confirm(_gilEntry.Amount)) throw new InvalidOperationException("Could not confirm the trade's gil input");
                    return;
                case GilEntryStep.Blocked:
                    Status = "Close the other quantity dialog so automatic payment can open the trade's gil box.";
                    return;
                case GilEntryStep.TimedOut:
                    Stop();
                    Status = "The gil input did not update the trade offer. Waiting stopped; no trade was accepted.";
                    return;
                case GilEntryStep.Wait:
                    Status = $"Preparing {payment:N0} gil; {trade.Given.Gil:N0} gil currently offered…";
                    return;
            }
            if (!stable || manager->TradeIsSyncPending) return;
            Status = $"Receiving {units:N0} units; paying {payment:N0} gil this trade. Balance after payment: {due - payment:N0} gil.";
            // The sender locks only after finishing all stacks. Offer payment first so it can do so.
            if (trade.RemoteState >= TradeState.LockedIn && trade.LocalState is TradeState.SelectingTradeGoods or TradeState.LockedIn)
                TradeAutomation.ClickTrade();
            if (trade.LocalState >= TradeState.LockedIn) TradeAutomation.ConfirmTrade();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Waiting for job delivery stopped.");
            Refuse();
            Finish($"Receiving stopped: {ex.Message}. Review any saved payment record before restarting.");
        }
    }

    private void Prepare()
    {
        if (_refresh is null || !_refresh.IsCompleted)
        { if (DateTime.UtcNow > _deadline) Finish("Could not check the job. Waiting was not enabled."); return; }
        var current = _client.State.Jobs.FirstOrDefault(x => x.Id == _job!.Id);
        if (!_refresh.IsCompletedSuccessfully || !_refresh.Result || current is null
            || current.State != JobState.Claimed || current.PostedBy != _owner || current.ClaimedBy == _owner
            || !current.ClaimedBy.Contains('@') || current.Remaining <= 0
            || !DeliveryPlan.TryTotalGil(current.Reward, out _total))
        { Finish("This must be your job, claimed by another player, with a numeric total Reward."); return; }
        _job = current;
        var remainingPayment = DeliveryPlan.Payment(_total, current.Quantity, current.Delivered, current.Remaining);
        if (PlayerInventory.CountGil() < remainingPayment)
        { Finish($"You need {remainingPayment:N0} gil available for the remaining request."); return; }
        _watcher = new TradeWatcher(new TradeLedger(), receipt => _receipt = receipt);
        _pollAt = DateTime.UtcNow.AddSeconds(5);
        _deadline = DateTime.UtcNow.AddMinutes(30);
        _phase = Phase.Waiting;
        Status = $"Waiting for delivery from {current.ClaimedBy}. Up to {current.Remaining:N0} units, {remainingPayment:N0} gil total; at most 1,000,000 gil per trade.";
    }

    private bool CorrectPartner(TradeSnapshot trade) => trade.IsTrading
        && DeliveryPlan.SameRecipient(_job!.ClaimedBy, trade.PartnerName, Trade.PartnerWorld(trade.PartnerEntityId));

    private void BeginSettling()
    { _phase = Phase.Settling; _settleAt = DateTime.UtcNow.AddSeconds(5); _deadline = DateTime.UtcNow.AddSeconds(12); }

    private void Settle()
    {
        if (_receipt is null)
        {
            if (DateTime.UtcNow < _settleAt) return;
            if (Trade.Read() is { IsTrading: false } && PlayerInventory.CountGil() == _beforeGil
                && PlayerInventory.Snapshot(PlayerInventory.Bags).GetValueOrDefault(Key) == _beforeItems)
            {
                if (_journal is not null)
                { _journal.PendingGil = 0; _journal.PendingUnits = 0; _journal.Save(); }
                if (_received == 0 && _paid == 0 && _journal is not null) ReceivingJournal.Clear();
                Finish("Trade cancelled without payment. Waiting stopped.");
            }
            else if (DateTime.UtcNow > _deadline) Finish("The last payment could not be verified. Review the saved receiving record.");
            return;
        }
        var receipt = _receipt;
        var incoming = receipt.Received.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        if (receipt.Evidence != TradeEvidence.Confirmed || receipt.PartnerKey != _job!.ClaimedBy
            || receipt.Given.Count != 0 || receipt.GilGiven != _gil || receipt.GilReceived != 0
            || !DeliveryPlan.MatchesExchange(incoming, 0, Key, _units, 0) || _journal is null)
        { Finish("The received items and payment did not match exactly. Review the saved receiving record."); return; }
        _received += _units;
        _paid += _gil;
        _journal.Received = _received;
        _journal.Paid = _paid;
        _journal.StillOwed = Owed;
        _journal.PendingGil = 0;
        _journal.PendingUnits = 0;
        _journal.Save();
        _receipt = null;
        _phase = Phase.Waiting;
        _deadline = DateTime.UtcNow.AddMinutes(30);
        _pollAt = DateTime.MinValue;
        Status = $"Received {_received:N0} units and paid {_paid:N0} gil. Outstanding payment: {Owed:N0} gil. Waiting for the next trade or sender's board update.";
        if (_stop) Finish("Receiving stopped. Review the saved record against the sender's board update.");
    }

    private void PollBoard()
    {
        if (DateTime.UtcNow < _pollAt) return;
        if (_refresh is { IsCompleted: false }) return;
        if (_refresh is not null && _refresh.IsCompletedSuccessfully && _refresh.Result)
        {
            var current = _client.State.Jobs.FirstOrDefault(x => x.Id == _job!.Id);
            if (current is not null && _received > 0 && Owed == 0 && current.Delivered >= _job!.Delivered + _received)
            { ReceivingJournal.Clear(); Finish($"Delivery received: {_received:N0} units, {_paid:N0} gil paid. The sender updated the board."); return; }
            if (current is null || current.State != JobState.Claimed || current.ClaimedBy != _job!.ClaimedBy
                || current.Delivered != _job.Delivered || current.Quantity != _job.Quantity || current.Reward != _job.Reward)
            { Finish("The job changed. Waiting stopped; review any saved receiving record."); return; }
        }
        _refresh = _client.Refresh();
        _pollAt = DateTime.UtcNow.AddSeconds(5);
    }

    private void Refuse()
    {
        if (_phase is not (Phase.Trading or Phase.Settling)) return;
        var manager = InventoryManager.Instance();
        if (manager is not null) manager->RefuseTrade();
    }
    private void Finish(string status) { _phase = Phase.Idle; _watcher = null; Status = status; }
    public void Dispose() { Refuse(); DalamudServices.Framework.Update -= Tick; }
}
