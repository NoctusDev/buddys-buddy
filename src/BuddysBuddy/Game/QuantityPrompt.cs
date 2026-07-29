using BuddysBuddy.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BuddysBuddy.Game;

/// <summary>
/// The "how many?" dialog the game raises when taking a stack out of a container.
/// </summary>
/// <remarks>
/// Confirmed in game for the free company chest: a move on its own never completes the withdrawal —
/// the game raises this prompt and nothing leaves the container until it is confirmed. Retainers
/// behave the same way.
///
/// The prompt opens at the full stack, so confirming without changing anything takes everything;
/// passing a smaller number takes exactly that many.
/// </remarks>
public static unsafe class QuantityPrompt
{
    public const string AddonName = "InputNumeric";

    public static bool IsOpen()
    {
        var addon = DalamudServices.GameGui.GetAddonByName(AddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    /// <summary>
    /// Confirms the prompt at <paramref name="quantity"/>.
    /// </summary>
    /// <remarks>
    /// Fires the addon's callback with a single integer and <c>close: true</c> — the path the OK
    /// button takes.
    /// </remarks>
    public static bool Confirm(int quantity)
    {
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>(AddonName, 1);
        if (addon is null || !addon->IsVisible)
            return false;

        var value = stackalloc AtkValue[1];
        value->Type = AtkValueType.Int;
        value->Int = quantity;

        return addon->FireCallback(1, value, true);
    }
}
