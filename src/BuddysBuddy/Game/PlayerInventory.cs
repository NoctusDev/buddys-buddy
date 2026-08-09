using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Game;

/// <summary>
/// The player's four main inventory pages — the destination for everything this plugin moves.
/// </summary>
/// <remarks>Must be called from the game thread.</remarks>
public static unsafe class PlayerInventory
{
    public static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>
    /// Everywhere something you are given can land: the four bags, plus the crystals container.
    /// </summary>
    /// <remarks>
    /// Shards, crystals and clusters do <em>not</em> go into a bag slot — they have their own
    /// container. A "what did I just gain" snapshot over <see cref="Bags"/> alone silently misses
    /// them, which is exactly what happened to the first desynthesis ledger: the game reported
    /// "6x Fire Shard" and the diff saw nothing. Confirmed in game 2026-08-05.
    /// </remarks>
    public static readonly InventoryType[] Receivable =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    /// <summary>Empty slots across all four pages.</summary>
    public static int CountFreeSlots()
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var free = 0;
        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is not null && item->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>
    /// Finds the first empty slot.
    /// </summary>
    /// <remarks>
    /// Only empty slots are ever targeted. Moving onto an occupied slot swaps the two items, which
    /// would push an inventory item back into the container being emptied.
    /// </remarks>
    public static bool TryFindFreeSlot(out InventoryType bag, out ushort slot)
    {
        bag = default;
        slot = 0;

        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        foreach (var candidate in Bags)
        {
            var container = manager->GetInventoryContainer(candidate);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is null || item->ItemId != 0)
                    continue;

                bag = candidate;
                slot = (ushort)i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Total units per item across a set of containers.
    /// </summary>
    /// <remarks>
    /// Two of these taken either side of an action, subtracted, are proof of what it actually did —
    /// which is how both withdrawals and desynthesis yields are confirmed here. Containers that are
    /// not loaded are skipped, so a snapshot never invents a zero.
    /// </remarks>
    public static Dictionary<ItemKey, int> Snapshot(IEnumerable<InventoryType> containers)
    {
        var totals = new Dictionary<ItemKey, int>();

        var manager = InventoryManager.Instance();
        if (manager is null)
            return totals;

        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is null || item->ItemId == 0)
                    continue;

                var key = new ItemKey(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
                totals[key] = totals.GetValueOrDefault(key) + item->Quantity;
            }
        }

        return totals;
    }

    /// <summary>What <paramref name="after"/> has that <paramref name="before"/> did not.</summary>
    public static Dictionary<ItemKey, int> Gained(
        IReadOnlyDictionary<ItemKey, int> before, IReadOnlyDictionary<ItemKey, int> after)
    {
        var gained = new Dictionary<ItemKey, int>();

        foreach (var (key, quantity) in after)
        {
            var delta = quantity - before.GetValueOrDefault(key);
            if (delta > 0)
                gained[key] = delta;
        }

        return gained;
    }

    /// <summary>
    /// Units of an item already in the player's bags.
    /// </summary>
    /// <remarks>
    /// This is the only reliable proof that a withdrawal actually landed — the move calls and the
    /// quantity prompt callback report nothing useful. Compare before and after.
    /// </remarks>
    public static int CountItem(uint itemId, bool highQuality)
    {
        var manager = InventoryManager.Instance();
        return manager is null
            ? 0
            : manager->GetInventoryItemCount(itemId, highQuality, checkEquipped: false, checkArmory: false, 0);
    }
}
