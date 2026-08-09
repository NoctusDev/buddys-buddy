using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Modules.BuddysSlaves;

/// <summary>Where some of an item sits, and how much.</summary>
internal sealed class RetainerHolding
{
    public required ulong RetainerId { get; init; }

    public required string RetainerName { get; init; }

    public required int SortIndex { get; init; }

    public int Quantity { get; set; }

    public int Stacks { get; set; }
}

/// <summary>One item's presence across every scanned retainer.</summary>
internal sealed class RetainerItemTotal
{
    public required ItemKey Key { get; init; }

    public required string Name { get; init; }

    public int Quantity { get; set; }

    public int Stacks { get; set; }

    /// <summary>Per-retainer breakdown, in bell order.</summary>
    public List<RetainerHolding> Holdings { get; } = [];

    public int FirstSortIndex => Holdings.Count == 0 ? 99 : Holdings[0].SortIndex;
}

/// <summary>
/// Running tally across all retainers, accumulated as each is visited.
/// </summary>
/// <remarks>
/// Only the retainer open at a bell has loaded inventory, so a single pass sees exactly one
/// retainer. Snapshots are kept per retainer and merged, and the set of retainers actually seen is
/// reported — a partial tally stays visibly partial rather than quietly wrong.
/// </remarks>
internal sealed class RetainerTally : IItemSourceProvider
{
    private readonly Dictionary<ulong, List<RetainerSlot>> _snapshots = new();
    private readonly Dictionary<ulong, string> _names = new();
    private readonly Dictionary<ulong, int> _sortIndex = new();
    private readonly Dictionary<ulong, DateTime> _scannedAt = new();
    private readonly Dictionary<ulong, SnapshotSource> _sources = new();

    private List<RetainerItemTotal>? _totals;

    /// <summary>
    /// One source per retainer actually visited this session.
    /// </summary>
    /// <remarks>
    /// A retainer that has never been opened contributes nothing — but a reading of it saved by an
    /// earlier session still surfaces through <see cref="SourceStore"/>, marked as not current.
    /// </remarks>
    public IEnumerable<IItemSource> GetSources() => _sources.Values;

    public int ScannedCount => _snapshots.Count;

    public IEnumerable<ulong> ScannedIds => _snapshots.Keys;

    public bool HasScanned(ulong retainerId) => _snapshots.ContainsKey(retainerId);

    public DateTime? ScannedAt(ulong retainerId)
        => _scannedAt.TryGetValue(retainerId, out var at) ? at : null;

    /// <summary>
    /// Re-reads the open retainer's pages. Retainers that are not open keep their existing snapshot.
    /// </summary>
    /// <returns>True if anything changed, meaning totals need rebuilding.</returns>
    public bool Refresh()
    {
        var active = Retainers.GetActive();
        if (active is null || !Retainers.IsInventoryOpen())
            return false;

        var info = active.Value;
        var slots = new List<RetainerSlot>();
        var anyLoaded = false;

        foreach (var page in Retainers.Pages)
        {
            if (!Retainers.IsPageLoaded(page))
                continue;

            anyLoaded = true;
            slots.AddRange(Retainers.ReadPage(info.Id, page));
        }

        if (!anyLoaded)
            return false;

        _names[info.Id] = info.Name;
        _sortIndex[info.Id] = info.SortIndex;
        _scannedAt[info.Id] = DateTime.Now;
        SourceFor(info).Update(Fold(slots), info.Gil);

        if (_snapshots.TryGetValue(info.Id, out var previous) && previous.SequenceEqual(slots))
            return false;

        _snapshots[info.Id] = slots;
        _totals = null;
        return true;
    }

    private SnapshotSource SourceFor(RetainerInfo info)
    {
        if (!_sources.TryGetValue(info.Id, out var source))
            _sources[info.Id] = source = new SnapshotSource(
                $"retainer:{info.Id:X}", info.Name, OwnerName());

        // Retainers can be renamed, and the owning character is only known once logged in.
        source.Label = info.Name;
        source.Group = OwnerName();
        return source;
    }

    /// <summary>The logged-in character, used to group retainers by who they belong to.</summary>
    private static string OwnerName()
    {
        var name = DalamudServices.PlayerState.CharacterName;
        return string.IsNullOrEmpty(name) ? "Retainers" : $"{name} · retainers";
    }

    private static Dictionary<ItemKey, int> Fold(List<RetainerSlot> slots)
    {
        var map = new Dictionary<ItemKey, int>();

        foreach (var slot in slots)
        {
            var key = new ItemKey(slot.ItemId, slot.HighQuality);
            map[key] = map.GetValueOrDefault(key) + slot.Quantity;
        }

        return map;
    }

    public IReadOnlyList<RetainerItemTotal> Totals => _totals ??= BuildTotals();

    private List<RetainerItemTotal> BuildTotals()
    {
        var map = new Dictionary<ItemKey, RetainerItemTotal>();

        // Walk retainers in bell order so each item's breakdown reads top to bottom.
        foreach (var (retainerId, slots) in _snapshots.OrderBy(kv => _sortIndex.GetValueOrDefault(kv.Key, 99)))
        {
            foreach (var slot in slots)
            {
                var key = new ItemKey(slot.ItemId, slot.HighQuality);

                if (!map.TryGetValue(key, out var total))
                {
                    total = new RetainerItemTotal { Key = key, Name = GameItems.Name(slot.ItemId) };
                    map[key] = total;
                }

                total.Quantity += slot.Quantity;
                total.Stacks++;

                var holding = total.Holdings.FirstOrDefault(h => h.RetainerId == retainerId);
                if (holding is null)
                {
                    holding = new RetainerHolding
                    {
                        RetainerId = retainerId,
                        RetainerName = _names.GetValueOrDefault(retainerId, "Retainer"),
                        SortIndex = _sortIndex.GetValueOrDefault(retainerId, 99),
                    };
                    total.Holdings.Add(holding);
                }

                holding.Quantity += slot.Quantity;
                holding.Stacks++;
            }
        }

        foreach (var total in map.Values)
            total.Holdings.Sort((a, b) => a.SortIndex.CompareTo(b.SortIndex));

        return map.Values
                  .OrderBy(t => t.FirstSortIndex)
                  .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(t => t.Key.HighQuality)
                  .ToList();
    }

    /// <summary>
    /// Stacks of an item in the retainer currently open, in page then slot order.
    /// </summary>
    /// <remarks>Read live: a snapshot goes stale the moment a withdrawal succeeds.</remarks>
    public static List<RetainerSlot> FindLiveStacks(ItemKey key)
    {
        var found = new List<RetainerSlot>();

        var active = Retainers.GetActive();
        if (active is null)
            return found;

        foreach (var page in Retainers.Pages.OrderBy(Retainers.PageOrder))
        {
            if (!Retainers.IsPageLoaded(page))
                continue;

            foreach (var slot in Retainers.ReadPage(active.Value.Id, page).OrderBy(s => s.Slot))
                if (slot.ItemId == key.ItemId && slot.HighQuality == key.HighQuality)
                    found.Add(slot);
        }

        return found;
    }

    /// <summary>Units of an item held by retainers that are not currently open, in bell order.</summary>
    public List<RetainerHolding> RemainingElsewhere(ItemKey key, ulong? activeId)
    {
        var total = Totals.FirstOrDefault(t => t.Key.Equals(key));
        if (total is null)
            return [];

        return total.Holdings
                    .Where(h => activeId is null || h.RetainerId != activeId.Value)
                    .OrderBy(h => h.SortIndex)
                    .ToList();
    }

    public void Clear()
    {
        _snapshots.Clear();
        _scannedAt.Clear();
        _sources.Clear();
        _totals = null;
    }
}
