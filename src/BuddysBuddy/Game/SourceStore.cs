using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuddysBuddy.Services;
using Newtonsoft.Json;

namespace BuddysBuddy.Game;

/// <summary>
/// Remembers what every source held, across sessions.
/// </summary>
/// <remarks>
/// Without this, "where is my stuff" can only answer for containers visited since login, which is
/// the least useful moment to ask. Restored readings are served as ordinary sources with
/// <see cref="IItemSource.IsLive"/> false, and <see cref="ItemSourceRegistry"/> shadows them the
/// moment the real thing is read again.
///
/// Deliberately its <em>own file</em> rather than part of the plugin config. This data grows with
/// every retainer and compartment ever seen, and Dalamud deserialises the plugin config inside the
/// plugin constructor — an unreadable config there kills the entire plugin, which has already
/// happened once (see knowledge/verified.md). A corrupt store costs the store and nothing else.
/// </remarks>
public sealed class SourceStore : IItemSourceProvider
{
    private const string FileName = "sources.json";

    private readonly Dictionary<string, SnapshotSource> _restored = new(StringComparer.Ordinal);

    public IEnumerable<IItemSource> GetSources() => _restored.Values;

    private static string Path
        => System.IO.Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, FileName);

    /// <summary>Reads the saved snapshots. Never throws: a broken store is dropped, not fatal.</summary>
    public void Load()
    {
        _restored.Clear();

        try
        {
            var path = Path;
            if (!File.Exists(path))
                return;

            foreach (var source in Deserialize(File.ReadAllText(path)))
                _restored[source.Id] = source;

            DalamudServices.Log.Debug("Restored {Count} item source(s) from disk.", _restored.Count);
        }
        catch (Exception ex)
        {
            _restored.Clear();
            DalamudServices.Log.Error(ex, "Could not read the saved item sources; starting without them.");
        }
    }

    /// <summary>
    /// Writes every source currently known — live readings included, since those are what the next
    /// session will want to start from.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            var sources = ItemSourceRegistry.Sources();
            File.WriteAllText(Path, Serialize(sources));
            DalamudServices.Log.Debug("Saved {Count} item source(s).", sources.Count);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not save item sources.");
        }
    }

    /// <summary>Forgets everything, on disk and in memory.</summary>
    public void Clear()
    {
        _restored.Clear();

        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Could not delete the saved item sources.");
        }
    }

    /// <summary>
    /// The on-disk form of a set of sources. Empty ones are dropped — a location holding nothing is
    /// not worth remembering, and re-reading it costs nothing.
    /// </summary>
    /// <remarks>
    /// Pure, and free of any Dalamud service, so <c>scripts/verify-config.cs</c> can round-trip it
    /// outside the game. This file is the first thing this plugin persists on its own, which makes
    /// it the first place a silent serialisation break could hide.
    /// </remarks>
    public static string Serialize(IEnumerable<IItemSource> sources)
        => JsonConvert.SerializeObject(sources
            .Where(s => s.Items.Count > 0 || s.Gil > 0)
            .Select(s => new StoredSource
            {
                Id = s.Id,
                Label = s.Label,
                Group = s.Group,
                LastSeen = s.LastSeen,
                Gil = s.Gil,
                Items = s.Items.Select(kv => new StoredItem
                {
                    I = kv.Key.ItemId,
                    H = kv.Key.HighQuality,
                    Q = kv.Value,
                }).ToList(),
            })
            .ToList());

    /// <summary>Reads <see cref="Serialize"/>'s output back as sources, none of them live.</summary>
    public static List<SnapshotSource> Deserialize(string json)
    {
        var result = new List<SnapshotSource>();

        foreach (var entry in JsonConvert.DeserializeObject<List<StoredSource>>(json) ?? [])
        {
            if (string.IsNullOrEmpty(entry.Id))
                continue;

            var source = new SnapshotSource(entry.Id, entry.Label ?? entry.Id, entry.Group ?? "Elsewhere");
            source.Restore(ToItems(entry.Items), entry.Gil, entry.LastSeen);
            result.Add(source);
        }

        return result;
    }

    private static Dictionary<ItemKey, int> ToItems(List<StoredItem>? items)
    {
        var map = new Dictionary<ItemKey, int>();

        if (items is null)
            return map;

        foreach (var item in items)
            map[new ItemKey(item.I, item.H)] = item.Q;

        return map;
    }

    /// <summary>On-disk shape. Short property names because this file holds one row per stack.</summary>
    private sealed class StoredSource
    {
        public string? Id { get; set; }

        public string? Label { get; set; }

        public string? Group { get; set; }

        public DateTime? LastSeen { get; set; }

        public long Gil { get; set; }

        public List<StoredItem>? Items { get; set; }
    }

    private sealed class StoredItem
    {
        public uint I { get; set; }

        public bool H { get; set; }

        public int Q { get; set; }
    }
}
