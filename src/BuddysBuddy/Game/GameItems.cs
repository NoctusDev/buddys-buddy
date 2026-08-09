using System.Collections.Generic;
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
