using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BuddysBuddy.Config;
using BuddysBuddy.Game;
using BuddysBuddy.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>
/// A shared board of "somebody please make or gather this".
/// </summary>
/// <remarks>
/// The one module here that is not self-contained. Two Dalamud plugins cannot see each other —
/// Dalamud's IPC reaches other plugins on the same client, never other players — so this talks to a
/// small server everyone on the board shares. <c>server/</c> in this repository is that server, and
/// deploying it is a one-off.
///
/// Off by default, and inert until a board key is entered: a module that leaves the machine should
/// never do so because it was switched on by a default.
/// </remarks>
public sealed class BuddysJobsModule : ModuleBase
{
    public override string Name => "Buddy's Jobs";

    public override string InternalName => "jobs";

    public override string Description => "A shared board of requests: post a job, claim someone else's, mark it delivered.";

    public override bool EnabledByDefault => false;

    public override int Order => 20;

    private static readonly Vector4 Open = new(0.5f, 0.85f, 1f, 1f);
    private static readonly Vector4 Mine = new(0.6f, 0.9f, 0.6f, 1f);
    private static readonly Vector4 Taken = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 Done = new(0.6f, 0.6f, 0.6f, 1f);

    private JobBoardClient _client = null!;
    private BuddysJobsConfig _settings = null!;

    private string _status = string.Empty;

    // The post form.
    private string _itemQuery = string.Empty;
    private uint _pickedItemId;
    private string _pickedItemName = string.Empty;
    private int _quantity = 1;
    private bool _highQuality;
    private string _reward = string.Empty;
    private string _note = string.Empty;

    // The deliver panel.
    private Job? _delivering;
    private int _deliverAmount = 1;

    // Settings drafts, so a half-typed key is never used or saved.
    private string _serverDraft = string.Empty;
    private string _keyDraft = string.Empty;

    protected override void OnLoad()
    {
        _settings = Config.GetModuleConfig<BuddysJobsConfig>(InternalName);
        _serverDraft = _settings.ServerUrl;
        _keyDraft = _settings.BoardKey;

        _client = new JobBoardClient(() => _settings);
        _client.Start();
    }

    protected override void OnUnload()
    {
        _client?.Dispose();
        _client = null!;
    }

    /// <summary>
    /// Who this character is to everyone else on the board.
    /// </summary>
    /// <remarks>
    /// Name and world, because a name alone is not an identity — the board is cross-world by nature,
    /// and two people called the same thing on different worlds must not be able to release each
    /// other's jobs.
    /// </remarks>
    private static string Actor()
    {
        var name = DalamudServices.PlayerState.CharacterName;
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var world = DalamudServices.PlayerState.HomeWorld.ValueNullable?.Name.ToString();
        return string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
    }

    private bool IsMine(string who) => !string.IsNullOrEmpty(who) && who == Actor();

    /// <summary>
    /// Runs a board action and reports the outcome, without blocking the draw thread.
    /// </summary>
    /// <remarks>
    /// The continuation lands on a thread pool thread, so the chat message is bounced back onto the
    /// framework thread before it is printed. The status line is only a string reference read by the
    /// next draw, which needs no such care.
    /// </remarks>
    private void Fire(Task<JobResult> action)
        => _ = action.ContinueWith(task =>
        {
            var result = task.IsCompletedSuccessfully ? task.Result : new JobResult(false, "The action failed.");
            _status = result.Message;

            if (_settings.AnnounceInChat)
                _ = Framework.RunOnFrameworkThread(() => Print(result.Message));
        }, TaskScheduler.Default);

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        if (!_client.IsConfigured)
        {
            DrawSetup();
            return;
        }

        var state = _client.State;
        DrawHeader(state);

        if (ImGui.BeginTabBar("##jobtabs"))
        {
            var jobs = state.Jobs;

            if (ImGui.BeginTabItem($"Open ({jobs.Count(j => j.State == JobState.Open)})"))
            {
                DrawJobs(jobs.Where(j => j.State == JobState.Open).OrderBy(j => j.CreatedAtUnix), "Nothing is open. Post something below.");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"Taken ({jobs.Count(j => j.State == JobState.Claimed)})"))
            {
                DrawJobs(jobs.Where(j => j.State == JobState.Claimed).OrderBy(j => j.ClaimedAtUnix), "Nobody is working on anything.");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Mine"))
            {
                DrawJobs(
                    jobs.Where(j => IsMine(j.PostedBy) || IsMine(j.ClaimedBy)).OrderByDescending(j => j.UpdatedAtUnix),
                    "You have not posted or claimed anything.");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Finished"))
            {
                DrawJobs(jobs.Where(j => j.IsFinished).OrderByDescending(j => j.UpdatedAtUnix), "Nothing finished in the last 30 days.");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Post a job"))
            {
                DrawPostForm();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawDeliverPanel();

        if (_status.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(_status);
        }
    }

    private void DrawHeader(BoardState state)
    {
        using (ImRaii.Disabled(state.Busy))
        {
            if (ImGui.SmallButton("Refresh"))
                _ = _client.Refresh();
        }

        ImGui.SameLine();

        if (state.LastSync is { } at)
            ImGui.TextDisabled($"Last read {at:HH:mm:ss}");
        else
            ImGui.TextDisabled("Never read.");

        if (state.LastError is { } error)
        {
            ImGui.SameLine();
            ImGui.TextColored(Bad, $"· {error}");
        }

        var actor = Actor();
        if (actor.Length == 0)
            ImGui.TextColored(Taken, "Log in before claiming anything — jobs are claimed as a character.");

        ImGui.Spacing();
    }

    /// <summary>The setup panel, shown until there is somewhere to talk to.</summary>
    private void DrawSetup()
    {
        ImGui.TextWrapped(
            "Buddy's Jobs is a shared board. Post what you need, and anyone with the same board " +
            "key can see it, take it, and mark it delivered.");

        ImGui.Spacing();
        ImGui.TextWrapped(
            "It needs a server, because plugins cannot talk to each other directly. The server/ " +
            "folder of this plugin's repository holds one you deploy once, free, on Cloudflare.");

        ImGui.Separator();
        ImGui.Spacing();

        DrawConnectionFields();
    }

    private void DrawConnectionFields()
    {
        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint("Server", "https://buddys-jobs.you.workers.dev", ref _serverDraft, 256);

        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint("Board key", "the join code everyone shares", ref _keyDraft, 128,
            ImGuiInputTextFlags.Password);

        ImGui.Spacing();

        if (ImGui.Button("Save and connect"))
        {
            _settings.ServerUrl = _serverDraft.Trim();
            _settings.BoardKey = _keyDraft.Trim();
            Config.Save();

            _client.Start();
            Fire(Test());
        }

        ImGui.SameLine();
        if (ImGui.Button("Test the server"))
            Fire(_client.Ping());

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Checks the address only, without the board key.\n" +
                "Separates \"the server is down\" from \"the key is wrong\".");
    }

    /// <summary>Pings, then reads the board — so a wrong key and a wrong address report differently.</summary>
    private async Task<JobResult> Test()
    {
        var ping = await _client.Ping().ConfigureAwait(false);
        if (!ping.Ok)
            return ping;

        var ok = await _client.Refresh().ConfigureAwait(false);
        return ok
            ? new JobResult(true, "Connected.")
            : new JobResult(false, _client.State.LastError ?? "The board could not be read.");
    }

    private void DrawJobs(IEnumerable<Job> jobs, string emptyMessage)
    {
        var list = jobs.ToList();

        if (list.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(emptyMessage);
            return;
        }

        if (!ImGui.BeginTable("##jobs", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
        {
            return;
        }

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Wanted", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("You have", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Who", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Reward", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var job in list)
        {
            ImGui.TableNextRow();
            ImGui.PushID(job.Id);

            ImGui.TableNextColumn();
            var name = LocalName(job);
            ImGui.TextUnformatted(job.HighQuality ? $"{name} (HQ)" : name);
            if (job.Note.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(job.Note);

            ImGui.TableNextColumn();
            if (job.Delivered > 0)
                ImGui.TextUnformatted($"{job.Delivered:N0}/{job.Quantity:N0}");
            else
                ImGui.TextUnformatted(job.Quantity.ToString("N0"));

            ImGui.TableNextColumn();
            DrawHave(job);

            ImGui.TableNextColumn();
            DrawWho(job);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(job.Reward.Length > 0 ? job.Reward : "—");

            ImGui.TableNextColumn();
            DrawActions(job);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>The local item sheet wins over the poster's name, so everyone reads their own language.</summary>
    private static string LocalName(Job job)
    {
        var local = GameItems.Name(job.ItemId);
        return local.StartsWith("item #", StringComparison.Ordinal) && job.ItemName.Length > 0
            ? job.ItemName
            : local;
    }

    /// <summary>
    /// How many of the job's item you already own, anywhere.
    /// </summary>
    /// <remarks>
    /// Free, because <see cref="ItemSourceRegistry"/> already knows — it is the same spine Buddy's
    /// Stash and Buddy's Purse read. It turns "can I take this one" from a question into a glance.
    /// </remarks>
    private static void DrawHave(Job job)
    {
        var key = new ItemKey(job.ItemId, job.HighQuality);
        var have = ItemSourceRegistry.Sources().Sum(s => (long)s.Items.GetValueOrDefault(key));

        if (have == 0)
        {
            ImGui.TextDisabled("—");
            return;
        }

        if (have >= job.Remaining)
            ImGui.TextColored(Mine, have.ToString("N0"));
        else
            ImGui.TextUnformatted(have.ToString("N0"));
    }

    private void DrawWho(Job job)
    {
        switch (job.State)
        {
            case JobState.Open:
                ImGui.TextColored(Open, IsMine(job.PostedBy) ? "you asked" : job.PostedBy.Split('@')[0]);
                break;

            case JobState.Claimed:
                ImGui.TextColored(IsMine(job.ClaimedBy) ? Mine : Taken,
                    IsMine(job.ClaimedBy) ? "you took it" : job.ClaimedBy.Split('@')[0]);
                break;

            case JobState.Done:
                ImGui.TextColored(Done, "delivered");
                break;

            case JobState.Cancelled:
                ImGui.TextColored(Done, "cancelled");
                break;

            default:
                ImGui.TextDisabled(job.StateName);
                break;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                $"Asked for by {job.PostedBy}\n" +
                (job.ClaimedBy.Length > 0 ? $"Taken by {job.ClaimedBy}\n" : string.Empty) +
                $"Posted {job.Created:yyyy-MM-dd HH:mm}");
    }

    private void DrawActions(Job job)
    {
        var actor = Actor();

        // Nothing is actionable without a character to act as, and an unknown state is never acted on.
        using var disabled = ImRaii.Disabled(actor.Length == 0);

        switch (job.State)
        {
            case JobState.Open:
                if (ImGui.SmallButton("Claim"))
                    Fire(_client.Claim(job, actor));

                if (IsMine(job.PostedBy))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Cancel"))
                        Fire(_client.Cancel(job, actor));
                }

                break;

            case JobState.Claimed:
                if (IsMine(job.ClaimedBy) || IsMine(job.PostedBy))
                {
                    if (ImGui.SmallButton("Delivered"))
                    {
                        _delivering = job;
                        _deliverAmount = job.Remaining;
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Give back"))
                        Fire(_client.Release(job, actor));
                }
                else
                {
                    ImGui.TextDisabled("in hand");
                }

                break;

            default:
                ImGui.TextDisabled(job.Updated.ToString("MM-dd HH:mm"));
                break;
        }
    }

    /// <summary>
    /// The "how many did you actually hand over" panel.
    /// </summary>
    /// <remarks>
    /// Inline rather than an ImGui popup, for the reason Buddy's Slaves records: popups are keyed by
    /// the id stack where they are opened, so one opened inside a table never matches a
    /// <c>BeginPopup</c> outside it and silently fails to draw.
    ///
    /// Anything short of the full amount puts the job back on the board for the remainder rather
    /// than closing it, because a big order is normally several people bringing what they could.
    /// </remarks>
    private void DrawDeliverPanel()
    {
        var job = _delivering;
        if (job is null)
            return;

        ImGui.Spacing();
        using var child = ImRaii.Child("##deliver", new Vector2(0, 108), true);
        if (!child)
            return;

        ImGui.TextUnformatted($"Delivered how many {LocalName(job)}?");
        ImGui.TextDisabled($"{job.Delivered:N0} of {job.Quantity:N0} so far · {job.Remaining:N0} still wanted");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("##amount", ref _deliverAmount);
        _deliverAmount = Math.Clamp(_deliverAmount, 1, Math.Max(1, job.Remaining));

        ImGui.SameLine();
        if (ImGui.SmallButton("All of it"))
            _deliverAmount = job.Remaining;

        ImGui.Spacing();

        if (ImGui.Button("Confirm", new Vector2(110, 0)))
        {
            Fire(_client.Complete(job, _deliverAmount, Actor()));
            _delivering = null;
        }

        ImGui.SameLine();
        if (ImGui.Button("Never mind", new Vector2(110, 0)))
            _delivering = null;
    }

    private void DrawPostForm()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(300);

        if (ImGui.InputTextWithHint("##itemsearch", "What do you need?", ref _itemQuery, 64))
            _pickedItemId = 0;

        if (_pickedItemId == 0 && _itemQuery.Trim().Length >= 2)
        {
            using var child = ImRaii.Child("##itemresults", new Vector2(300, 120), true);
            if (child)
            {
                foreach (var (id, name) in GameItems.Search(_itemQuery, 20))
                {
                    if (!ImGui.Selectable($"{name}##{id}"))
                        continue;

                    _pickedItemId = id;
                    _pickedItemName = name;
                    _itemQuery = name;
                }
            }
        }

        if (_pickedItemId == 0)
        {
            ImGui.TextDisabled("Pick an item to post a job for it.");
            return;
        }

        ImGui.TextUnformatted($"Item: {_pickedItemName}");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Quantity", ref _quantity);
        _quantity = Math.Max(1, _quantity);

        ImGui.Checkbox("High quality only", ref _highQuality);

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("Reward", "e.g. 5,000g each, or a favour", ref _reward, 128);

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("Note", "anything else worth saying", ref _note, 256);

        ImGui.Spacing();

        var actor = Actor();
        using (ImRaii.Disabled(actor.Length == 0))
        {
            if (ImGui.Button("Post it", new Vector2(120, 0)))
            {
                Fire(_client.Post(_pickedItemId, _pickedItemName, _quantity, _highQuality, _reward, _note, actor));

                _pickedItemId = 0;
                _itemQuery = string.Empty;
                _quantity = 1;
                _highQuality = false;
                _reward = string.Empty;
                _note = string.Empty;
            }
        }

        if (actor.Length == 0)
            ImGui.TextDisabled("Log in first — a job is posted as a character.");
    }

    public override void DrawSettings()
    {
        DrawConnectionFields();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var refresh = _settings.AutoRefresh;
        if (ImGui.Checkbox("Check the board automatically", ref refresh))
        {
            _settings.AutoRefresh = refresh;
            Config.Save();
        }

        var seconds = _settings.PollSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Seconds between checks", ref seconds))
        {
            _settings.PollSeconds = Math.Clamp(seconds, 15, 3600);
            Config.Save();
        }

        var announce = _settings.AnnounceInChat;
        if (ImGui.Checkbox("Report board actions in chat", ref announce))
        {
            _settings.AnnounceInChat = announce;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("This module talks to the server you set above, and to nothing else.");
        ImGui.TextDisabled("It sends the character name and world you act as, and the items you ask");
        ImGui.TextDisabled("for or deliver. Nothing leaves your machine until a board key is set.");
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public override bool HandleCommand(string args)
    {
        var trimmed = args.Trim();

        switch (trimmed.ToLowerInvariant())
        {
            case "":
            {
                if (!_client.IsConfigured)
                {
                    Print("No board configured yet — open the module and set a server and board key.");
                    return true;
                }

                var state = _client.State;
                var open = state.Jobs.Where(j => j.State == JobState.Open).ToList();
                var mine = state.Jobs.Count(j => j.State == JobState.Claimed && IsMine(j.ClaimedBy));

                Print($"{open.Count} job(s) open, {mine} claimed by you.");

                foreach (var job in open.Take(8))
                    Print($"  {job.Remaining:N0}x {LocalName(job)}" +
                          $"{(job.Reward.Length > 0 ? $" — {job.Reward}" : string.Empty)}" +
                          $" (asked by {job.PostedBy.Split('@')[0]})");

                if (state.LastError is { } error)
                    Print($"Last error: {error}");

                return true;
            }

            case "refresh":
                _ = _client.Refresh();
                Print("Checking the board…");
                return true;

            case "test":
                Fire(Test());
                return true;

            default:
                return false;
        }
    }
}

/// <summary>Persisted settings for <see cref="BuddysJobsModule"/>.</summary>
public sealed class BuddysJobsConfig : IModuleConfig
{
    /// <summary>Where the board lives. Empty means the module never touches the network.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// The join code.
    /// </summary>
    /// <remarks>
    /// Stored in the plugin config in the clear, which is the honest option: it has to be sent to
    /// the server on every request, so anything else would only be obfuscation. It is worth no more
    /// than the jobs on the board, and it is the only thing that identifies one.
    /// </remarks>
    public string BoardKey { get; set; } = string.Empty;

    public bool AutoRefresh { get; set; } = true;

    public int PollSeconds { get; set; } = 45;

    public bool AnnounceInChat { get; set; } = true;
}
