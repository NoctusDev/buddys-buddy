using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BuddysBuddy.Modules.BuddysDesynther;

/// <summary>One item the game says came out of a desynthesis.</summary>
internal readonly record struct SalvageYield(uint ItemId, int Quantity);


/// <summary>
/// Interop for desynthesis.
/// </summary>
/// <remarks>
/// Confirmed by reflecting over the live FFXIVClientStructs on 2026-08-05 — see
/// knowledge/verified.md. The useful discovery is <c>AgentSalvage.DesynthResults</c>: the game
/// reports the yield itself, so the ledger does not have to infer it from an inventory diff. It
/// still does the diff as well, and disagreement between the two is recorded rather than hidden.
///
/// What is <em>not</em> confirmed is the exact confirm path — which callback value dismisses
/// <c>SalvageDialog</c>, and what the second and third arguments of <c>SalvageItem</c> want. Those
/// only resolve in game, which is what <see cref="Report"/> exists for.
///
/// Every method here must be called from the game thread.
/// </remarks>
internal static unsafe class Salvage
{
    /// <summary>The confirm dialog raised for a single item.</summary>
    public const string DialogAddon = "SalvageDialog";

    /// <summary>The window listing what came out.</summary>
    public const string ResultAddon = "SalvageResult";

    /// <summary>The game's own desynthesis window. Not needed — this module never opens it.</summary>
    public const string SelectorAddon = "SalvageItemSelector";

    /// <summary>The crafting classes that can desynthesise, by ClassJob row id.</summary>
    public static readonly (uint Id, string Name)[] Crafters =
    [
        (8, "Carpenter"), (9, "Blacksmith"), (10, "Armorer"), (11, "Goldsmith"),
        (12, "Leatherworker"), (13, "Weaver"), (14, "Alchemist"), (15, "Culinarian"),
    ];

    public static bool IsAddonOpen(string name)
    {
        var addon = DalamudServices.GameGui.GetAddonByName(name, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    public static bool IsDialogOpen() => IsAddonOpen(DialogAddon);

    public static bool IsResultOpen() => IsAddonOpen(ResultAddon);

    /// <summary>
    /// True while the game's own Desynthesis window is up.
    /// </summary>
    /// <remarks>
    /// Not required for anything — desynthesis works with it shut. Only reported by the diagnostic
    /// dump. <c>AgentSalvage.ItemList</c> was tried as the source of truth for what can be
    /// desynthesised and came back empty for the armoury categories, so the item sheet is used
    /// instead; see <see cref="DesynthScan"/>.
    /// </remarks>
    public static bool IsSelectorOpen() => IsAddonOpen(SelectorAddon);

    /// <summary>True when the agent exists at all — i.e. the game is far enough along to desynth.</summary>
    public static bool IsAvailable() => AgentSalvage.Instance() is not null;

    /// <summary>
    /// Units of an item sitting in one specific slot, or 0 if that slot no longer holds it.
    /// </summary>
    /// <remarks>
    /// This is how a desynthesis is confirmed. <c>InventoryManager.GetInventoryItemCount</c> is no
    /// use here: it takes a <c>checkArmory</c> flag that defaults off in this plugin's helper, so
    /// an armoury item counted 0 both before and after and the runner concluded nothing had
    /// happened — while the game had cheerfully destroyed it. Watching the exact slot works
    /// wherever the item lives. Confirmed in game 2026-08-05.
    /// </remarks>
    public static int SlotQuantity(InventoryType bag, ushort slot, uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var container = manager->GetInventoryContainer(bag);
        if (container is null || !container->IsLoaded || slot >= container->Size)
            return 0;

        var item = container->GetInventorySlot(slot);
        return item is null || item->ItemId != itemId ? 0 : item->Quantity;
    }

    /// <summary>Desynthesis level for one crafting class. Fractional, as the game tracks it.</summary>
    public static float DesynthLevel(uint classJobId)
    {
        var state = PlayerState.Instance();
        return state is null ? 0f : state->GetDesynthesisLevel(classJobId);
    }

    /// <summary>The class level at which desynthesis becomes available.</summary>
    public const int UnlockLevel = 30;

    /// <summary>
    /// Your level in a crafting class — not its desynthesis skill, the class itself.
    /// </summary>
    /// <remarks>
    /// Desynthesis is unlocked per class at level <see cref="UnlockLevel"/>, so this is what decides
    /// whether an item can be desynthesised at all. Read straight from the game rather than inferred
    /// from anything: `PlayerState.GetClassJobLevel`.
    /// </remarks>
    public static int ClassJobLevel(uint classJobId)
    {
        var state = PlayerState.Instance();
        return state is null || classJobId == 0 ? 0 : state->GetClassJobLevel((int)classJobId, false);
    }

    /// <summary>Whether desynthesis is available for the class that made an item.</summary>
    public static bool IsUnlocked(uint classJobId)
        => classJobId != 0 && ClassJobLevel(classJobId) >= UnlockLevel;

    /// <summary>
    /// Asks the game to desynthesise one inventory slot, which raises <see cref="DialogAddon"/>.
    /// </summary>
    /// <remarks>
    /// This is the same action as right-clicking an item and choosing Desynthesize, so no window
    /// needs to be open first. The slot is re-read here rather than taken from the caller's
    /// snapshot: a stale <c>InventoryItem*</c> is the one thing that could desynthesise the wrong
    /// item, so the item id is checked against what the caller expected before anything happens.
    /// </remarks>
    public static bool RequestDesynth(InventoryType bag, ushort slot, uint expectedItemId)
    {
        var agent = AgentSalvage.Instance();
        var manager = InventoryManager.Instance();
        if (agent is null || manager is null)
            return false;

        var container = manager->GetInventoryContainer(bag);
        if (container is null || !container->IsLoaded || slot >= container->Size)
            return false;

        var item = container->GetInventorySlot(slot);
        if (item is null || item->ItemId == 0)
            return false;

        // The slot moved or changed under us. Refuse rather than destroy something else.
        if (item->ItemId != expectedItemId)
        {
            DalamudServices.Log.Warning(
                "Desynth aborted: slot {Bag}/{Slot} holds {Actual}, expected {Expected}.",
                bag, slot, item->ItemId, expectedItemId);
            return false;
        }

        agent->SalvageItem(item, 0, 0);
        return true;
    }

    /// <summary>
    /// Confirms the desynthesis dialog.
    /// </summary>
    /// <remarks>
    /// The dialog carries a "desynthesize multiple items" checkbox (<c>BulkDesynthEnabled</c>).
    /// It is forced off: bulk mode changes which addon reports the results, and one item at a time
    /// is what makes a per-item yield ledger possible at all.
    /// </remarks>
    public static bool ConfirmDialog()
    {
        var dialog = DalamudServices.GameGui.GetAddonByName<AddonSalvageDialog>(DialogAddon, 1);
        if (dialog is null || !dialog->AtkUnitBase.IsVisible)
            return false;

        dialog->BulkDesynthEnabled = false;

        // Callback 0 is the Desynthesize button's path; -1 is Cancel.
        return dialog->AtkUnitBase.FireCallbackInt(0);
    }

    /// <summary>
    /// Dismisses the results window, trying harder on each successive call.
    /// </summary>
    /// <remarks>
    /// The first version fired callback -1 and returned true because the *call* succeeded, which is
    /// not the same as the window closing — so the fallbacks never ran and a run stalled on the
    /// first result window. Confirmed in game 2026-08-05.
    ///
    /// There is no way to know which of these an addon honours without trying, so the caller passes
    /// an escalating attempt number and stops when the window is actually gone. `Hide` is last
    /// because it bypasses the addon's own bookkeeping.
    /// </remarks>
    /// <summary>Backs out of the confirmation dialog, for when an item is being abandoned.</summary>
    public static void CancelDialog()
    {
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>(DialogAddon, 1);
        if (addon is null || !addon->IsVisible)
            return;

        addon->FireCallbackInt(-1);
    }

    /// <summary>How many distinct ways <see cref="CloseResult"/> knows to try.</summary>
    public const int CloseAttempts = 5;

    public static void CloseResult(int attempt)
    {
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>(ResultAddon, 1);
        if (addon is null || !addon->IsVisible)
            return;

        switch (attempt)
        {
            case 0:
                addon->FireCallbackInt(-1);
                break;
            case 1:
                addon->FireCallbackInt(0);
                break;
            case 2:
                addon->Close(true);
                break;
            case 3:
                addon->FireCloseCallback();
                break;
            default:
                addon->Hide(true, true, 0);
                break;
        }
    }

    /// <summary>
    /// Dumps the agent's own item list, raw.
    /// </summary>
    /// <remarks>
    /// The open question is what — beyond <c>Item.Desynth &gt; 0</c> — decides whether the game will
    /// desynthesise something. The agent builds that judgement itself: <c>ItemListAdd</c> takes a
    /// leading <c>bool meetsLevelRequirement</c>, and <c>SalvageListItem.Flags</c> is the obvious
    /// place for it to end up. An earlier attempt to read this list came back empty, which may have
    /// been the read being wrong rather than the list being empty — so this prints it unfiltered,
    /// with no cross-checking, next to what the item sheet says. Run it with the Desynthesis window
    /// open and the answer should be visible by comparison.
    /// </remarks>
    private static void AppendItemList(StringBuilder text)
    {
        var agent = AgentSalvage.Instance();
        if (agent is null)
            return;

        text.AppendLine($"AgentSalvage.ItemList (count {agent->ItemCount}, " +
                        $"pointer {(agent->ItemList is null ? "null" : "set")}):");

        if (agent->ItemList is null || agent->ItemCount == 0)
        {
            text.AppendLine("  (nothing — open the Desynthesis window and pick a category first)");
            return;
        }

        var sheet = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

        text.AppendLine("  flags  class  container/slot   qty   ilvl  name");

        for (var i = 0; i < agent->ItemCount; i++)
        {
            var row = agent->ItemList[i];
            var ilvl = sheet?.GetRowOrDefault(row.ItemId)?.LevelItem.RowId ?? 0;

            text.AppendLine(
                $"  0x{row.Flags:X2}   {row.ClassJob,-5}  {row.InventoryType}/{row.InventorySlot,-4} " +
                $"{row.Quantity,4}  {ilvl,5}  {GameItems.Name(row.ItemId)}");
        }
    }

    /// <summary>Which desynthesis windows are on screen, for a status line worth reading.</summary>
    public static string OpenWindows()
    {
        var open = new[] { DialogAddon, ResultAddon, SelectorAddon }.Where(IsAddonOpen).ToList();
        return open.Count == 0 ? "none" : string.Join(", ", open);
    }

    /// <summary>
    /// What the game says the last desynthesis produced.
    /// </summary>
    /// <remarks>
    /// Read straight off the agent. Trusted for <em>identity</em> — which items came out — but the
    /// caller still confirms quantities against the inventory, because that is the only evidence
    /// that survives being wrong about the agent's timing.
    /// </remarks>
    public static List<SalvageYield> ReadResults()
    {
        var results = new List<SalvageYield>();

        var agent = AgentSalvage.Instance();
        if (agent is null)
            return results;

        foreach (var result in agent->DesynthResults)
        {
            if (result.ItemId == 0 || result.Quantity <= 0)
                continue;

            results.Add(new SalvageYield(result.ItemId, result.Quantity));
        }

        return results;
    }

    /// <summary>
    /// A snapshot of everything desynthesis-related the game will tell us right now.
    /// </summary>
    /// <remarks>
    /// The confirm path could not be settled from metadata alone, so this exists to settle it from
    /// the running game: trigger it at each stage of a real desynthesis and the output says what the
    /// agent and addons actually did. Written to a file rather than the log so it can be read whole.
    /// </remarks>
    public static string Report()
    {
        var text = new StringBuilder();
        text.AppendLine($"Buddy's Desynther probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();

        var agent = AgentSalvage.Instance();
        text.AppendLine($"AgentSalvage.Instance()   : {(agent is null ? "null" : "present")}");

        if (agent is not null)
        {
            text.AppendLine($"  IsAgentActive           : {agent->IsAgentActive()}");
            text.AppendLine($"  AddonId                 : {agent->AddonId}");
            text.AppendLine($"  DesynthItemId           : {agent->DesynthItemId} ({GameItems.Name(agent->DesynthItemId)})");
            text.AppendLine($"  IsSalvageResultAddonOpen: {agent->IsSalvageResultAddonOpen}");
            text.AppendLine($"  SelectedCategory        : {agent->SelectedCategory}");
            text.AppendLine($"  ItemCount               : {agent->ItemCount}");

            text.AppendLine("  DesynthResults:");
            var any = false;
            foreach (var result in agent->DesynthResults)
            {
                if (result.ItemId == 0)
                    continue;

                any = true;
                text.AppendLine($"    {result.Quantity,5} x {GameItems.Name(result.ItemId)} (#{result.ItemId})");
            }

            if (!any)
                text.AppendLine("    (empty)");
        }

        text.AppendLine();
        text.AppendLine("Addons:");
        foreach (var name in new[] { DialogAddon, ResultAddon, SelectorAddon, "SalvageAutoDialog" })
        {
            var addon = DalamudServices.GameGui.GetAddonByName(name, 1);
            text.AppendLine(addon.IsNull
                ? $"  {name,-20}: not loaded"
                : $"  {name,-20}: ready={addon.IsReady} visible={addon.IsVisible} id={addon.Id} values={addon.AtkValuesCount}");
        }

        var dialog = DalamudServices.GameGui.GetAddonByName<AddonSalvageDialog>(DialogAddon, 1);
        if (dialog is not null)
        {
            text.AppendLine();
            text.AppendLine("SalvageDialog:");
            text.AppendLine($"  BulkDesynthEnabled      : {dialog->BulkDesynthEnabled}");
            text.AppendLine($"  DesynthesizeButton      : {(dialog->DesynthesizeButton is null ? "null" : "present")}");
            text.AppendLine($"  BulkDesynthCheckboxNode : {(dialog->BulkDesynthCheckboxNode is null ? "null" : "present")}");
        }

        text.AppendLine();
        text.AppendLine("Desynthesis levels:");
        foreach (var (id, name) in Crafters)
            text.AppendLine($"  {name,-14}: {DesynthLevel(id):F1}");

        text.AppendLine();
        AppendItemList(text);

        return text.ToString();
    }
}
