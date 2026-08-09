using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace BuddysBuddy.Modules.BuddysDesynther;

/// <summary>Where a desynthesisable item is kept.</summary>
internal enum DesynthSource
{
    Bags,
    Armoury,
}

/// <summary>One stack you own that the game will desynthesise.</summary>
internal sealed record DesynthItem
{
    public required DesynthSource Source { get; init; }

    public required InventoryType Bag { get; init; }

    public required ushort Slot { get; init; }

    public required uint ItemId { get; init; }

    public required bool HighQuality { get; init; }

    public required int Quantity { get; init; }

    public required string Name { get; init; }

    /// <summary>The desynthesis level this item needs — its item level.</summary>
    public required int Required { get; init; }

    /// <summary>Your desynthesis level in the class that made it.</summary>
    public required float Skill { get; init; }

    public required uint ClassJobId { get; init; }

    public required string ClassJobName { get; init; }

    /// <summary>Your level in that class — the thing that decides whether you can do this at all.</summary>
    public required int ClassJobLevel { get; init; }

    public required uint VendorPrice { get; init; }

    /// <summary>
    /// Whether desynthesis is available for the class that made this.
    /// </summary>
    /// <remarks>
    /// Desynthesis unlocks per crafting class at level 30. This is the one genuine restriction —
    /// item level is not — and it is read from the game rather than guessed at.
    /// </remarks>
    public bool Unlocked => Salvage.IsUnlocked(ClassJobId);

    /// <summary>Why this cannot be desynthesised, or null if it can.</summary>
    public string? Blocked => ClassJobId == 0
        ? "no crafting class repairs this, so nothing can desynthesise it"
        : Unlocked
            ? null
            : $"{ClassJobName} is level {ClassJobLevel} — desynthesis unlocks at {Salvage.UnlockLevel}";

    /// <summary>
    /// Whether your skill is below this item's level.
    /// </summary>
    /// <remarks>
    /// **Not a restriction.** Desynthesis is allowed at any level; being under-levelled means poorer
    /// yields and no skill gain. Shown as a hint and never used to exclude anything — see
    /// knowledge/verified.md for how this was got wrong twice.
    /// </remarks>
    public bool UnderLevelled => Skill < Required;

    public ItemKey Key => new(ItemId, HighQuality);
}

/// <summary>
/// Everything you own that can be desynthesised.
/// </summary>
/// <remarks>
/// Read straight from the item sheet — <c>Item.Desynth &gt; 0</c> is the game's own answer to "can
/// this be desynthesised", so there is nothing to configure and no rule of ours to be wrong about.
///
/// Bags and the armoury chest, never equipped gear. Reading the sheet rather than
/// <c>AgentSalvage.ItemList</c> means this works with the Desynthesis window shut, and does not
/// depend on which category tab happens to be selected.
/// </remarks>
internal static unsafe class DesynthScan
{
    /// <summary>The armoury chest, every slot category.</summary>
    private static readonly InventoryType[] Armoury =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    public static List<DesynthItem> Scan()
    {
        var found = new List<DesynthItem>();

        var manager = InventoryManager.Instance();
        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();

        if (manager is null || sheet is null)
            return found;

        var skills = Salvage.Crafters.ToDictionary(c => c.Id, c => Salvage.DesynthLevel(c.Id));
        var classLevels = Salvage.Crafters.ToDictionary(c => c.Id, c => Salvage.ClassJobLevel(c.Id));

        void Read(DesynthSource source, IEnumerable<InventoryType> containers)
        {
            foreach (var type in containers)
            {
                var container = manager->GetInventoryContainer(type);
                if (container is null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot is null || slot->ItemId == 0)
                        continue;

                    if (sheet.GetRowOrDefault(slot->ItemId) is not { } item || item.Desynth == 0)
                        continue;

                    var classJobId = item.ClassJobRepair.RowId;

                    found.Add(new DesynthItem
                    {
                        Source = source,
                        Bag = type,
                        Slot = (ushort)i,
                        ItemId = slot->ItemId,
                        HighQuality = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                        Quantity = slot->Quantity,
                        Name = GameItems.Name(slot->ItemId),
                        Required = (int)item.LevelItem.RowId,
                        Skill = skills.GetValueOrDefault(classJobId),
                        ClassJobId = classJobId,
                        ClassJobName = Salvage.Crafters.FirstOrDefault(c => c.Id == classJobId).Name ?? "—",
                        ClassJobLevel = classLevels.GetValueOrDefault(classJobId),
                        VendorPrice = item.PriceLow,
                    });
                }
            }
        }

        Read(DesynthSource.Bags, PlayerInventory.Bags);
        Read(DesynthSource.Armoury, Armoury);

        // Cheapest first: if anything goes wrong, it goes wrong on the least valuable thing.
        found.Sort((a, b) =>
        {
            var byValue = a.VendorPrice.CompareTo(b.VendorPrice);
            return byValue != 0 ? byValue : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return found;
    }

    public static string Label(DesynthSource source)
        => source == DesynthSource.Bags ? "Bags" : "Armoury chest";
}
