using System;
using System.Collections.Generic;
using System.Linq;

namespace BuddysBuddy.Game;

/// <summary>Identity of an item. High quality counts as a separate item everywhere in this plugin.</summary>
public readonly record struct ItemKey(uint ItemId, bool HighQuality);

/// <summary>
/// One place items can sit — a bag, a saddlebag, a chest compartment, a retainer.
/// </summary>
/// <remarks>
/// Every module in this plugin already keeps a snapshot of somewhere items live, and until now none
/// of them could see each other's. This is the shared shape: a labelled bag of counts, plus enough
/// honesty about <em>when</em> it was read that a stale answer is never mistaken for a current one.
///
/// <see cref="Items"/> must be replaced wholesale rather than mutated in place — the UI reads it
/// from the draw thread while the framework tick writes it.
/// </remarks>
public interface IItemSource
{
    /// <summary>Stable identity, used to match a live source against a stored one across sessions.</summary>
    string Id { get; }

    /// <summary>Display name, e.g. "Bags", "Compartment 3", "Chocobo".</summary>
    string Label { get; }

    /// <summary>Display bucket, e.g. the owning character's name, or "Free company".</summary>
    string Group { get; }

    /// <summary>When this was last read, or null if it never has been.</summary>
    DateTime? LastSeen { get; }

    /// <summary>
    /// True when the contents were read during this session.
    /// </summary>
    /// <remarks>
    /// False means the numbers came off disk and describe how things were, not how they are. Every
    /// view is required to say so — a partial answer that looks complete is the one failure this
    /// plugin has already learned to design against.
    /// </remarks>
    bool IsLive { get; }

    /// <summary>Gil held here, or 0 where the concept does not apply.</summary>
    long Gil { get; }

    /// <summary>Units per item.</summary>
    IReadOnlyDictionary<ItemKey, int> Items { get; }
}

/// <summary>
/// Something that contributes sources to <see cref="ItemSourceRegistry"/>.
/// </summary>
/// <remarks>
/// Providers rather than sources are registered because the set is not fixed: retainers appear as
/// they are discovered, compartments as they are opened. A module registers one provider in
/// <c>OnLoad</c> and unregisters it in <c>OnUnload</c>.
/// </remarks>
public interface IItemSourceProvider
{
    /// <summary>The sources known right now. Called from the draw thread, so it must not block.</summary>
    IEnumerable<IItemSource> GetSources();
}

/// <summary>
/// The one place that knows about everywhere you keep things.
/// </summary>
/// <remarks>
/// Registration mirrors module lifetime: anything registered in <c>OnLoad</c> must be unregistered
/// in <c>OnUnload</c>, because modules are toggled at runtime.
/// </remarks>
public static class ItemSourceRegistry
{
    private static readonly List<IItemSourceProvider> Providers = [];
    private static readonly object Gate = new();

    public static void Register(IItemSourceProvider provider)
    {
        lock (Gate)
        {
            if (!Providers.Contains(provider))
                Providers.Add(provider);
        }
    }

    public static void Unregister(IItemSourceProvider provider)
    {
        lock (Gate)
            Providers.Remove(provider);
    }

    /// <summary>
    /// Every source, with live readings shadowing stored ones of the same <see cref="IItemSource.Id"/>.
    /// </summary>
    public static IReadOnlyList<IItemSource> Sources()
    {
        List<IItemSourceProvider> providers;
        lock (Gate)
            providers = [.. Providers];

        var byId = new Dictionary<string, IItemSource>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            foreach (var source in provider.GetSources())
            {
                // Best-informed wins: a reading from this session beats one restored from disk, and
                // between two of the same kind, the more recent one.
                if (byId.TryGetValue(source.Id, out var existing) && !IsBetter(source, existing))
                    continue;

                byId[source.Id] = source;
            }
        }

        return byId.Values
                   .OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private static bool IsBetter(IItemSource candidate, IItemSource incumbent)
    {
        if (candidate.IsLive != incumbent.IsLive)
            return candidate.IsLive;

        return (candidate.LastSeen ?? DateTime.MinValue) > (incumbent.LastSeen ?? DateTime.MinValue);
    }
}

/// <summary>
/// A plain mutable <see cref="IItemSource"/> for callers that just hold a bag of counts.
/// </summary>
/// <remarks>
/// <see cref="Update"/> swaps the whole dictionary rather than editing it, so a reader on the draw
/// thread always sees one complete reading or the previous one, never a half-written mix.
/// </remarks>
public sealed class SnapshotSource(string id, string label, string group) : IItemSource
{
    public string Id { get; } = id;

    public string Label { get; set; } = label;

    public string Group { get; set; } = group;

    public DateTime? LastSeen { get; private set; }

    public bool IsLive { get; private set; }

    public long Gil { get; private set; }

    public IReadOnlyDictionary<ItemKey, int> Items { get; private set; }
        = new Dictionary<ItemKey, int>();

    /// <summary>Records a fresh reading taken this session.</summary>
    public void Update(IReadOnlyDictionary<ItemKey, int> items, long gil)
    {
        Items = items;
        Gil = gil;
        LastSeen = DateTime.Now;
        IsLive = true;
    }

    /// <summary>Restores a reading from an earlier session — present, but explicitly not current.</summary>
    public void Restore(IReadOnlyDictionary<ItemKey, int> items, long gil, DateTime? lastSeen)
    {
        Items = items;
        Gil = gil;
        LastSeen = lastSeen;
        IsLive = false;
    }
}
