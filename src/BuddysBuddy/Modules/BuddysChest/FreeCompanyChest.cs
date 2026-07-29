using System.Collections.Generic;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BuddysBuddy.Modules.BuddysChest;

/// <summary>
/// Interop for the free company chest.
/// </summary>
/// <remarks>
/// The chest's pages are fetched from the server one compartment at a time: only the compartment
/// currently being viewed reports <c>IsLoaded</c>, and the others sit empty until visited. Anything
/// that needs a complete picture has to accumulate across visits — see <see cref="ChestTally"/>.
///
/// Every method here must be called from the game thread.
/// </remarks>
internal static unsafe class FreeCompanyChest
{
    public const string AddonName = "FreeCompanyChest";

    /// <summary>The five item compartments.</summary>
    public static readonly InventoryType[] ItemPages =
    [
        InventoryType.FreeCompanyPage1,
        InventoryType.FreeCompanyPage2,
        InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4,
        InventoryType.FreeCompanyPage5,
    ];

    /// <summary>The crystals compartment, scanned alongside the item pages.</summary>
    public const InventoryType Crystals = InventoryType.FreeCompanyCrystals;

    /// <summary>Every compartment worth tallying.</summary>
    public static IEnumerable<InventoryType> AllPages
    {
        get
        {
            foreach (var page in ItemPages)
                yield return page;

            yield return Crystals;
        }
    }

    public static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>Human label for a compartment, matching the game's tab order.</summary>
    public static string PageLabel(InventoryType page) => page switch
    {
        InventoryType.FreeCompanyPage1 => "Compartment 1",
        InventoryType.FreeCompanyPage2 => "Compartment 2",
        InventoryType.FreeCompanyPage3 => "Compartment 3",
        InventoryType.FreeCompanyPage4 => "Compartment 4",
        InventoryType.FreeCompanyPage5 => "Compartment 5",
        InventoryType.FreeCompanyCrystals => "Crystals",
        _ => page.ToString(),
    };

    /// <summary>Compartment order for display and withdrawal: pages 1-5, then crystals.</summary>
    public static int PageOrder(InventoryType page) => page switch
    {
        InventoryType.FreeCompanyPage1 => 0,
        InventoryType.FreeCompanyPage2 => 1,
        InventoryType.FreeCompanyPage3 => 2,
        InventoryType.FreeCompanyPage4 => 3,
        InventoryType.FreeCompanyPage5 => 4,
        InventoryType.FreeCompanyCrystals => 5,
        _ => 99,
    };

    /// <summary>True while the chest window is on screen.</summary>
    public static bool IsWindowOpen()
    {
        var addon = DalamudServices.GameGui.GetAddonByName(AddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    /// <summary>
    /// True when the chest is genuinely open and active — the window is up and its agent is live.
    /// </summary>
    /// <remarks>
    /// Withdrawal is gated on this. The chest must already be open and targeted; this plugin never
    /// opens it, by design.
    /// </remarks>
    public static bool IsChestReady()
    {
        if (!IsWindowOpen())
            return false;

        var agent = AgentFreeCompanyChest.Instance();
        return agent is not null && agent->IsAgentActive();
    }

    /// <summary>
    /// True once the server has sent this compartment's contents. Compartments other than the one
    /// on screen report false, which is the "loading" state seen in the UI.
    /// </summary>
    public static bool IsPageLoaded(InventoryType page)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        var container = manager->GetInventoryContainer(page);
        return container is not null && container->IsLoaded && container->Size > 0;
    }

    /// <summary>Reads every occupied slot of a compartment. Returns empty if it is not loaded yet.</summary>
    public static List<ChestSlot> ReadPage(InventoryType page)
    {
        var slots = new List<ChestSlot>();

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
            slots.Add(new ChestSlot(page, (ushort)i, item->ItemId, hq, item->Quantity));
        }

        return slots;
    }

    /// <summary>Free slots across the player's four inventory pages.</summary>
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

    /// <summary>Finds the first empty player inventory slot; only empty slots are ever targeted.</summary>
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

    /// <summary>
    /// Moves a whole stack out of the chest.
    /// </summary>
    /// <remarks>
    /// Uses the chest agent's own move rather than <c>InventoryManager.MoveItemSlot</c>. It takes no
    /// quantity argument, which is what makes "take everything of this item" possible without
    /// driving the game's quantity prompt.
    /// </remarks>
    public static bool MoveOut(InventoryType srcPage, ushort srcSlot, InventoryType dstBag, ushort dstSlot)
    {
        var agent = AgentFreeCompanyChest.Instance();
        if (agent is null)
            return false;

        agent->MoveItemInChest(srcPage, srcSlot, dstBag, dstSlot);
        return true;
    }

    /// <summary>Current free company gil.</summary>
    public static uint GetGil()
    {
        var manager = InventoryManager.Instance();
        return manager is null ? 0 : manager->GetFreeCompanyGil();
    }

    /// <summary>Units of an item already in the player's bags — used to confirm a withdrawal landed.</summary>
    public static int CountInPlayerInventory(uint itemId, bool highQuality)
    {
        var manager = InventoryManager.Instance();
        return manager is null
            ? 0
            : manager->GetInventoryItemCount(itemId, highQuality, checkEquipped: false, checkArmory: false, 0);
    }
}

/// <summary>
/// The "how many?" dialog the game raises when taking a stack out of the chest.
/// </summary>
/// <remarks>
/// Taking from the free company chest always raises this prompt — a move on its own never completes
/// the withdrawal, which is why the first version appeared to do nothing. The prompt already
/// defaults to the full stack, so confirming it is all that is required.
/// </remarks>
internal static unsafe class QuantityPrompt
{
    public const string AddonName = "InputNumeric";

    public static bool IsOpen()
    {
        var addon = DalamudServices.GameGui.GetAddonByName(AddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    /// <summary>
    /// Confirms the prompt at <paramref name="quantity"/>, which should be the whole stack.
    /// </summary>
    /// <remarks>
    /// Fires the addon's callback with a single integer and <c>close: true</c>, the same path the
    /// OK button takes. The dialog defaults to the maximum, so this matches what is on screen.
    /// </remarks>
    public static bool Confirm(int quantity)
    {
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>(AddonName, 1);
        if (addon is null || !addon->IsVisible)
            return false;

        var value = stackalloc AtkValue[1];
        value->Type = AtkValueType.Int;
        value->Int = quantity;

        return addon->FireCallback(1, value, true);
    }
}

/// <summary>One occupied slot in the chest.</summary>
internal readonly record struct ChestSlot(
    InventoryType Page,
    ushort Slot,
    uint ItemId,
    bool HighQuality,
    int Quantity);
