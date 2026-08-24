using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Services;
using Lumina.Excel.Sheets;

namespace BuddysBuddy.Game;

/// <summary>
/// Item sheet lookups, cached.
/// </summary>
/// <remarks>
/// UI code resolves names every frame while a list is on screen, so the Excel lookup is memoised.
/// Item names never change at runtime, which makes the cache safe to keep for the session.
/// </remarks>
public static class GameItems
{
    private static readonly Dictionary<uint, string> NameCache = new();
    private static readonly Dictionary<uint, uint> PriceCache = new();

    /// <summary>Every named item, built once on first search. Null until then.</summary>
    private static List<(uint Id, string Name)>? _searchable;

    /// <summary>Item name, falling back to the raw id when the sheet has no row.</summary>
    public static string Name(uint itemId)
    {
        if (NameCache.TryGetValue(itemId, out var cached))
            return cached;

        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        var name = sheet?.GetRowOrDefault(itemId)?.Name.ToString();

        if (string.IsNullOrEmpty(name))
            name = $"item #{itemId}";

        NameCache[itemId] = name;
        return name;
    }

    /// <summary>
    /// Items whose name contains <paramref name="query"/>, best match first.
    /// </summary>
    /// <remarks>
    /// Searches the whole item sheet rather than what you own, because the thing you are asking
    /// somebody else to make is by definition the thing you do not have.
    ///
    /// The index is built once and kept: the sheet holds tens of thousands of rows, which is far too
    /// many to walk on every keystroke, and item names do not change at runtime. An exact match, then
    /// a prefix match, then anything containing the text — otherwise "iron ore" is buried under every
    /// item with "iron" somewhere in its name.
    /// </remarks>
    public static IReadOnlyList<(uint Id, string Name)> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        _searchable ??= BuildIndex();

        var needle = query.Trim();

        return _searchable
            .Where(entry => entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name.Equals(needle, StringComparison.OrdinalIgnoreCase) ? 0
                            : entry.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 1
                            : 2)
            .ThenBy(entry => entry.Name.Length)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static List<(uint Id, string Name)> BuildIndex()
    {
        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        if (sheet is null)
            return [];

        var index = new List<(uint Id, string Name)>(sheet.Count);

        foreach (var item in sheet)
        {
            var name = item.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                index.Add((item.RowId, name));
        }

        DalamudServices.Log.Debug("Indexed {Count} named item(s) for search.", index.Count);
        return index;
    }

    /// <summary>
    /// What a vendor pays for one unit.
    /// </summary>
    /// <remarks>
    /// The offline stand-in for a market price: no network, no rate limit, and available for every
    /// item. It understates anything worth selling to players, so treat it as a floor rather than a
    /// valuation — a real price would come from a market plugin over IPC.
    /// </remarks>
    public static uint VendorPrice(uint itemId)
    {
        if (PriceCache.TryGetValue(itemId, out var cached))
            return cached;

        var sheet = DalamudServices.DataManager.GetExcelSheet<Item>();
        var price = sheet?.GetRowOrDefault(itemId)?.PriceLow ?? 0;

        PriceCache[itemId] = price;
        return price;
    }
}
