using System;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BuddysBuddy.Modules.BuddysJobs;

internal static unsafe class TradeAutomation
{
    private static DateTime _nextTradeClick;
    private static DateTime _nextConfirmation;
    public static bool OpenGilInput()
    {
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>("Trade", 1);
        if (addon is null || !addon->IsVisible || QuantityPrompt.IsOpen()) return false;
        // Trade's gil button callback: action 2 followed by an empty AtkValue.
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        values[1] = new AtkValue();
        return addon->FireCallback(2, values, true);
    }
    public static bool ClickTrade()
    {
        if (DateTime.UtcNow < _nextTradeClick) return false;
        var addon = DalamudServices.GameGui.GetAddonByName<AtkUnitBase>("Trade", 1);
        if (addon is null || !addon->IsVisible || addon->UldManager.NodeListCount <= 3) return false;
        // This is a node-list index, not a node ID. The two identify different nodes.
        var node = addon->UldManager.NodeList[3];
        if (node is null || !Click(addon, node->GetAsAtkComponentButton())) return false;
        _nextTradeClick = DateTime.UtcNow.AddSeconds(2);
        return true;
    }

    public static bool ConfirmTrade()
    {
        if (DateTime.UtcNow < _nextConfirmation) return false;
        var expected = DalamudServices.DataManager.GetExcelSheet<Addon>().GetRow(102223).Text.ExtractText();
        for (var i = 1; i <= 10; i++)
        {
            var addon = DalamudServices.GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", i);
            if (addon is null || !addon->IsVisible || addon->PromptText is null) continue;
            var actual = MemoryHelper.ReadSeStringNullTerminated((nint)addon->PromptText->NodeText.StringPtr.Value).TextValue;
            if (DeliveryPlan.SamePrompt(actual, expected) && Click((AtkUnitBase*)addon, addon->YesButton))
            { _nextConfirmation = DateTime.UtcNow.AddSeconds(2); return true; }
        }
        return false;
    }

    private static bool Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button is null || !button->IsEnabled || button->OwnerNode is null) return false;
        var evt = button->OwnerNode->AtkEventManager.Event;
        if (evt is null) return false;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }
}
