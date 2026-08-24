using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BuddysBuddy.Modules.BuddysLedger;

/// <summary>How well a recorded trade is evidenced.</summary>
public enum TradeEvidence
{
    /// <summary>The game reported both sides confirmed, and the inventory agreed with the window.</summary>
    Confirmed,

    /// <summary>Both sides confirmed, but the inventory did not match what the window showed.</summary>
    Disputed,

    /// <summary>The confirmation was never seen; the contents came from the inventory alone.</summary>
    Reconstructed,
}

/// <summary>One stack in a trade.</summary>
public sealed class TradeLine
{
    /// <summary>Item id. Short names because a busy ledger holds one of these per stack per trade.</summary>
    [JsonProperty("i")]
    public uint ItemId { get; set; }

    [JsonProperty("h")]
    public bool HighQuality { get; set; }

    [JsonProperty("q")]
    public int Quantity { get; set; }

    [JsonIgnore]
    public ItemKey Key => new(ItemId, HighQuality);

    public static TradeLine From(ItemKey key, int quantity)
        => new() { ItemId = key.ItemId, HighQuality = key.HighQuality, Quantity = quantity };
}

/// <summary>One completed trade, from your side.</summary>
public sealed class TradeRecord
{
    public DateTime At { get; set; }

    /// <summary>Which of your characters traded. Several share one ledger file on a multibox setup.</summary>
    public string Character { get; set; } = string.Empty;

    public ulong ContentId { get; set; }

    public string Partner { get; set; } = string.Empty;

    /// <summary>The partner's home world, or null when the object table could not be asked in time.</summary>
    public string? PartnerWorld { get; set; }

    public long GilGiven { get; set; }

    public long GilReceived { get; set; }

    public List<TradeLine> Given { get; set; } = new();

    public List<TradeLine> Received { get; set; } = new();

    [JsonConverter(typeof(StringEnumConverter))]
    public TradeEvidence Evidence { get; set; }

    /// <summary>Free text, set by hand — what the trade was for.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Stable identity for the partner.
    /// </summary>
    /// <remarks>
    /// Name plus world, because a character name on its own is not unique — the same name exists on
    /// every other world, and cross-world trades are ordinary. A trade whose world could not be
    /// resolved falls back to the bare name rather than being merged into some other world's
    /// totals.
    /// </remarks>
    [JsonIgnore]
    public string PartnerKey => TradeLedger.KeyOf(Partner, PartnerWorld);

    /// <summary>True when nothing actually changed hands.</summary>
    [JsonIgnore]
    public bool IsEmpty
        => GilGiven == 0 && GilReceived == 0 && Given.Count == 0 && Received.Count == 0;
}

/// <summary>Everything one partner has ever exchanged with you, added up.</summary>
public sealed class PartnerTotals
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? World { get; init; }

    public int Trades { get; set; }

    public long GilGiven { get; set; }

    public long GilReceived { get; set; }

    public DateTime FirstTraded { get; set; }

    public DateTime LastTraded { get; set; }

    /// <summary>Units of each item you handed over, across every trade.</summary>
    public Dictionary<ItemKey, long> Given { get; } = new();

    /// <summary>Units of each item they handed over, across every trade.</summary>
    public Dictionary<ItemKey, long> Received { get; } = new();

    /// <summary>Gil received minus gil given. Positive means they have paid you more than you them.</summary>
    public long NetGil => GilReceived - GilGiven;

    public long ItemsGiven => Given.Values.Sum();

    public long ItemsReceived => Received.Values.Sum();

    public string Display => World is null ? Name : $"{Name} @ {World}";
}

/// <summary>
/// Every trade, and what it adds up to per player.
/// </summary>
/// <remarks>
/// Records are the only thing stored; the per-partner and per-item totals are derived on load and
/// kept in step as trades arrive. Storing a rollup as well as the records it came from is how a
/// ledger ends up disagreeing with itself, and the derivation is a single pass over a list that
/// grows by one entry per trade.
///
/// Its own file in the plugin's config directory rather than part of the plugin config, for the
/// same reason as the item sources and the desynthesis ledger: Dalamud deserialises the plugin
/// config inside the plugin constructor, so an unreadable config there kills the whole plugin. A
/// corrupt ledger costs the ledger and nothing else.
/// </remarks>
internal sealed class TradeLedger
{
    private const string FileName = "trade-ledger.json";

    private readonly List<TradeRecord> _records = new();
    private readonly Dictionary<string, PartnerTotals> _partners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every trade, newest last.</summary>
    public IReadOnlyList<TradeRecord> Records => _records;

    public IReadOnlyCollection<PartnerTotals> Partners => _partners.Values;

    /// <summary>Set when a record is added or edited, cleared by <see cref="Save"/>.</summary>
    public bool IsDirty { get; private set; }

    private static string Path
        => System.IO.Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, FileName);

    /// <summary>Partner identity: name plus world where it is known, name alone where it is not.</summary>
    public static string KeyOf(string name, string? world)
        => string.IsNullOrEmpty(world) ? name : $"{name}@{world}";

    public PartnerTotals? Find(string key) => _partners.GetValueOrDefault(key);

    /// <summary>Adds a trade and folds it into the totals.</summary>
    public void Add(TradeRecord record)
    {
        _records.Add(record);
        Accumulate(record);
        IsDirty = true;
    }

    /// <summary>Attaches or clears a note on a trade already recorded.</summary>
    public void Annotate(TradeRecord record, string? note)
    {
        record.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        IsDirty = true;
    }

    /// <summary>Drops one trade and rebuilds the totals without it.</summary>
    public void Remove(TradeRecord record)
    {
        if (!_records.Remove(record))
            return;

        Rebuild();
        IsDirty = true;
    }

    /// <summary>Forgets everything, in memory and on disk.</summary>
    public void Clear()
    {
        _records.Clear();
        _partners.Clear();
        IsDirty = false;

        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not delete the trade ledger.");
        }
    }

    /// <summary>Totals for every item that has ever crossed the table, both directions.</summary>
    public Dictionary<ItemKey, (long Given, long Received)> ItemTotals()
    {
        var totals = new Dictionary<ItemKey, (long Given, long Received)>();

        foreach (var partner in _partners.Values)
        {
            foreach (var (key, quantity) in partner.Given)
            {
                var current = totals.GetValueOrDefault(key);
                totals[key] = (current.Given + quantity, current.Received);
            }

            foreach (var (key, quantity) in partner.Received)
            {
                var current = totals.GetValueOrDefault(key);
                totals[key] = (current.Given, current.Received + quantity);
            }
        }

        return totals;
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Reads the saved ledger. Never throws: a broken ledger is reported, not fatal.</summary>
    public void Load()
    {
        _records.Clear();
        _partners.Clear();
        IsDirty = false;

        try
        {
            var path = Path;
            if (!File.Exists(path))
                return;

            _records.AddRange(Deserialize(File.ReadAllText(path)));
            Rebuild();

            DalamudServices.Log.Debug("Restored {Count} trade(s) from disk.", _records.Count);
        }
        catch (Exception ex)
        {
            _records.Clear();
            _partners.Clear();
            DalamudServices.Log.Error(ex, "Could not read the trade ledger; starting without it.");
        }
    }

    /// <summary>Writes the ledger, but only when something has changed.</summary>
    public void Save(bool force = false)
    {
        if (!IsDirty && !force)
            return;

        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            File.WriteAllText(Path, Serialize(_records));
            IsDirty = false;

            DalamudServices.Log.Debug("Saved {Count} trade(s).", _records.Count);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not save the trade ledger.");
        }
    }

    /// <summary>
    /// The on-disk form.
    /// </summary>
    /// <remarks>
    /// Pure and free of any Dalamud service, so <c>scripts/verify-config.cs</c> can round-trip it
    /// outside the game — the same treatment the item source store gets, and for the same reason:
    /// data that persists can break silently.
    /// </remarks>
    public static string Serialize(IEnumerable<TradeRecord> records)
        => JsonConvert.SerializeObject(records.ToList(), Formatting.None);

    public static List<TradeRecord> Deserialize(string json)
        => JsonConvert.DeserializeObject<List<TradeRecord>>(json) ?? new List<TradeRecord>();

    /// <summary>
    /// The ledger as CSV, one row per stack plus one per gil movement.
    /// </summary>
    /// <remarks>
    /// One row per item rather than per trade, so a spreadsheet can pivot it without unpacking a
    /// nested column. Item names are resolved here because a bare id is useless in a spreadsheet.
    /// </remarks>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("when,character,partner,world,direction,item,item_id,hq,quantity,evidence,note");

        foreach (var record in _records)
        {
            foreach (var line in record.Given)
                Row(record, "given", GameItems.Name(line.ItemId), line.ItemId, line.HighQuality, line.Quantity);

            foreach (var line in record.Received)
                Row(record, "received", GameItems.Name(line.ItemId), line.ItemId, line.HighQuality, line.Quantity);

            if (record.GilGiven > 0)
                Row(record, "given", "Gil", PlayerInventory.GilItemId, false, record.GilGiven);

            if (record.GilReceived > 0)
                Row(record, "received", "Gil", PlayerInventory.GilItemId, false, record.GilReceived);
        }

        return sb.ToString();

        void Row(TradeRecord record, string direction, string item, uint itemId, bool hq, long quantity)
            => sb.AppendLine(string.Join(',',
                Escape(record.At.ToString("yyyy-MM-dd HH:mm:ss")),
                Escape(record.Character),
                Escape(record.Partner),
                Escape(record.PartnerWorld ?? string.Empty),
                direction,
                Escape(item),
                itemId,
                hq ? "1" : "0",
                quantity,
                record.Evidence,
                Escape(record.Note ?? string.Empty)));
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    // ── Rollups ──────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        _partners.Clear();
        foreach (var record in _records)
            Accumulate(record);
    }

    private void Accumulate(TradeRecord record)
    {
        var key = record.PartnerKey;

        if (!_partners.TryGetValue(key, out var totals))
        {
            _partners[key] = totals = new PartnerTotals
            {
                Key = key,
                Name = record.Partner,
                World = record.PartnerWorld,
                FirstTraded = record.At,
            };
        }

        totals.Trades++;
        totals.GilGiven += record.GilGiven;
        totals.GilReceived += record.GilReceived;

        if (record.At < totals.FirstTraded)
            totals.FirstTraded = record.At;

        if (record.At > totals.LastTraded)
            totals.LastTraded = record.At;

        foreach (var line in record.Given)
            totals.Given[line.Key] = totals.Given.GetValueOrDefault(line.Key) + line.Quantity;

        foreach (var line in record.Received)
            totals.Received[line.Key] = totals.Received.GetValueOrDefault(line.Key) + line.Quantity;
    }
}
