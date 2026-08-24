using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace BuddysBuddy.Modules.BuddysLedger;

/// <summary>
/// Every trade, and what each person has handed over in total.
/// </summary>
/// <remarks>
/// The question this exists to answer is not "what did that trade contain" — the chat log manages
/// that, badly — but "how much has this person actually given me, over all the times we have
/// traded". So the record of one trade is the raw material and the per-partner running total is the
/// product, and the ledger keeps the trades so the totals can always be re-derived rather than
/// drifting away from them.
/// </remarks>
public sealed class BuddysLedgerModule : ModuleBase
{
    public override string Name => "Buddy's Ledger";

    public override string InternalName => "ledger";

    public override string Description => "Records every trade, and what each player has given you in total.";

    public override int Order => 15;

    /// <summary>Consecutive tick failures before the watcher gives up, rather than logging every frame.</summary>
    private const int MaxTickErrors = 10;

    private static readonly Vector4 Gain = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Loss = new(1f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Live = new(0.5f, 0.8f, 1f, 1f);

    private readonly TradeLedger _ledger = new();

    private TradeWatcher _watcher = null!;
    private BuddysLedgerConfig _settings = null!;

    private int _tickErrors;
    private string _filter = string.Empty;
    private string _selectedPartner = string.Empty;
    private TradeRecord? _selectedTrade;
    private string _noteDraft = string.Empty;
    private PartnerSort _sort = PartnerSort.Recent;
    private string _status = string.Empty;

    private enum PartnerSort
    {
        Recent,
        Trades,
        NetGil,
    }

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysLedgerConfig>(InternalName);
        _ledger.Load();
        _watcher = new TradeWatcher(_ledger, OnTradeRecorded);

        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DalamudServices.ClientState.Logout += OnLogout;
    }

    protected override void OnUnload()
    {
        DalamudServices.ClientState.Logout -= OnLogout;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;

        _watcher?.Reset();
        _ledger.Save();
    }

    private void OnLogout(int type, int code)
    {
        // A trade cannot survive a logout, and half of one must not be recorded as a whole.
        _watcher.Reset();
        _ledger.Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_tickErrors >= MaxTickErrors)
            return;

        try
        {
            _watcher.Tick();
            _tickErrors = 0;
        }
        catch (Exception ex)
        {
            _tickErrors++;
            Log.Error(ex, "Reading the trade window failed ({Count}/{Max}).", _tickErrors, MaxTickErrors);

            if (_tickErrors >= MaxTickErrors)
            {
                Log.Error("Buddy's Ledger has stopped watching trades. Re-enable the module to try again.");
                _status = "Stopped watching trades after repeated errors — see the log.";
            }
        }
    }

    /// <summary>
    /// Called on the game thread the moment a trade is recorded.
    /// </summary>
    /// <remarks>
    /// Saved immediately rather than on a timer. Trades are rare and irreplaceable — there is no
    /// second chance to read one back out of the game — so the cost of writing a small file per
    /// trade is not worth weighing against losing one to a crash.
    /// </remarks>
    private void OnTradeRecorded(TradeRecord record)
    {
        _ledger.Save();
        _status = $"Recorded a trade with {Display(record)}.";

        if (_settings.AnnounceInChat)
            Print(Summarise(record));
    }

    private static string Display(TradeRecord record)
        => record.PartnerWorld is null ? record.Partner : $"{record.Partner} @ {record.PartnerWorld}";

    /// <summary>One line of plain English for a trade.</summary>
    private static string Summarise(TradeRecord record)
    {
        var given = Describe(record.Given, record.GilGiven);
        var received = Describe(record.Received, record.GilReceived);

        return (given, received) switch
        {
            (null, null) => $"Traded nothing with {Display(record)}.",
            (not null, null) => $"Gave {Display(record)} {given}.",
            (null, not null) => $"Received {received} from {Display(record)}.",
            _ => $"Gave {Display(record)} {given}, received {received}.",
        };
    }

    private static string? Describe(IReadOnlyList<TradeLine> lines, long gil)
    {
        var parts = lines
            .Select(l => $"{l.Quantity:N0}x {GameItems.Name(l.ItemId)}{(l.HighQuality ? " (HQ)" : string.Empty)}")
            .ToList();

        if (gil > 0)
            parts.Add($"{gil:N0} gil");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        DrawLiveTrade();

        var partners = _ledger.Partners.Count;
        var trades = _ledger.Records.Count;

        if (trades == 0)
        {
            ImGui.TextDisabled("No trades recorded yet. Trade with someone and it will appear here.");
            ImGui.Spacing();
            ImGui.TextDisabled("Only trades this plugin was running for can be counted — there is no");
            ImGui.TextDisabled("history in the game to read back.");
            DrawStatus();
            return;
        }

        ImGui.TextUnformatted($"{trades:N0} trade(s) with {partners:N0} player(s)");

        if (_watcher.CancelledThisSession > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {_watcher.CancelledThisSession} cancelled this session");
        }

        ImGui.Spacing();

        if (ImGui.BeginTabBar("##ledgertabs"))
        {
            if (ImGui.BeginTabItem($"Players ({partners})"))
            {
                DrawPartners();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"History ({trades})"))
            {
                DrawHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Items"))
            {
                DrawItems();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawStatus();
    }

    private void DrawStatus()
    {
        if (_status.Length == 0)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled(_status);
    }

    /// <summary>
    /// The trade currently on screen, if there is one.
    /// </summary>
    /// <remarks>
    /// Worth showing for its own sake — the game's trade window does not total the gil or name what
    /// the other side is offering in words — but mostly it is the honest signal that the watcher is
    /// actually seeing the trade, rather than the player finding out afterwards that it was not.
    /// </remarks>
    private void DrawLiveTrade()
    {
        var live = _watcher.Current;
        if (live is null)
            return;

        using var child = ImRaii.Child("##livetrade", new Vector2(0, 84), true);
        if (!child)
            return;

        var who = string.IsNullOrEmpty(live.PartnerName) ? "someone" : live.PartnerName;
        ImGui.TextColored(Live, $"Trading with {who} — {live.LocalState} / {live.RemoteState}");

        ImGui.TextUnformatted($"You offer:  {Offer(live.Given)}");
        ImGui.TextUnformatted($"They offer: {Offer(live.Received)}");

        ImGui.Separator();
        return;

        static string Offer(TradeOffer offer)
        {
            var parts = offer.Items
                .Select(kv => $"{kv.Value:N0}x {GameItems.Name(kv.Key.ItemId)}{(kv.Key.HighQuality ? " (HQ)" : string.Empty)}")
                .ToList();

            if (offer.Gil > 0)
                parts.Add($"{offer.Gil:N0} gil");

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }
    }

    private void DrawPartners()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##partnerfilter", "Search players…", ref _filter, 64);

        ImGui.SameLine();
        ImGui.TextDisabled("Sort:");
        SortButton("Recent", PartnerSort.Recent);
        SortButton("Trades", PartnerSort.Trades);
        SortButton("Net gil", PartnerSort.NetGil);

        var partners = _ledger.Partners
            .Where(p => _filter.Length == 0 || p.Display.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        partners.Sort((a, b) => _sort switch
        {
            PartnerSort.Trades => b.Trades.CompareTo(a.Trades),
            PartnerSort.NetGil => b.NetGil.CompareTo(a.NetGil),
            _ => b.LastTraded.CompareTo(a.LastTraded),
        });

        ImGui.Spacing();

        if (ImGui.BeginTable("##partners", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220)))
        {
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Trades", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Items in", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Items out", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Net gil", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var partner in partners)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable(partner.Display, partner.Key == _selectedPartner, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedPartner = partner.Key == _selectedPartner ? string.Empty : partner.Key;

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(partner.Trades.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(partner.ItemsReceived.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(partner.ItemsGiven.ToString("N0"));

                ImGui.TableNextColumn();
                if (partner.NetGil == 0)
                    ImGui.TextDisabled("—");
                else
                    ImGui.TextColored(partner.NetGil > 0 ? Gain : Loss, partner.NetGil.ToString("+#,##0;-#,##0"));

                ImGui.TableNextColumn();
                ImGui.TextDisabled(partner.LastTraded.ToString("MM-dd HH:mm"));
            }

            ImGui.EndTable();
        }

        if (partners.Count == 0)
            ImGui.TextDisabled("No players match that.");

        DrawPartnerDetail();
    }

    private void SortButton(string label, PartnerSort sort)
    {
        ImGui.SameLine();

        using var disabled = ImRaii.Disabled(_sort == sort);
        if (ImGui.SmallButton(label))
            _sort = sort;
    }

    /// <summary>Everything one player has ever handed over, and everything they have been given.</summary>
    private void DrawPartnerDetail()
    {
        if (_selectedPartner.Length == 0)
            return;

        var partner = _ledger.Find(_selectedPartner);
        if (partner is null)
        {
            _selectedPartner = string.Empty;
            return;
        }

        ImGui.Spacing();
        using var child = ImRaii.Child("##partnerdetail", new Vector2(0, 240), true);
        if (!child)
            return;

        ImGui.TextUnformatted(partner.Display);
        ImGui.TextDisabled(
            $"{partner.Trades:N0} trade(s) · first {partner.FirstTraded:yyyy-MM-dd} · last {partner.LastTraded:yyyy-MM-dd}");

        if (partner.GilGiven > 0 || partner.GilReceived > 0)
            ImGui.TextUnformatted(
                $"Gil: they gave you {partner.GilReceived:N0}, you gave them {partner.GilGiven:N0}");

        ImGui.Separator();

        if (!ImGui.BeginTable("##partneritems", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
        {
            return;
        }

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("They gave", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("You gave", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Vendor ≈", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var keys = partner.Received.Keys.Union(partner.Given.Keys)
            .OrderByDescending(k => partner.Received.GetValueOrDefault(k))
            .ThenBy(k => GameItems.Name(k.ItemId), StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            var received = partner.Received.GetValueOrDefault(key);
            var given = partner.Given.GetValueOrDefault(key);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(key.HighQuality ? $"{GameItems.Name(key.ItemId)} (HQ)" : GameItems.Name(key.ItemId));

            ImGui.TableNextColumn();
            if (received > 0)
                ImGui.TextColored(Gain, received.ToString("N0"));
            else
                ImGui.TextDisabled("—");

            ImGui.TableNextColumn();
            if (given > 0)
                ImGui.TextColored(Loss, given.ToString("N0"));
            else
                ImGui.TextDisabled("—");

            ImGui.TableNextColumn();
            ImGui.TextDisabled((GameItems.VendorPrice(key.ItemId) * (received - given)).ToString("+#,##0;-#,##0;—"));
        }

        ImGui.EndTable();
    }

    private void DrawHistory()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##historyfilter", "Search players or items…", ref _filter, 64);

        var records = _ledger.Records
            .Where(Matches)
            .OrderByDescending(r => r.At)
            .Take(500)
            .ToList();

        ImGui.Spacing();

        if (ImGui.BeginTable("##history", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260)))
        {
            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 115);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("They gave", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("You gave", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var record in records)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.Selectable(record.At.ToString("MM-dd HH:mm"), ReferenceEquals(record, _selectedTrade),
                        ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedTrade = ReferenceEquals(record, _selectedTrade) ? null : record;
                    _noteDraft = _selectedTrade?.Note ?? string.Empty;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(record.Partner);
                if (record.PartnerWorld is not null && ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{record.Partner} @ {record.PartnerWorld}\nTraded as {record.Character}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Describe(record.Received, record.GilReceived) ?? "—");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Describe(record.Given, record.GilGiven) ?? "—");

                ImGui.TableNextColumn();
                DrawEvidence(record);
            }

            ImGui.EndTable();
        }

        if (records.Count == 0)
            ImGui.TextDisabled("No trades match that.");
        else if (records.Count == 500)
            ImGui.TextDisabled("Showing the 500 most recent. Export to CSV for the rest.");

        DrawTradeDetail();
    }

    /// <summary>
    /// Marks a trade the plugin is less sure about.
    /// </summary>
    /// <remarks>
    /// A confirmed trade gets nothing at all — an icon on every row is noise. Only the two cases
    /// where the record might not be the whole truth are marked, because a ledger that looks
    /// equally confident about everything hides exactly the rows worth a second look.
    /// </remarks>
    private static void DrawEvidence(TradeRecord record)
    {
        switch (record.Evidence)
        {
            case TradeEvidence.Disputed:
                ImGui.TextColored(Warn, "!");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "What the trade window offered and what actually changed in your bags\n" +
                        "did not match. Your bags were recorded — the log has both versions.");
                break;

            case TradeEvidence.Reconstructed:
                ImGui.TextColored(Warn, "~");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "The trade window was never seen with both sides locked in, so there was\n" +
                        "nothing to check this against. The amounts still come from your bags and\n" +
                        "are as accurate as any other row — only the cross-check is missing.");
                break;

            default:
                ImGui.TextDisabled(" ");
                break;
        }
    }

    private void DrawTradeDetail()
    {
        var record = _selectedTrade;
        if (record is null)
            return;

        ImGui.Spacing();
        using var child = ImRaii.Child("##tradedetail", new Vector2(0, 120), true);
        if (!child)
            return;

        ImGui.TextUnformatted($"{Display(record)} · {record.At:yyyy-MM-dd HH:mm:ss} · as {record.Character}");
        ImGui.TextDisabled(Summarise(record));

        ImGui.Spacing();
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##note", "What was this trade for?", ref _noteDraft, 128);

        ImGui.SameLine();
        if (ImGui.SmallButton("Save note"))
        {
            _ledger.Annotate(record, _noteDraft);
            _ledger.Save();
            _status = "Note saved.";
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Delete trade"))
        {
            _ledger.Remove(record);
            _ledger.Save();
            _selectedTrade = null;
            _status = "Trade deleted.";
        }
    }

    /// <summary>Every item that has ever crossed the table, both directions, all players together.</summary>
    private void DrawItems()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##itemfilter", "Search items…", ref _filter, 64);

        var totals = _ledger.ItemTotals()
            .Where(kv => _filter.Length == 0
                         || GameItems.Name(kv.Key.ItemId).Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value.Received)
            .ThenByDescending(kv => kv.Value.Given)
            .ToList();

        ImGui.Spacing();

        if (ImGui.BeginTable("##itemtotals", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Received", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Given", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Net", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var (key, value) in totals)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(key.HighQuality ? $"{GameItems.Name(key.ItemId)} (HQ)" : GameItems.Name(key.ItemId));

                ImGui.TableNextColumn();
                if (value.Received > 0)
                    ImGui.TextColored(Gain, value.Received.ToString("N0"));
                else
                    ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                if (value.Given > 0)
                    ImGui.TextColored(Loss, value.Given.ToString("N0"));
                else
                    ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted((value.Received - value.Given).ToString("+#,##0;-#,##0;0"));
            }

            ImGui.EndTable();
        }

        if (totals.Count == 0)
            ImGui.TextDisabled("Nothing matches that.");
    }

    private bool Matches(TradeRecord record)
    {
        if (_filter.Length == 0)
            return true;

        if (record.Partner.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return true;

        if (record.Note is not null && record.Note.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return true;

        return record.Given.Concat(record.Received)
                     .Any(l => GameItems.Name(l.ItemId).Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    public override void DrawSettings()
    {
        var announce = _settings.AnnounceInChat;
        if (ImGui.Checkbox("Report each trade in chat", ref announce))
        {
            _settings.AnnounceInChat = announce;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Trades are read from the game's own trade window and cross-checked");
        ImGui.TextDisabled("against what actually changed in your bags. Rows marked ! or ~ are the");
        ImGui.TextDisabled("ones where those two did not agree.");

        ImGui.Spacing();

        if (ImGui.Button("Export to CSV"))
            ExportCsv();

        ImGui.SameLine();
        if (ImGui.Button("Write a diagnostic report"))
            WriteProbe();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dumps what the game currently says about the trade window to a text file.");

        ImGui.Spacing();

        if (ImGui.Button("Clear the ledger"))
        {
            _ledger.Clear();
            _selectedPartner = string.Empty;
            _selectedTrade = null;
            _status = "Ledger cleared.";
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Deletes every recorded trade. There is no history in the game to re-read.");
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Length == 0)
        {
            Print($"{_ledger.Records.Count:N0} trade(s) with {_ledger.Partners.Count:N0} player(s).");

            foreach (var partner in _ledger.Partners.OrderByDescending(p => p.LastTraded).Take(5))
                Print($"  {partner.Display}: {partner.Trades} trade(s), " +
                      $"{partner.ItemsReceived:N0} item(s) in, {partner.NetGil:+#,##0;-#,##0;0} gil");

            return true;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "export":
                ExportCsv();
                return true;

            case "probe":
                WriteProbe();
                return true;
        }

        // Anything else is treated as a player to look up, which is the common case by far.
        var match = _ledger.Partners
            .Where(p => p.Display.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastTraded)
            .FirstOrDefault();

        if (match is null)
        {
            Print($"No trades recorded with anyone matching \"{trimmed}\".");
            return true;
        }

        Print($"{match.Display}: {match.Trades} trade(s) between " +
              $"{match.FirstTraded:yyyy-MM-dd} and {match.LastTraded:yyyy-MM-dd}.");
        Print($"  Gil: they gave {match.GilReceived:N0}, you gave {match.GilGiven:N0} " +
              $"({match.NetGil:+#,##0;-#,##0;0} net).");

        foreach (var (key, quantity) in match.Received.OrderByDescending(kv => kv.Value).Take(10))
            Print($"  ← {quantity:N0}x {GameItems.Name(key.ItemId)}{(key.HighQuality ? " (HQ)" : string.Empty)}");

        foreach (var (key, quantity) in match.Given.OrderByDescending(kv => kv.Value).Take(10))
            Print($"  → {quantity:N0}x {GameItems.Name(key.ItemId)}{(key.HighQuality ? " (HQ)" : string.Empty)}");

        return true;
    }

    private void ExportCsv()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            var path = Path.Combine(directory.FullName, $"trades-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(path, _ledger.ToCsv());

            _status = $"Exported to {path}";
            Print(_status);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not export the trade ledger.");
            _status = "Export failed — see the log.";
        }
    }

    private void WriteProbe()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.ConfigDirectory;
            if (!directory.Exists)
                directory.Create();

            var path = Path.Combine(directory.FullName, $"trade-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Trade.Probe());

            _status = $"Diagnostic report written to {path}";
            Print(_status);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write the trade probe.");
            _status = "Diagnostic report failed — see the log.";
        }
    }
}

/// <summary>Persisted settings for <see cref="BuddysLedgerModule"/>.</summary>
public sealed class BuddysLedgerConfig : IModuleConfig
{
    /// <summary>Print a one-line summary in chat whenever a trade is recorded.</summary>
    public bool AnnounceInChat { get; set; } = true;
}
