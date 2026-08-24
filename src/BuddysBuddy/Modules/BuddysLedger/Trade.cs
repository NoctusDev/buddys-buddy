using System;
using System.Collections.Generic;
using System.Text;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;

namespace BuddysBuddy.Modules.BuddysLedger;

/// <summary>What one side of the trade window is offering.</summary>
public sealed class TradeOffer
{
    public Dictionary<ItemKey, int> Items { get; } = new();

    /// <summary>Gil found in the trade slots as item id 1, or -1 if no slot held any.</summary>
    public long GilInSlots { get; set; } = -1;

    /// <summary>Gil as the trade window's own number array reports it, or -1 if it was unreadable.</summary>
    public long GilInArray { get; set; } = -1;

    /// <summary>
    /// The gil on the table.
    /// </summary>
    /// <remarks>
    /// Two sources, read independently, and **neither one is sufficient alone** — confirmed in game
    /// 2026-08-23. The partner's gil sits in their sixth trade slot as item id 1; your own does
    /// <em>not</em> appear in your slots at all and is only ever reported by
    /// <c>TradeNumberArray.SendGilCount</c>. So the slot reading is preferred where it exists —
    /// it comes from <see cref="InventoryManager"/>, which outlives the addon — and the array
    /// carries the rest. <see cref="GilDisagrees"/> flags the case that should never happen.
    /// </remarks>
    public long Gil => GilInSlots >= 0 ? GilInSlots : Math.Max(0, GilInArray);

    public bool GilDisagrees => GilInSlots >= 0 && GilInArray >= 0 && GilInSlots != GilInArray;

    public bool IsEmpty => Gil == 0 && Items.Count == 0;
}

/// <summary>Both sides of the trade window as they stood on one frame.</summary>
public sealed class TradeSnapshot
{
    public TradeOffer Given { get; } = new();

    public TradeOffer Received { get; } = new();

    public TradeState LocalState { get; set; }

    public TradeState RemoteState { get; set; }

    public string PartnerName { get; set; } = string.Empty;

    public uint PartnerEntityId { get; set; }

    /// <summary>
    /// Whether a trade is actually open.
    /// </summary>
    /// <remarks>
    /// Checked against the known range rather than written as "not <see cref="TradeState.NotTrading"/>".
    /// The enum starts at 1, so zeroed or not-yet-populated memory reads as a value that is not any
    /// member — and "not NotTrading" would call that a trade in progress and start recording one.
    /// </remarks>
    public bool IsTrading
        => LocalState >= TradeState.TradeRequestPending && LocalState <= TradeState.Confirmed;

    /// <summary>
    /// Both sides have locked their offers in — the deal as it will be struck.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> "both confirmed". Observed in game 2026-08-23: the local side sits
    /// at <see cref="TradeState.WaitingForConfirmation"/> while the remote side reads
    /// <see cref="TradeState.Confirmed"/>, and the trade then completes without
    /// <c>TradeLocalState</c> ever being seen as <c>Confirmed</c> — so a both-confirmed test never
    /// fires and every trade falls through to being rebuilt from the inventory.
    ///
    /// Locked-in is the honest line anyway: it is the point where the contents stop changing. It is
    /// not a promise the trade completed, and nothing here treats it as one — the inventory decides
    /// that.
    /// </remarks>
    public bool BothLockedIn => LocalState >= TradeState.LockedIn && RemoteState >= TradeState.LockedIn;

    public bool IsEmpty => Given.IsEmpty && Received.IsEmpty;

    /// <summary>
    /// How far along the trade is, both sides together.
    /// </summary>
    /// <remarks>
    /// Used to decide which of several readings describes the deal that was actually struck: a
    /// later reading at the same or a higher rank replaces an earlier one, so removing an item
    /// before confirming is picked up, while the window clearing on completion is not.
    /// </remarks>
    public int Rank => (int)LocalState + (int)RemoteState;
}

/// <summary>
/// Reads the live trade window out of the game.
/// </summary>
/// <remarks>
/// Everything here comes off <see cref="InventoryManager"/>, which needs no addon open and no
/// pointer into the UI — confirmed by reflecting over the live <c>FFXIVClientStructs.dll</c> on
/// 2026-08-23. Gil is the one thing the manager's item slots may not carry, so the UI's
/// <see cref="TradeNumberArray"/> is read alongside it and the two are compared rather than one
/// being trusted.
///
/// Must be called from the game thread.
/// </remarks>
public static unsafe class Trade
{
    /// <summary>Reads both sides of the trade window, or null if the game is not ready.</summary>
    public static TradeSnapshot? Read()
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return null;

        var snapshot = new TradeSnapshot
        {
            LocalState = manager->TradeLocalState,
            RemoteState = manager->TradeRemoteState,
            PartnerEntityId = manager->TradePartnerEntityId,
            PartnerName = manager->TradePartnerNameString ?? string.Empty,
        };

        if (!snapshot.IsTrading)
            return snapshot;

        Fill(snapshot.Given, manager->TradeItemsLocal);
        Fill(snapshot.Received, manager->TradeItemsRemote);

        var numbers = TradeNumberArray.Instance();
        if (numbers is not null)
        {
            snapshot.Given.GilInArray = numbers->SendGilCount;
            snapshot.Received.GilInArray = numbers->ReceiveGilCount;
        }

        return snapshot;
    }

    /// <summary>The character's gil, read out of the currency container.</summary>
    public static long CountGil() => PlayerInventory.CountGil();

    /// <summary>
    /// The trade partner's home world, or null if it could not be resolved.
    /// </summary>
    /// <remarks>
    /// Only answerable while the partner is loaded in the object table, which is true for the whole
    /// of a trade — you have to be standing next to them. Resolve it early and keep it: a name on
    /// its own is not an identity, because the same name exists on other worlds.
    /// </remarks>
    public static string? PartnerWorld(uint entityId)
    {
        if (entityId == 0)
            return null;

        if (DalamudServices.ObjectTable.SearchByEntityId(entityId) is not IPlayerCharacter player)
            return null;

        var world = player.HomeWorld.ValueNullable?.Name.ToString();
        return string.IsNullOrEmpty(world) ? null : world;
    }

    /// <summary>
    /// Dumps the live trade state as text.
    /// </summary>
    /// <remarks>
    /// The same idea as <c>/bb desynth probe</c>: run it at each stage of a real trade and the
    /// behavioural questions the metadata cannot answer — whether gil rides in the item slots, and
    /// what the states do on completion versus cancellation — answer themselves.
    /// </remarks>
    public static string Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# trade probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var manager = InventoryManager.Instance();
        if (manager is null)
        {
            sb.AppendLine("InventoryManager.Instance() is null.");
            return sb.ToString();
        }

        sb.AppendLine($"TradeLocalState      : {manager->TradeLocalState} ({(int)manager->TradeLocalState})");
        sb.AppendLine($"TradeRemoteState     : {manager->TradeRemoteState} ({(int)manager->TradeRemoteState})");
        // TradeLockedIn is private on InventoryManager; TradeNumberArray.IsLockedIn below is the
        // readable equivalent.
        sb.AppendLine($"TradeIsSyncPending   : {manager->TradeIsSyncPending}");
        sb.AppendLine($"TradeWarnIfMovedTooFar: {manager->TradeWarnIfMovedTooFar}");
        sb.AppendLine($"TradePartnerName     : {manager->TradePartnerNameString}");
        sb.AppendLine($"TradePartnerEntityId : {manager->TradePartnerEntityId:X}");
        sb.AppendLine($"resolved world       : {PartnerWorld(manager->TradePartnerEntityId) ?? "(unresolved)"}");

        DumpSlots(sb, "TradeItemsLocal", manager->TradeItemsLocal);
        DumpSlots(sb, "TradeItemsRemote", manager->TradeItemsRemote);

        var numbers = TradeNumberArray.Instance();
        if (numbers is null)
        {
            sb.AppendLine();
            sb.AppendLine("TradeNumberArray.Instance() is null — the trade addon is not up.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("TradeNumberArray:");
            sb.AppendLine($"  SendGilCount    : {numbers->SendGilCount}");
            sb.AppendLine($"  ReceiveGilCount : {numbers->ReceiveGilCount}");
            sb.AppendLine($"  SelfState       : {numbers->SelfState}");
            sb.AppendLine($"  OtherState      : {numbers->OtherState}");
            sb.AppendLine($"  IsLockedIn      : {numbers->IsLockedIn}");
        }

        var strings = TradeStringArray.Instance();
        if (strings is not null)
        {
            sb.AppendLine();
            sb.AppendLine("TradeStringArray:");
            sb.AppendLine($"  OwnName          : {strings->OwnName}");
            sb.AppendLine($"  TradePartnerName : {strings->TradePartnerName}");
        }

        sb.AppendLine();
        sb.AppendLine($"character gil: {CountGil():N0}");
        return sb.ToString();
    }

    private static void DumpSlots(StringBuilder sb, string label, Span<InventoryItem> slots)
    {
        sb.AppendLine();
        sb.AppendLine($"{label} ({slots.Length} slots):");

        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].ItemId == 0)
            {
                sb.AppendLine($"  [{i}] empty");
                continue;
            }

            var hq = (slots[i].Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? " HQ" : string.Empty;
            sb.AppendLine(
                $"  [{i}] {slots[i].ItemId} x{slots[i].Quantity}{hq}  " +
                $"container={slots[i].Container} slot={slots[i].Slot} flags={slots[i].Flags}");
        }
    }

    /// <summary>
    /// Reads one side's slots into an offer.
    /// </summary>
    /// <remarks>
    /// The array is six slots wide where the window shows five, and the sixth is gil as item id 1 —
    /// but **only on the partner's side**; your own gil never appears there. Confirmed in game
    /// 2026-08-23. That asymmetry is why the two gil readings are kept apart on
    /// <see cref="TradeOffer"/> rather than resolved here.
    /// </remarks>
    private static void Fill(TradeOffer offer, Span<InventoryItem> slots)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            var itemId = slots[i].ItemId;
            if (itemId == 0)
                continue;

            if (itemId == PlayerInventory.GilItemId)
            {
                offer.GilInSlots = Math.Max(0, offer.GilInSlots) + slots[i].Quantity;
                continue;
            }

            var key = new ItemKey(itemId, (slots[i].Flags & InventoryItem.ItemFlags.HighQuality) != 0);
            offer.Items[key] = offer.Items.GetValueOrDefault(key) + slots[i].Quantity;
        }
    }
}
