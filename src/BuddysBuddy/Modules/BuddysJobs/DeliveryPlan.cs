using System;
using System.Collections.Generic;
using System.Linq;
using BuddysBuddy.Game;

namespace BuddysBuddy.Modules.BuddysJobs;

internal readonly record struct DeliveryStack(int Bag, ushort Slot, ItemKey Key, int Available, int Quantity);

/// <summary>A trade contains at most five item stacks, with an exact cap on units.</summary>
internal static class DeliveryPlan
{
    public const int MaxTradeGil = 1_000_000;

    public static bool TryTotalGil(string reward, out long total)
    {
        total = 0;
        var text = reward.Trim().ToLowerInvariant();
        if (text.Length == 0) return true;
        if (text.EndsWith("gil", StringComparison.Ordinal)) text = text[..^3].Trim();
        else if (text.EndsWith('g')) text = text[..^1].Trim();
        text = text.Replace(",", "").Replace(" ", "");
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && long.TryParse(text, out total) && total <= 999_999_999;
    }

    public static long Payment(long total, int quantity, int alreadyDelivered, int units)
    {
        if (total < 0 || total > 999_999_999 || quantity <= 0 || alreadyDelivered < 0
            || units < 0 || alreadyDelivered > quantity || units > quantity - alreadyDelivered)
            throw new ArgumentOutOfRangeException(nameof(units));
        return total * (alreadyDelivered + (long)units) / quantity - total * alreadyDelivered / quantity;
    }

    public static int PaymentBatchLimit(long total, int quantity, int alreadyDelivered, int available)
    {
        available = Math.Min(available, quantity - alreadyDelivered);
        if (available <= 0) return 0;
        var low = 0;
        var high = available;
        while (low < high)
        {
            var mid = low + (int)(((long)high - low + 1) / 2);
            if (Payment(total, quantity, alreadyDelivered, mid) <= MaxTradeGil) low = mid;
            else high = mid - 1;
        }
        // One expensive item is delivered with the first payment chunk, followed by gil-only trades.
        return Math.Max(1, low);
    }

    public static bool MatchesExchange(IReadOnlyDictionary<ItemKey, int> items, long gil, ItemKey key, int quantity, long expectedGil)
        => gil == expectedGil && expectedGil >= 0 && expectedGil <= MaxTradeGil
           && (quantity == 0 ? items.Count == 0 : quantity > 0 && items.Count == 1 && items.GetValueOrDefault(key) == quantity);
    public static List<DeliveryStack> Batch(IEnumerable<DeliveryStack> slots, ItemKey key, int remaining)
    {
        var result = new List<DeliveryStack>();
        foreach (var slot in slots)
        {
            if (remaining <= 0 || result.Count == 5) break;
            if (slot.Key != key || slot.Available <= 0) continue;
            var quantity = Math.Min(slot.Available, remaining);
            result.Add(slot with { Quantity = quantity });
            remaining -= quantity;
        }
        return result;
    }

    public static bool SameRecipient(string expected, string name, string? world)
        => !string.IsNullOrEmpty(world)
           && string.Equals(expected, $"{name}@{world}", StringComparison.Ordinal);

    public static bool MatchesOffer(IReadOnlyDictionary<ItemKey, int> items, long gil, ItemKey key, int quantity)
        => gil == 0 && quantity > 0 && items.Count == 1 && items.GetValueOrDefault(key) == quantity;

    public static bool TryFriendCommand(string recipient, IEnumerable<string> friendNames, int estateChoice, out string command)
    {
        command = "";
        var parts = recipient.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0
            || parts[0].IndexOfAny(new[] { '\n', '\r', ',', '/' }) >= 0) return false;
        var name = parts[0];
        // Lifestream matches prefixes and prioritizes the current world. Do not let it
        // pick another friend with a matching name or prefix, including on another world.
        var matches = friendNames.Where(x => x.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1 || !string.Equals(matches[0], name, StringComparison.Ordinal)) return false;
        var prefix = estateChoice switch { 1 => "fh", 2 => "ff", 3 => "fa", _ => "f" };
        command = $"{prefix} {name}";
        return true;
    }

    public static bool SamePrompt(string actual, string expected)
        => !string.IsNullOrWhiteSpace(expected)
           && string.Concat(actual.Where(c => !char.IsWhiteSpace(c)))
               == string.Concat(expected.Where(c => !char.IsWhiteSpace(c)));
}
