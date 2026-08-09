using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Newtonsoft.Json;

namespace BuddysBuddy.Modules.BuddysDesynther;

/// <summary>Everything one source item has ever produced.</summary>
internal sealed class LedgerEntry
{
    public uint SourceItemId { get; set; }

    public bool SourceHighQuality { get; set; }

    /// <summary>How many times this item has been desynthesised.</summary>
    public int Runs { get; set; }

    /// <summary>Total units produced per result item, across every run.</summary>
    public Dictionary<string, long> Yield { get; set; } = new();

    /// <summary>Runs where the game's reported yield and the inventory diff disagreed.</summary>
    public int Disagreements { get; set; }

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>Average units of one result item per desynthesis.</summary>
    public double PerRun(string yieldKey)
        => Runs == 0 ? 0 : (double)Yield.GetValueOrDefault(yieldKey) / Runs;
}

/// <summary>
/// What desynthesising things actually gets you.
/// </summary>
/// <remarks>
/// The point of the module. Yields are random, so a single desynthesis says nothing and a hundred
/// say a great deal — this accumulates across sessions until the average is worth trusting, and
/// <see cref="LedgerEntry.Runs"/> is always shown so a two-run average is never mistaken for a
/// settled one.
///
/// A run is recorded from two independent sources: what <c>AgentSalvage.DesynthResults</c> claims,
/// and what actually appeared in the bags. They should agree. When they do not, the inventory wins
/// and the disagreement is counted, because a quietly wrong ledger is worse than no ledger.
///
/// Stored in the plugin's own folder rather than the plugin config, for the same reason as the item
/// sources — see knowledge/verified.md.
/// </remarks>
internal sealed class DesynthLedger
{
    private const string FileName = "desynth-ledger.json";

    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

    public int TotalRuns => _entries.Values.Sum(e => e.Runs);

    private static string Path
        => System.IO.Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, FileName);

    /// <summary>Stable key for an item, used for both entries and yield rows.</summary>
    public static string KeyOf(ItemKey key) => key.HighQuality ? $"{key.ItemId}h" : key.ItemId.ToString();

    public static ItemKey FromKey(string key)
        => key.EndsWith('h')
            ? new ItemKey(uint.Parse(key[..^1]), true)
            : new ItemKey(uint.Parse(key), false);

    public LedgerEntry? Find(ItemKey source) => _entries.GetValueOrDefault(KeyOf(source));

    /// <summary>
    /// Records one desynthesis.
    /// </summary>
    /// <param name="source">What was destroyed.</param>
    /// <param name="observed">What actually arrived in the bags — the evidence.</param>
    /// <param name="reported">What the agent claimed, for cross-checking only.</param>
    public void Record(ItemKey source, IReadOnlyDictionary<ItemKey, int> observed, IReadOnlyList<SalvageYield> reported)
    {
        var key = KeyOf(source);

        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry = new LedgerEntry
            {
                SourceItemId = source.ItemId,
                SourceHighQuality = source.HighQuality,
                FirstSeen = DateTime.Now,
            };
        }

        entry.Runs++;
        entry.LastSeen = DateTime.Now;

        foreach (var (item, quantity) in observed)
        {
            if (quantity <= 0)
                continue;

            var yieldKey = KeyOf(item);
            entry.Yield[yieldKey] = entry.Yield.GetValueOrDefault(yieldKey) + quantity;
        }

        // The agent's own claim, folded down the same way, purely to see whether it matches.
        var claimed = new Dictionary<uint, int>();
        foreach (var result in reported)
            claimed[result.ItemId] = claimed.GetValueOrDefault(result.ItemId) + result.Quantity;

        var seen = new Dictionary<uint, int>();
        foreach (var (item, quantity) in observed)
            seen[item.ItemId] = seen.GetValueOrDefault(item.ItemId) + quantity;

        if (!SameTotals(claimed, seen))
        {
            entry.Disagreements++;
            DalamudServices.Log.Warning(
                "Desynth of {Item}: agent reported {Reported}, inventory gained {Observed}. Recording the inventory.",
                GameItems.Name(source.ItemId),
                Describe(claimed),
                Describe(seen));
        }
    }

    private static bool SameTotals(Dictionary<uint, int> a, Dictionary<uint, int> b)
        => a.Count == b.Count && a.All(kv => b.GetValueOrDefault(kv.Key) == kv.Value);

    private static string Describe(Dictionary<uint, int> totals)
        => totals.Count == 0
            ? "nothing"
            : string.Join(", ", totals.Select(kv => $"{kv.Value}x {GameItems.Name(kv.Key)}"));

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Reads the saved ledger. Never throws: a broken ledger costs the ledger, nothing else.</summary>
    public void Load()
    {
        _entries.Clear();

        try
        {
            if (!File.Exists(Path))
                return;

            foreach (var entry in Deserialize(File.ReadAllText(Path)))
                _entries[KeyOf(new ItemKey(entry.SourceItemId, entry.SourceHighQuality))] = entry;

            DalamudServices.Log.Debug("Restored desynth ledger — {Count} item(s).", _entries.Count);
        }
        catch (Exception ex)
        {
            _entries.Clear();
            DalamudServices.Log.Error(ex, "Could not read the desynth ledger; starting empty.");
        }
    }

    public void Save()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            File.WriteAllText(Path, Serialize(_entries.Values));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not save the desynth ledger.");
        }
    }

    public void Clear()
    {
        _entries.Clear();

        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not delete the desynth ledger.");
        }
    }

    /// <summary>Where the ledger lives on disk, for the UI to show.</summary>
    public static string Location => Path;

    /// <summary>
    /// Pure, service-free serialisation so <c>scripts/verify-config.cs</c> can round-trip it outside
    /// the game. This is hard-won data — hundreds of desynths — and it must not break silently.
    /// </summary>
    public static string Serialize(IEnumerable<LedgerEntry> entries)
        => JsonConvert.SerializeObject(entries.ToList(), Formatting.Indented);

    public static List<LedgerEntry> Deserialize(string json)
        => JsonConvert.DeserializeObject<List<LedgerEntry>>(json) ?? [];
}
