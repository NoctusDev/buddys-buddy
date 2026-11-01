using System;

namespace BuddysBuddy.Modules.BuddysJobs;

internal enum GilEntryStep { Ready, Wait, Blocked, Open, Confirm, TimedOut }

/// <summary>Owns only the gil input dialog it opened and waits for the offered amount to change.</summary>
internal sealed class TradeGilEntry
{
    private bool _opening;
    private bool _applying;
    private DateTime _deadline;
    public int Amount { get; private set; }

    public GilEntryStep Next(DateTime now, long offered, long wanted, bool canEdit, bool numericVisible)
    {
        if (wanted < 0 || wanted > DeliveryPlan.MaxTradeGil) throw new ArgumentOutOfRangeException(nameof(wanted));
        if (_opening)
        {
            if (now >= _deadline) return GilEntryStep.TimedOut;
            if (!numericVisible || !canEdit) return GilEntryStep.Wait;
            Amount = (int)wanted;
            _opening = false;
            _applying = true;
            _deadline = now.AddSeconds(5);
            return GilEntryStep.Confirm;
        }
        if (_applying)
        {
            if (offered != Amount || numericVisible)
                return now >= _deadline ? GilEntryStep.TimedOut : GilEntryStep.Wait;
            _applying = false;
        }
        if (numericVisible) return GilEntryStep.Blocked;
        if (offered == wanted) return GilEntryStep.Ready;
        if (!canEdit) return GilEntryStep.Wait;
        _opening = true;
        _deadline = now.AddSeconds(5);
        return GilEntryStep.Open;
    }
}
