using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysChest;

/// <summary>A single item's presence across the whole chest.</summary>
internal sealed class ItemTotal
{
    public required ItemKey Key { get; init; }

    public required string Name { get; init; }

    /// <summary>Total units across every compartment.</summary>
    public int Quantity { get; set; }

    /// <summary>How many separate stacks make up <see cref="Quantity"/>.</summary>
    public int Stacks { get; set; }

    /// <summary>Compartments this item was found in, in tab order.</summary>
    public List<InventoryType> Pages { get; } = [];
}

/// <summary>
/// Running tally of the free company chest, accumulated across compartments.
/// </summary>
/// <remarks>
/// The chest never has more than one compartment loaded at a time, so a single pass can only ever
/// see a fraction of the contents. This keeps a per-compartment snapshot, refreshing whichever
/// compartment happens to be loaded, and reports the merged totals plus which compartments have
/// actually been seen — so a partial tally is always visibly partial rather than quietly wrong.
/// </remarks>
internal sealed class ChestTally : IItemSourceProvider
{
    private readonly Dictionary<InventoryType, List<ChestSlot>> _snapshots = new();
    private readonly Dictionary<InventoryType, DateTime> _scannedAt = new();
    private readonly Dictionary<InventoryType, SnapshotSource> _sources = new();

    private List<ItemTotal>? _totals;

    /// <summary>
    /// One source per compartment, so Buddy's Stash can answer for the chest without this module
    /// being on screen.
    /// </summary>
    /// <remarks>
    /// Ids are not scoped to a particular free company — the game gives no cheap handle for that,
    /// and the chest is cleared on open by default. Belonging to two FCs will therefore blur their
    /// stored tallies together; the live reading is always correct.
    /// </remarks>
    public IEnumerable<IItemSource> GetSources() => _sources.Values;

    private SnapshotSource SourceFor(InventoryType page)
    {
        if (!_sources.TryGetValue(page, out var source))
            _sources[page] = source = new SnapshotSource(
                $"fc:{FreeCompanyChest.PageOrder(page)}", FreeCompanyChest.PageLabel(page), "Free company");

        return source;
    }

    /// <summary>Totals for one compartment, for the source view.</summary>
    private static Dictionary<ItemKey, int> Fold(List<ChestSlot> slots)
    {
        var map = new Dictionary<ItemKey, int>();

        foreach (var slot in slots)
        {
            var key = new ItemKey(slot.ItemId, slot.HighQuality);
            map[key] = map.GetValueOrDefault(key) + slot.Quantity;
        }

        return map;
    }

    /// <summary>Compartments with a snapshot, in tab order.</summary>
    public IEnumerable<InventoryType> ScannedPages
        => FreeCompanyChest.AllPages.Where(_snapshots.ContainsKey);

    /// <summary>Compartments not yet seen — the tally is incomplete until this is empty.</summary>
    public IEnumerable<InventoryType> UnscannedPages
        => FreeCompanyChest.AllPages.Where(p => !_snapshots.ContainsKey(p));

    public int ScannedCount => _snapshots.Count;

    public int TotalPageCount => FreeCompanyChest.AllPages.Count();

    public bool IsComplete => ScannedCount == TotalPageCount;

    public DateTime? ScannedAt(InventoryType page)
        => _scannedAt.TryGetValue(page, out var at) ? at : null;

    /// <summary>
    /// Re-reads every compartment the game currently has loaded. Cheap enough to call on a timer;
    /// compartments that are not loaded are left as they were rather than being cleared.
    /// </summary>
    /// <returns>True if any snapshot changed, meaning the totals need rebuilding.</returns>
    public bool Refresh()
    {
        var changed = false;

        foreach (var page in FreeCompanyChest.AllPages)
        {
            if (!FreeCompanyChest.IsPageLoaded(page))
                continue;

            var slots = FreeCompanyChest.ReadPage(page);

            // Every successful read counts as having seen the compartment, even an unchanged one.
            _scannedAt[page] = DateTime.Now;
            // FC gil hangs off the first compartment only, so a sum over sources counts it once.
            SourceFor(page).Update(Fold(slots), page == FreeCompanyChest.ItemPages[0] ? FreeCompanyChest.GetGil() : 0);

            // Replace rather than merge: this compartment's current contents are authoritative.
            if (_snapshots.TryGetValue(page, out var previous) && SameContents(previous, slots))
                continue;

            _snapshots[page] = slots;
            changed = true;
        }

        if (changed)
            _totals = null;

        return changed;
    }

    private static bool SameContents(List<ChestSlot> a, List<ChestSlot> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i]))
                return false;

        return true;
    }

    /// <summary>Merged totals across every scanned compartment, ordered by name.</summary>
    public IReadOnlyList<ItemTotal> Totals => _totals ??= BuildTotals();

    private List<ItemTotal> BuildTotals()
    {
        var map = new Dictionary<ItemKey, ItemTotal>();

        foreach (var page in FreeCompanyChest.AllPages)
        {
            if (!_snapshots.TryGetValue(page, out var slots))
                continue;

            foreach (var slot in slots)
            {
                var key = new ItemKey(slot.ItemId, slot.HighQuality);

                if (!map.TryGetValue(key, out var total))
                {
                    total = new ItemTotal { Key = key, Name = GameItems.Name(slot.ItemId) };
                    map[key] = total;
                }

                total.Quantity += slot.Quantity;
                total.Stacks++;

                if (!total.Pages.Contains(page))
                    total.Pages.Add(page);
            }
        }

        // Ordered by compartment (1-5, then crystals) so the list mirrors the chest's own tabs.
        return map.Values
                  .OrderBy(t => t.Pages.Count == 0 ? 99 : FreeCompanyChest.PageOrder(t.Pages[0]))
                  .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(t => t.Key.HighQuality)
                  .ToList();
    }

    /// <summary>
    /// Every stack of one item, from compartments the game currently has loaded.
    /// </summary>
    /// <remarks>
    /// Reads live rather than from the snapshot, because a snapshot goes stale the moment a move
    /// succeeds. Stacks in compartments that are not loaded cannot be moved and are not returned.
    /// </remarks>
    public static List<ChestSlot> FindLiveStacks(ItemKey key)
    {
        var found = new List<ChestSlot>();

        // Compartment order, then slot order, so withdrawal works tab by tab: 1-5, then crystals.
        foreach (var page in FreeCompanyChest.AllPages.OrderBy(FreeCompanyChest.PageOrder))
        {
            if (!FreeCompanyChest.IsPageLoaded(page))
                continue;

            foreach (var slot in FreeCompanyChest.ReadPage(page).OrderBy(s => s.Slot))
                if (slot.ItemId == key.ItemId && slot.HighQuality == key.HighQuality)
                    found.Add(slot);
        }

        return found;
    }

    /// <summary>Units of one item sitting in compartments that are not currently loaded.</summary>
    public int UnreachableQuantity(ItemKey key)
    {
        var total = 0;

        foreach (var (page, slots) in _snapshots)
        {
            if (FreeCompanyChest.IsPageLoaded(page))
                continue;

            total += slots.Where(s => s.ItemId == key.ItemId && s.HighQuality == key.HighQuality)
                          .Sum(s => s.Quantity);
        }

        return total;
    }

    public void Clear()
    {
        _snapshots.Clear();
        _scannedAt.Clear();
        _sources.Clear();
        _totals = null;
    }

    /// <summary>Drops one compartment's snapshot, forcing it to be re-read when next loaded.</summary>
    public void Invalidate(InventoryType page)
    {
        _snapshots.Remove(page);
        _scannedAt.Remove(page);
        _sources.Remove(page);
        _totals = null;
    }
}
