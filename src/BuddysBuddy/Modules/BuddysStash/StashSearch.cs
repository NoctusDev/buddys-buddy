using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;

namespace BuddysBuddy.Modules.BuddysStash;

/// <summary>Some of an item, and where it is.</summary>
internal sealed record StashHolding(string SourceId, string Label, string Group, bool IsLive, DateTime? LastSeen, int Quantity);

/// <summary>One item, everywhere it was found.</summary>
internal sealed class StashEntry
{
    public required ItemKey Key { get; init; }

    public required string Name { get; init; }

    public int Total { get; set; }

    /// <summary>Units in places read this session — the part of <see cref="Total"/> known to be current.</summary>
    public int LiveTotal { get; set; }

    public List<StashHolding> Holdings { get; } = [];

    public bool HasStale => Holdings.Any(h => !h.IsLive);

    /// <summary>"Chocobo 400 · Compartment 2 120 · Bags 47" — largest holding first.</summary>
    public string Where => string.Join(" · ", Holdings.Select(h => $"{h.Label} {h.Quantity:N0}"));
}

/// <summary>
/// Folds every registered <see cref="IItemSource"/> into one answer per item.
/// </summary>
/// <remarks>
/// Rebuilt from the registry rather than cached long-term: the sources themselves already do the
/// caching, and folding a few thousand entries is far cheaper than getting invalidation wrong.
/// </remarks>
internal static class StashSearch
{
    public static List<StashEntry> Build(IReadOnlyList<IItemSource> sources)
    {
        var map = new Dictionary<ItemKey, StashEntry>();

        foreach (var source in sources)
        {
            foreach (var (key, quantity) in source.Items)
            {
                if (quantity <= 0)
                    continue;

                if (!map.TryGetValue(key, out var entry))
                {
                    entry = new StashEntry { Key = key, Name = GameItems.Name(key.ItemId) };
                    map[key] = entry;
                }

                entry.Total += quantity;

                if (source.IsLive)
                    entry.LiveTotal += quantity;

                entry.Holdings.Add(new StashHolding(
                    source.Id, source.Label, source.Group, source.IsLive, source.LastSeen, quantity));
            }
        }

        foreach (var entry in map.Values)
            entry.Holdings.Sort((a, b) => b.Quantity.CompareTo(a.Quantity));

        return map.Values
                  .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(e => e.Key.HighQuality)
                  .ToList();
    }

    public static List<StashEntry> Filter(IEnumerable<StashEntry> entries, string search)
        => string.IsNullOrWhiteSpace(search)
            ? entries.ToList()
            : entries.Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
}
