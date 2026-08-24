using System;
using System.Collections.Generic;
using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BuddysBuddy.Game;

/// <summary>
/// The character's own storage as <see cref="IItemSource"/>s: bags, crystals and saddlebag.
/// </summary>
/// <remarks>
/// Owned by the host rather than by a module, so "where is my stuff" still has a character to
/// answer for when every optional module is switched off.
///
/// Sources are keyed by content id, because on a multibox setup several characters' snapshots share
/// one saved file and must not overwrite each other.
///
/// Must be refreshed from the game thread.
/// </remarks>
public sealed unsafe class CharacterInventory : IItemSourceProvider
{
    private static readonly InventoryType[] SaddlebagPages =
        [InventoryType.SaddleBag1, InventoryType.SaddleBag2];

    private static readonly InventoryType[] PremiumSaddlebagPages =
        [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];

    private const string SaddlebagAddon = "InventoryBuddy";

    private readonly Dictionary<ulong, CharacterSources> _byCharacter = new();

    /// <summary>
    /// Re-reads whatever the game currently has loaded for the logged-in character.
    /// </summary>
    /// <remarks>
    /// Bags and crystals are always readable while logged in. The saddlebag is only readable with
    /// its window open, so it keeps its last reading rather than being cleared — which is exactly
    /// what <see cref="IItemSource.IsLive"/> and <see cref="IItemSource.LastSeen"/> exist to label.
    /// </remarks>
    public void Refresh()
    {
        if (!DalamudServices.ClientState.IsLoggedIn)
            return;

        var contentId = DalamudServices.PlayerState.ContentId;
        if (contentId == 0)
            return;

        var name = DalamudServices.PlayerState.CharacterName;
        if (string.IsNullOrEmpty(name))
            name = "Character";

        if (!_byCharacter.TryGetValue(contentId, out var sources))
            _byCharacter[contentId] = sources = new CharacterSources(contentId, name);

        sources.Rename(name);

        sources.Bags.Update(PlayerInventory.Snapshot(PlayerInventory.Bags), PlayerInventory.CountGil());
        sources.Crystals.Update(PlayerInventory.Snapshot([InventoryType.Crystals]), 0);

        if (IsSaddlebagOpen())
            sources.Saddlebag.Update(
                PlayerInventory.Snapshot([.. SaddlebagPages, .. PremiumSaddlebagPages]), 0);
    }

    public IEnumerable<IItemSource> GetSources()
    {
        foreach (var character in _byCharacter.Values)
        {
            yield return character.Bags;
            yield return character.Crystals;
            yield return character.Saddlebag;
        }
    }

    private static bool IsSaddlebagOpen()
    {
        var addon = DalamudServices.GameGui.GetAddonByName(SaddlebagAddon, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    /// <summary>Stable source ids for one character's storage.</summary>
    public static string BagsId(ulong contentId) => $"char:{contentId:X}:bags";

    public static string CrystalsId(ulong contentId) => $"char:{contentId:X}:crystals";

    public static string SaddlebagId(ulong contentId) => $"char:{contentId:X}:saddlebag";

    private sealed class CharacterSources
    {
        public CharacterSources(ulong contentId, string name)
        {
            Bags = new SnapshotSource(BagsId(contentId), "Bags", name);
            Crystals = new SnapshotSource(CrystalsId(contentId), "Crystals", name);
            Saddlebag = new SnapshotSource(SaddlebagId(contentId), "Saddlebag", name);
        }

        public SnapshotSource Bags { get; }

        public SnapshotSource Crystals { get; }

        public SnapshotSource Saddlebag { get; }

        /// <summary>Characters can be renamed, and a restored source starts with a placeholder name.</summary>
        public void Rename(string name)
        {
            Bags.Group = name;
            Crystals.Group = name;
            Saddlebag.Group = name;
        }
    }
}
