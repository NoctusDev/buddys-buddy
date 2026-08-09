using System.Collections.Generic;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysSlaves;

/// <summary>One retainer as listed at the bell, in display order.</summary>
internal readonly record struct RetainerInfo(ulong Id, string Name, int SortIndex, byte ItemCount, uint Gil);

/// <summary>One occupied slot in a retainer's inventory.</summary>
internal readonly record struct RetainerSlot(
    ulong RetainerId,
    InventoryType Page,
    ushort Slot,
    uint ItemId,
    bool HighQuality,
    int Quantity);

/// <summary>
/// Interop for retainer inventories.
/// </summary>
/// <remarks>
/// Only the retainer currently open at a bell has its pages loaded; every other retainer's
/// containers are empty. A complete picture therefore has to be accumulated across visits, exactly
/// like the free company chest — see <see cref="RetainerTally"/>.
///
/// Must be called from the game thread.
/// </remarks>
internal static unsafe class Retainers
{
    /// <summary>The retainer inventory window, in its normal and expanded forms.</summary>
    public static readonly string[] AddonNames = ["InventoryRetainer", "InventoryRetainerLarge"];

    /// <summary>The seven inventory pages plus crystals. Market and equipped items are left alone.</summary>
    public static readonly InventoryType[] Pages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    public static int PageOrder(InventoryType page) => page switch
    {
        InventoryType.RetainerPage1 => 0,
        InventoryType.RetainerPage2 => 1,
        InventoryType.RetainerPage3 => 2,
        InventoryType.RetainerPage4 => 3,
        InventoryType.RetainerPage5 => 4,
        InventoryType.RetainerPage6 => 5,
        InventoryType.RetainerPage7 => 6,
        InventoryType.RetainerCrystals => 7,
        _ => 99,
    };

    public static string PageLabel(InventoryType page)
        => page == InventoryType.RetainerCrystals ? "Crystals" : $"Bag {PageOrder(page) + 1}";

    /// <summary>True while a retainer's inventory window is open.</summary>
    public static bool IsInventoryOpen()
    {
        foreach (var name in AddonNames)
        {
            var addon = DalamudServices.GameGui.GetAddonByName(name, 1);
            if (!addon.IsNull && addon.IsReady && addon.IsVisible)
                return true;
        }

        return false;
    }

    /// <summary>Every retainer, in the order they appear at the bell — top to bottom.</summary>
    public static List<RetainerInfo> ListInDisplayOrder()
    {
        var list = new List<RetainerInfo>();

        var manager = RetainerManager.Instance();
        if (manager is null || !manager->IsReady)
            return list;

        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer is null || retainer->RetainerId == 0)
                continue;

            list.Add(new RetainerInfo(
                retainer->RetainerId, retainer->NameString, (int)i, retainer->ItemCount, retainer->Gil));
        }

        return list;
    }

    /// <summary>The retainer whose inventory is currently loaded, if any.</summary>
    public static RetainerInfo? GetActive()
    {
        var manager = RetainerManager.Instance();
        if (manager is null || !manager->IsReady)
            return null;

        var active = manager->GetActiveRetainer();
        if (active is null || active->RetainerId == 0)
            return null;

        // Recover the display position so ordering stays consistent with the bell list.
        var sortIndex = 0;
        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var candidate = manager->GetRetainerBySortedIndex(i);
            if (candidate is not null && candidate->RetainerId == active->RetainerId)
            {
                sortIndex = (int)i;
                break;
            }
        }

        return new RetainerInfo(active->RetainerId, active->NameString, sortIndex, active->ItemCount, active->Gil);
    }

    /// <summary>True once the open retainer's page has been sent by the server.</summary>
    public static bool IsPageLoaded(InventoryType page)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        var container = manager->GetInventoryContainer(page);
        return container is not null && container->IsLoaded && container->Size > 0;
    }

    /// <summary>Reads every occupied slot of one page of the open retainer.</summary>
    public static List<RetainerSlot> ReadPage(ulong retainerId, InventoryType page)
    {
        var slots = new List<RetainerSlot>();

        var manager = InventoryManager.Instance();
        if (manager is null)
            return slots;

        var container = manager->GetInventoryContainer(page);
        if (container is null || !container->IsLoaded)
            return slots;

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item is null || item->ItemId == 0)
                continue;

            var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            slots.Add(new RetainerSlot(retainerId, page, (ushort)i, item->ItemId, hq, item->Quantity));
        }

        return slots;
    }

    /// <summary>
    /// Starts moving a stack out of the open retainer.
    /// </summary>
    /// <remarks>
    /// This only begins the withdrawal: the game raises the quantity prompt, and nothing arrives
    /// until that is confirmed. See <see cref="Game.QuantityPrompt"/>.
    /// </remarks>
    public static int MoveOut(InventoryType srcPage, ushort srcSlot, InventoryType dstBag, ushort dstSlot)
    {
        var manager = InventoryManager.Instance();
        return manager is null ? -1 : manager->MoveItemSlot(srcPage, srcSlot, dstBag, dstSlot, true);
    }
}
