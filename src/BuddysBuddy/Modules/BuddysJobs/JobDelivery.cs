using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuddysBuddy.Game;
using BuddysBuddy.Modules.BuddysLedger;
using BuddysBuddy.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>One explicitly started delivery. All game access stays on the framework thread.</summary>
internal sealed unsafe class JobDelivery : IDisposable
{
    private enum Phase { Idle, RefreshJob, RefreshBatch, Travel, FindRecipient, Request, Offer, Quantity, VerifyOffer, Confirm, Settle, Report }
    private readonly JobBoardClient _client;
    private readonly Func<string> _actor;
    private Phase _phase;
    private Job? _job;
    private string _owner = "";
    private string _server = "";
    private string _board = "";
    private readonly Func<BuddysJobsConfig> _settings;
    private string _route = "";
    private DateTime _deadline;
    private DateTime _next;
    private bool _ownsTravel;
    private uint _targetId;
    private ulong _ownerContentId;
    private List<DeliveryStack> _batch = new();
    private int _slot;
    private int _batchQuantity;
    private long _rewardGil;
    private long _expectedGil;
    private long _gilDue;
    private long _beforeGil;
    private DateTime _offerStableSince;
    private string _offerFingerprint = "";
    private ItemKey DeliveryKey => new(_job!.ItemId, _job.HighQuality);
    private int _delivered;
    private int _toDeliver;
    private Task<bool>? _refresh;
    private Task<JobResult>? _report;
    private TradeWatcher? _watcher;
    private TradeRecord? _receipt;
    private int _estateChoice;
    private bool _cancel;
    private int _before;
    private DateTime _settleUntil;
    private DeliveryJournal? _journal;
    private string _finishReason = "";
    private bool _readyToTrade;

    public bool Active => _phase != Phase.Idle;
    public string Status { get; private set; } = "";

    public JobDelivery(JobBoardClient client, Func<string> actor, Func<BuddysJobsConfig> settings)
    {
        _client = client;
        _actor = actor;
        _settings = settings;
        DalamudServices.Framework.Update += Tick;
    }

    public void Start(Job job, string route, int estateChoice = 0)
    {
        if (Active) return;
        if (DeliveryJournal.Exists) { Status = DeliveryJournal.Summary(); return; }
        _job = job;
        _owner = _actor();
        _ownerContentId = DalamudServices.PlayerState.ContentId;
        _server = _settings().ServerUrl;
        _board = _settings().BoardKey;
        _route = route.Trim();
        _estateChoice = estateChoice;
        _delivered = 0;
        _gilDue = 0;
        _cancel = false;
        _ownsTravel = false;
        _receipt = null;
        _watcher = null;
        _journal = null;
        _readyToTrade = false;
        if (_owner.Length == 0 || job.ClaimedBy != _owner || job.State != JobState.Claimed)
        { Finish("Claim this job as your current character first."); return; }
        if (!job.PostedBy.Contains('@') || job.PostedBy == _owner)
        { Finish("Delivery needs another player's full Name@World."); return; }
        if (_route.Contains('@') || _route.Contains('\n') || _route.Contains('\r') || _route.StartsWith('/'))
        { Finish("Enter only a Lifestream housing alias, without /li or Name@World."); return; }
        var trade = Trade.Read();
        if (trade is null || trade.IsTrading || QuantityPrompt.IsOpen())
        { Finish("Close the current trade and quantity dialog before delivering."); return; }
        _refresh = _client.Refresh();
        Set(Phase.RefreshJob, "Checking the claimed job…", 25);
    }

    public void Stop()
    {
        if (!Active) return;
        // Let an already-confirmed trade settle and report it; never forget an in-flight delivery.
        _cancel = true;
        AbortTravel();
        if (_phase is Phase.Offer or Phase.Quantity or Phase.VerifyOffer or Phase.Confirm or Phase.Settle or Phase.Request)
        {
            var manager = InventoryManager.Instance();
            if (manager is not null) manager->RefuseTrade();
            BeginSettling("Stopping; checking whether the last trade completed…");
        }
        else if (_phase != Phase.Report)
            EndDelivery("Delivery stopped.");
    }

    private void Tick(IFramework framework)
    {
        if (!Active) return;
        try
        {
            if (!DalamudServices.ClientState.IsLoggedIn || _actor() != _owner
                || DalamudServices.PlayerState.ContentId != _ownerContentId)
            { AbortTravel(); Finish("Delivery stopped: the logged-in character changed. Review the saved delivery record before restarting."); return; }
            if (_settings().ServerUrl != _server || _settings().BoardKey != _board)
            { AbortTravel(); Finish("Delivery stopped: board settings changed. Review the saved delivery record before restarting."); return; }
            _watcher?.Tick();
            if (DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow.AddMilliseconds(350);
            if (DateTime.UtcNow > _deadline)
            {
                if (_phase is Phase.Request or Phase.Offer or Phase.Quantity or Phase.VerifyOffer or Phase.Confirm)
                { Stop(); return; }
                AbortTravel();
                if (_phase is Phase.Report or Phase.Settle)
                    Finish("Delivery needs review: the last trade or board reply was not confirmed. Check the saved delivery record before restarting.");
                else EndDelivery($"Delivery stopped while {Status}");
                return;
            }
            switch (_phase)
            {
                case Phase.RefreshJob: Prepare(); break;
                case Phase.RefreshBatch:
                    if (_refresh is null || !_refresh.IsCompleted) break;
                    var current = _client.State.Jobs.FirstOrDefault(x => x.Id == _job!.Id);
                    if (!_refresh.IsCompletedSuccessfully || !_refresh.Result || current is null
                        || current.State != JobState.Claimed || current.ClaimedBy != _owner
                        || current.Delivered != _job!.Delivered || current.ItemId != _job.ItemId
                        || current.Quantity != _job.Quantity || current.HighQuality != _job.HighQuality || current.PostedBy != _job.PostedBy)
                    { EndDelivery("The job changed or could not be refreshed. No more trades were started."); break; }
                    _readyToTrade = true;
                    Set(Phase.FindRecipient, "Waiting for the recipient for the next trade…", 180);
                    break;
                case Phase.Travel:
                    if (!BusyTravel())
                    { _ownsTravel = false; Set(Phase.FindRecipient, $"Waiting for {_job!.PostedBy} nearby…", 180); }
                    break;
                case Phase.FindRecipient: FindRecipient(); break;
                case Phase.Request:
                    var trade = Trade.Read();
                    if (trade is { IsTrading: true } && trade.LocalState >= TradeState.SelectingTradeGoods)
                    {
                        if (!CorrectPartner(trade)) { Stop(); return; }
                        Set(Phase.Offer, "Adding the requested materials…", 60);
                    }
                    break;
                case Phase.Offer: Offer(); break;
                case Phase.Quantity:
                    var quantityTrade = Trade.Read();
                    if (quantityTrade is null || !CorrectPartner(quantityTrade)
                        || quantityTrade.LocalState != TradeState.SelectingTradeGoods) { Stop(); break; }
                    if (QuantityPrompt.IsOpen())
                    { QuantityPrompt.Confirm(_batch[_slot].Quantity); Set(Phase.VerifyOffer, "Checking offered quantity…", 10); }
                    break;
                case Phase.VerifyOffer:
                    var offered = Trade.Read();
                    if (offered is null || !CorrectPartner(offered)) { Stop(); break; }
                    var expected = _batch.Take(_slot + 1).Sum(x => x.Quantity);
                    if (offered.Given.Items.GetValueOrDefault(_batch[0].Key) == expected)
                    { _slot++; Set(Phase.Offer, "Adding the requested materials…", 60); }
                    break;
                case Phase.Confirm: Confirm(); break;
                case Phase.Settle: Settle(); break;
                case Phase.Report: Report(); break;
            }
        }
        catch (Exception ex)
        {
            AbortTravel();
            DalamudServices.Log.Error(ex, "Job delivery stopped.");
            if (_phase is Phase.Request or Phase.Offer or Phase.Quantity or Phase.VerifyOffer or Phase.Confirm or Phase.Settle)
            {
                var manager = InventoryManager.Instance();
                if (manager is not null) manager->RefuseTrade();
            }
            Finish($"Delivery stopped: {ex.Message}. Check the last trade before restarting.");
        }
    }

    private void Prepare()
    {
        if (_refresh is null || !_refresh.IsCompleted) return;
        if (!_refresh.IsCompletedSuccessfully || !_refresh.Result)
        { Finish("Could not read the claimed job. Check the connection and try again."); return; }
        var current = _client.State.Jobs.FirstOrDefault(x => x.Id == _job!.Id);
        if (current is null || current.State != JobState.Claimed || current.ClaimedBy != _owner || current.Remaining <= 0)
        { Finish("This job is no longer claimed by you or has no remaining materials."); return; }
        _job = current;
        _rewardGil = DeliveryPlan.TryTotalGil(current.Reward, out var reward) ? reward : -1;
        var key = new ItemKey(current.ItemId, current.HighQuality);
        _toDeliver = Math.Min(current.Remaining, PlayerInventory.Snapshot(PlayerInventory.Bags).GetValueOrDefault(key));
        if (_toDeliver == 0)
        { Finish("No matching materials in your bags. Withdraw them from retainers or storage first."); return; }
        if (BusyTravel()) { Finish("Lifestream is busy. Wait for it to finish first."); return; }
        if (_route.Length > 0)
        {
            DalamudServices.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(_route);
            _ownsTravel = true;
            Set(Phase.Travel, "Travelling using the saved Lifestream housing route…", 600);
            _next = DateTime.UtcNow.AddSeconds(3);
            return;
        }
        var friends = InfoProxyFriendList.Instance();
        if (friends is null) { Finish("Open your Friends List once, then try delivery again."); return; }
        var names = new List<string>();
        var found = false;
        foreach (var friend in friends->CharDataSpan)
        {
            names.Add(friend.NameString);
            var world = DalamudServices.DataManager.GetExcelSheet<World>().GetRow(friend.HomeWorld).Name.ToString();
            found |= DeliveryPlan.SameRecipient(current.PostedBy, friend.NameString, world);
        }
        if (!found)
        { Finish("The requester was not found in your loaded Friends List. Open the list or save their Lifestream housing alias."); return; }
        if (!DeliveryPlan.TryFriendCommand(current.PostedBy, names, _estateChoice, out var command))
        { Finish("The friend name is ambiguous. Save a Lifestream housing alias for this requester."); return; }
        DalamudServices.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(command);
        _ownsTravel = true;
        Set(Phase.Travel, "Lifestream is selecting the friend's estate and travelling…", 600);
        _next = DateTime.UtcNow.AddSeconds(3);
    }

    private bool BusyTravel()
    {
        try { return DalamudServices.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch (Exception ex) { throw new InvalidOperationException("Enable a compatible Lifestream version before starting delivery", ex); }
    }

    private void AbortTravel()
    {
        if (!_ownsTravel) return;
        try { DalamudServices.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch (Exception ex) { DalamudServices.Log.Debug(ex, "Could not stop delivery travel."); }
        _ownsTravel = false;
    }

    private void FindRecipient()
    {
        if (BusyTravel() || DalamudServices.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || DalamudServices.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || DalamudServices.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting]) return;
        var local = DalamudServices.ObjectTable.LocalPlayer;
        if (local is null) return;
        foreach (var player in DalamudServices.ObjectTable.OfType<IPlayerCharacter>())
        {
            if (!DeliveryPlan.SameRecipient(_job!.PostedBy, player.Name.ToString(), player.HomeWorld.ValueNullable?.Name.ToString())) continue;
            if (!player.IsTargetable || System.Numerics.Vector3.Distance(local.Position, player.Position) > 3)
            { Status = "Recipient found. Move within trade range (3 yalms) to continue."; return; }
            if (!_readyToTrade)
            {
                _refresh = _client.Refresh();
                Set(Phase.RefreshBatch, "Checking the job before trading…", 25);
                return;
            }
            BeginTrade(player.EntityId);
            return;
        }
    }

    private void BeginTrade(uint targetId)
    {
        if (_job is null) { Finish("No delivery job is selected."); return; }
        var trade = Trade.Read();
        if (trade is null || trade.IsTrading || QuantityPrompt.IsOpen()) return;
        var slots = new List<DeliveryStack>();
        var manager = InventoryManager.Instance();
        if (manager is null) return;
        foreach (var bag in PlayerInventory.Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container is null || !container->IsLoaded) return;
            for (ushort i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is null || item->ItemId == 0) continue;
                slots.Add(new((int)bag, i, new(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0), item->Quantity, 0));
            }
        }
        var available = _toDeliver - _delivered;
        if (_rewardGil >= 0 && _gilDue == 0)
            available = DeliveryPlan.PaymentBatchLimit(_rewardGil, _job!.Quantity, _job.Delivered + _delivered, available);
        _batch = _gilDue > 0 ? new() : DeliveryPlan.Batch(slots, DeliveryKey, available);
        if (_batch.Count == 0 && _gilDue == 0) { EndDelivery("No more matching materials in bags."); return; }
        _batchQuantity = _batch.Sum(x => x.Quantity);
        _expectedGil = _gilDue > 0 ? Math.Min(_gilDue, DeliveryPlan.MaxTradeGil)
            : _rewardGil >= 0 ? Math.Min(DeliveryPlan.Payment(_rewardGil, _job!.Quantity, _job.Delivered + _delivered, _batchQuantity), DeliveryPlan.MaxTradeGil) : -1;
        _offerFingerprint = "";
        _slot = 0;
        _receipt = null;
        _before = PlayerInventory.Snapshot(PlayerInventory.Bags).GetValueOrDefault(DeliveryKey);
        _beforeGil = PlayerInventory.CountGil();
        _watcher = new TradeWatcher(new TradeLedger(), receipt => _receipt = receipt);
        _targetId = targetId;
        if (_journal is null)
        {
            _journal = new DeliveryJournal
            {
                JobId = _job.Id, Recipient = _job.PostedBy, Item = _job.ItemName,
                Character = _owner, Server = _server, InFlight = _batchQuantity,
            };
            _journal.Create();
        }
        _journal.Verified = _delivered;
        _journal.InFlight = _batchQuantity;
        _journal.InFlightGil = Math.Max(0, _expectedGil);
        _journal.GilStillDue = _gilDue;
        _journal.Save(); // Never start a trade if its recovery record cannot be written.
        _readyToTrade = false;
        manager->SendTradeRequest(targetId);
        Set(Phase.Request, _batchQuantity == 0
            ? $"Collecting {_expectedGil:N0} gil of the outstanding {_gilDue:N0}-gil payment…"
            : $"Waiting for {_job.PostedBy} to accept the trade…", 60);
    }

    private bool CorrectPartner(TradeSnapshot trade)
        => trade.IsTrading && trade.PartnerEntityId == _targetId
           && DeliveryPlan.SameRecipient(_job!.PostedBy, trade.PartnerName, Trade.PartnerWorld(trade.PartnerEntityId));

    private void Offer()
    {
        var trade = Trade.Read();
        if (trade is null || !CorrectPartner(trade)) { Stop(); return; }
        if (_slot == _batch.Count) { Set(Phase.Confirm, "Waiting for trade confirmation…", 120); return; }
        if (trade.LocalState != TradeState.SelectingTradeGoods || trade.Given.Gil != 0) { Stop(); return; }
        var slot = _batch[_slot];
        var manager = InventoryManager.Instance();
        var container = manager->GetInventoryContainer((InventoryType)slot.Bag);
        var item = container == null ? null : container->GetInventorySlot(slot.Slot);
        if (item is null || item->ItemId != slot.Key.ItemId || item->Quantity != slot.Available
            || ((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != slot.Key.HighQuality)
        { Stop(); return; }
        var agent = AgentTrade.Instance();
        if (agent is null) { Stop(); return; }
        agent->InventoryContextEvent.HandleCallback(slot.Slot, (InventoryType)slot.Bag, 0, 0);
        Set(slot.Available > 1 ? Phase.Quantity : Phase.VerifyOffer, "Setting the exact trade quantity…", 10);
    }

    private void Confirm()
    {
        var trade = Trade.Read();
        if (trade is null || !trade.IsTrading) { BeginSettling("Verifying the completed trade…"); return; }
        if (!CorrectPartner(trade) || !DeliveryPlan.MatchesExchange(trade.Given.Items, trade.Given.Gil, DeliveryKey, _batchQuantity, 0)
            || trade.Received.Items.Count != 0 || trade.Given.GilDisagrees || trade.Received.GilDisagrees)
        { Stop(); return; }
        if (_expectedGil >= 0 && trade.Received.Gil != _expectedGil)
        {
            Status = $"Waiting for {_expectedGil:N0} gil for this trade ({trade.Received.Gil:N0} offered).";
            _offerFingerprint = "";
            return;
        }
        var fingerprint = $"{_batchQuantity}:{trade.Received.Gil}";
        if (_offerFingerprint != fingerprint)
        { _offerFingerprint = fingerprint; _offerStableSince = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - _offerStableSince < TimeSpan.FromSeconds(1)) return;
        var manager = InventoryManager.Instance();
        if (manager is null || manager->TradeIsSyncPending) return;
        if (trade.LocalState is TradeState.SelectingTradeGoods or TradeState.LockedIn)
            if (TradeAutomation.ClickTrade()) Status = "Accepted the trade; waiting for confirmation…";
        if (trade.LocalState >= TradeState.LockedIn && TradeAutomation.ConfirmTrade())
            Status = "Confirmed our side; waiting for the recipient…";
    }
    private void Settle()
    {
        if (_receipt is null)
        {
            if (DateTime.UtcNow >= _settleUntil && Trade.Read() is { IsTrading: false }
                && PlayerInventory.Snapshot(PlayerInventory.Bags).GetValueOrDefault(DeliveryKey) == _before
                && PlayerInventory.CountGil() == _beforeGil)
            {
                _journal!.InFlight = 0;
                _journal.InFlightGil = 0;
                _journal.Save();
                EndDelivery("Trade ended without a delivery.");
            }
            return;
        }
        var receipt = _receipt;
        if (receipt.Evidence != TradeEvidence.Confirmed || receipt.PartnerKey != _job!.PostedBy
            || receipt.GilGiven != 0 || receipt.Given.Any(x => x.Key != DeliveryKey)
            || receipt.Given.Sum(x => x.Quantity) != _batchQuantity || receipt.Received.Count != 0
            || (_expectedGil >= 0 && receipt.GilReceived != _expectedGil))
        { Finish("The trade could not be verified exactly. Check the ledger and job before marking it delivered or restarting."); return; }
        if (_settings().ServerUrl != _server || _settings().BoardKey != _board)
        { Finish($"Delivered {_batchQuantity:N0} units, but the board settings changed. Record them on the original board before restarting."); return; }
        if (_batchQuantity > 0 && _rewardGil >= 0)
            _gilDue = DeliveryPlan.Payment(_rewardGil, _job!.Quantity, _job.Delivered + _delivered, _batchQuantity) - receipt.GilReceived;
        else if (_gilDue > 0) _gilDue -= receipt.GilReceived;
        _delivered += _batchQuantity;
        _journal!.Verified = _delivered;
        _journal.InFlight = 0;
        _journal.InFlightGil = 0;
        _journal.GilStillDue = _gilDue;
        _journal.Save();
        _watcher = null;
        _receipt = null;
        if (_cancel || (_delivered >= _toDeliver && _gilDue == 0))
        { EndDelivery(_cancel ? "Delivery stopped." : "Delivery finished."); return; }
        _refresh = _client.Refresh();
        Set(Phase.RefreshBatch, "Checking the job before the next trade…", 25);
        _next = DateTime.UtcNow.AddSeconds(2);
    }

    private void BeginSettling(string message)
    {
        _settleUntil = DateTime.UtcNow.AddSeconds(5);
        Set(Phase.Settle, message, 12);
    }

    private void EndDelivery(string reason)
    {
        _watcher = null;
        if (_gilDue > 0)
        { Finish($"{reason} {_gilDue:N0} gil is still due. Review the saved delivery record before restarting."); return; }
        if (_delivered == 0)
        {
            if (_journal is { InFlight: 0 }) DeliveryJournal.Clear();
            Finish(reason);
            return;
        }
        _finishReason = reason;
        _report = _client.CompleteVerified(_job!, _delivered, _owner);
        Set(Phase.Report, $"Recording {_delivered:N0} delivered units on the board…", 50);
    }

    private void Report()
    {
        if (_report is null || !_report.IsCompleted) return;
        if (!_report.IsCompletedSuccessfully || !_report.Result.Ok)
        { Finish($"The materials were traded, but the board update was not confirmed. Check the board before recording {_delivered:N0} units; do not trade them again."); return; }
        DeliveryJournal.Clear();
        _watcher = null;
        Finish($"{_finishReason} {_delivered:N0} units traded and recorded on the board.");
    }

    private void Set(Phase phase, string status, int seconds)
    {
        _phase = phase;
        Status = status;
        _deadline = DateTime.UtcNow.AddSeconds(seconds);
        DalamudServices.Log.Debug("Job delivery phase {Phase}: {Status}", phase, status);
    }

    private void Finish(string status)
    {
        _phase = Phase.Idle;
        _watcher = null;
        Status = status;
    }

    public void Dispose()
    {
        // No network work during teardown: a possibly completed POST must not be retried.
        AbortTravel();
        if (_phase is Phase.Request or Phase.Offer or Phase.Quantity or Phase.VerifyOffer or Phase.Confirm)
        {
            var manager = InventoryManager.Instance();
            if (manager is not null) manager->RefuseTrade();
        }
        DalamudServices.Framework.Update -= Tick;
    }
}
