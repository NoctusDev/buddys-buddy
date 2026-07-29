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
