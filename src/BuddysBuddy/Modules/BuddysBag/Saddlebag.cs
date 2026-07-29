using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace BuddysBuddy.Modules.BuddysBag;

/// <summary>
/// Thin interop layer over the chocobo saddlebag.
/// </summary>
/// <remarks>
/// The game only permits saddlebag reads and moves while its window (the <c>InventoryBuddy</c>
/// addon) is open — with it closed the containers report as not loaded and moves silently fail, so
/// callers must gate on <see cref="IsWindowOpen"/>.
///
/// Every method here must be called from the game thread.
/// </remarks>
internal static unsafe class Saddlebag
{
    public const string AddonName = "InventoryBuddy";

    /// <summary>The two free saddlebag pages, held by every player.</summary>
    public static readonly InventoryType[] StandardBags =
        [InventoryType.SaddleBag1, InventoryType.SaddleBag2];

    /// <summary>The extra pages from the Companion premium subscription; not loaded without it.</summary>
    public static readonly InventoryType[] PremiumBags =
        [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];

    /// <summary>The four main player inventory pages — the move destination.</summary>
    public static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static IEnumerable<InventoryType> BagsToDrain(bool includePremium)
        => includePremium ? StandardBags.Concat(PremiumBags) : StandardBags;

    /// <summary>True while the saddlebag window is on screen, which is when moves are permitted.</summary>
    public static bool IsWindowOpen()
    {
        var addon = DalamudServices.GameGui.GetAddonByName(AddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    /// <summary>Asks the game to open the saddlebag window. It may refuse while occupied.</summary>
    public static void RequestOpenWindow()
    {
        var module = AgentModule.Instance();
        if (module is null)
            return;

        var agent = module->GetAgentByInternalId(AgentId.InventoryBuddy);
        if (agent is not null && !agent->IsAgentActive())
            agent->Show();
    }

    /// <summary>Number of items still sitting in the saddlebag.</summary>
    public static int CountItems(bool includePremium)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var total = 0;
        foreach (var bag in BagsToDrain(includePremium))
        {
            var container = manager->GetInventoryContainer(bag);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is not null && item->ItemId != 0)
                    total++;
            }
        }

        return total;
    }

    /// <summary>Free slots across the four player inventory pages.</summary>
    public static int CountFreePlayerSlots()
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var free = 0;
        foreach (var bag in PlayerBags)
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

    /// <summary>Finds the first occupied saddlebag slot, scanning pages in order.</summary>
    public static bool TryFindNextItem(bool includePremium, out InventoryType bag, out ushort slot, out uint itemId)
    {
        bag = default;
        slot = 0;
        itemId = 0;

        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        foreach (var candidate in BagsToDrain(includePremium))
        {
            var container = manager->GetInventoryContainer(candidate);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is null || item->ItemId == 0)
                    continue;

                bag = candidate;
                slot = (ushort)i;
                itemId = item->ItemId;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first empty player inventory slot.
    /// </summary>
    /// <remarks>
    /// Deliberately only targets <em>empty</em> slots. Moving onto an occupied slot swaps the two
    /// items rather than merging them, which would push an inventory item into the saddlebag —
    /// the opposite of the point. Partial stacks therefore do not merge; that is the safe trade.
    /// </remarks>
    public static bool TryFindFreePlayerSlot(out InventoryType bag, out ushort slot)
    {
        bag = default;
        slot = 0;

        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        foreach (var candidate in PlayerBags)
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

    /// <summary>Issues the move. Returns the game's status code; 0 is success.</summary>
    public static int Move(InventoryType src, ushort srcSlot, InventoryType dst, ushort dstSlot)
    {
        var manager = InventoryManager.Instance();
        return manager is null ? -1 : manager->MoveItemSlot(src, srcSlot, dst, dstSlot, true);
    }

    /// <summary>Item name for logging, falling back to the raw id if the sheet has no row.</summary>
    public static string ItemName(uint itemId)
    {
        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        var row = sheet?.GetRowOrDefault(itemId);
        var name = row?.Name.ToString();
        return string.IsNullOrEmpty(name) ? $"item #{itemId}" : name;
    }
}
