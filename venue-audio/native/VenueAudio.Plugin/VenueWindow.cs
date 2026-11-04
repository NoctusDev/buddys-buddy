using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VenueAudio.Core;
using static VenueAudio.Theme;

namespace VenueAudio;

public sealed class VenueWindow : Window
{
    private enum Pending { None, Broadcast, Listen, Stop }
    private enum Bar { Idle, Starting, Stopping, Live }

    private static readonly (string Name, int Bitrate)[] Qualities =
        [("Voice · 64 kbps", 64000), ("Standard · 96 kbps", 96000), ("High · 128 kbps", 128000), ("Studio · 192 kbps", 192000)];
    // Session's own status lines while going live, in order. The last one arrives once already broadcasting.
    private static readonly string[] GoingLive =
        ["Starting broadcast…", "Opening a temporary internet relay… This can take about a minute.", "Broadcasting. Reserving a short code…"];
    private static readonly int[] GoingLiveBulbs = [5, 10, 16];

    private readonly Session session;
    private readonly Settings settings;
    private readonly Action save;
    private readonly Func<string> characterName;
    private readonly IFontHandle mono;
    private readonly PageMotion motion;
    private List<AudioDevice> devices = [];
    private Role role;
    private bool hideCodes, announce, watchVenues, autoJoin, liveBadge, setupOpen = true;
    private string sourceId, microphoneId, invitation, venueName;
    private string? joinedWith;
    private string deviceError = "";
    private float volume, music, microphone, inset;
    private int bitrate;
    private double copiedAt = -10, pendingSince;
    private string copiedWhat = "";
    private Task? operation;
    private Pending pending;
    private (int, int) pushed;

    private static Palette P => Current;
    private bool HasMicrophone => microphoneId.Length > 0;
    /// <summary>The usable row width, less a panel's right padding when inside one.</summary>
    private float Avail => ImGui.GetContentRegionAvail().X - inset;

    public VenueWindow(Session session, Settings settings, Action save, Func<string> characterName, IFontHandle mono, GuideWindow guide)
        : base("House Lights###VenueAudioMain")
    {
        this.session = session; this.settings = settings; this.save = save; this.characterName = characterName; this.mono = mono;
        role = settings.Role;
        sourceId = settings.SourceId; microphoneId = settings.MicrophoneId;
        invitation = settings.LastInvite; venueName = settings.VenueName;
        hideCodes = settings.HideCodes; announce = settings.Announce; liveBadge = settings.LiveBadge;
        watchVenues = settings.WatchVenues; autoJoin = settings.AutoJoin;
        volume = settings.Volume; music = settings.MusicGain; microphone = settings.MicGain; bitrate = settings.Bitrate;
        motion = new PageMotion((int)role);
        Size = new Vector2(460, 400); SizeCondition = ImGuiCond.FirstUseEver;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            IconOffset = new Vector2(1.5f, 1),
            Priority = 2,
            Click = _ => guide.Show(role),
            ShowTooltip = () => ImGui.SetTooltip("How House Lights works"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Palette,
            IconOffset = new Vector2(1.5f, 1),
            Priority = 1,
            Click = button => { settings.Style = Theme.Next(settings.Style, button == ImGuiMouseButton.Right ? -1 : 1); save(); },
            ShowTooltip = () => ImGui.SetTooltip($"Style: {settings.Style}\nClick for the next style, right-click for the previous."),
        });
        Refresh();
    }

    public override void PreDraw()
    {
        pushed = Theme.Push(settings.Style);
        // Height follows the screen's content; only the width is the player's to drag.
        float height = motion.Height(S(400), ImGui.GetMainViewport().Size.Y * .9f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(S(420), height), new Vector2(float.MaxValue, height));
        Flags = motion.Moving ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
    }

    public override void OnOpen() => motion.Jump((int)role);
    public override void PostDraw() => Theme.Pop(pushed);

    private void Refresh()
    {
        try
        {
            devices = AudioDevices.List(); deviceError = "";
            if (!devices.Any(d => d.Id == sourceId)) sourceId =
                devices.FirstOrDefault(d => !d.IsOutput && d.Name.Contains("Stream Mix", StringComparison.OrdinalIgnoreCase))?.Id
                ?? devices.FirstOrDefault(d => d.IsOutput)?.Id ?? "";
            if (!devices.Any(d => d.Id == microphoneId)) microphoneId = "";
        }
        catch (Exception e) { deviceError = e.Message; }
    }

    private void Remember()
    {
        settings.Role = role;
        settings.SourceId = sourceId; settings.MicrophoneId = microphoneId;
        settings.LastInvite = invitation; settings.VenueName = venueName;
        settings.HideCodes = hideCodes; settings.Announce = announce; settings.LiveBadge = liveBadge;
        settings.WatchVenues = watchVenues; settings.AutoJoin = autoJoin;
        settings.Volume = volume; settings.MusicGain = music; settings.MicGain = microphone; settings.Bitrate = bitrate;
        save();
    }

    private void Run(Pending kind, Func<Task> action)
    {
        Remember(); pending = kind; pendingSince = ImGui.GetTime(); operation = Task.Run(action);
    }

    public override void Draw()
    {
        bool busy = operation is { IsCompleted: false };
        if (!busy) pending = Pending.None;
        motion.Begin();
        switch (role)
        {
            case Role.Host: DrawHost(busy); break;
            case Role.Listener: DrawListener(busy); break;
            default: DrawRolePicker(); break;
        }
        motion.End(ImGui.GetStyle().WindowPadding.Y);
    }

    // ── Role picker (B1) ─────────────────────────────────────────────────────

    private void DrawRolePicker()
    {
        MarqueeDraw.Sign(mono, Bulbs.Chasing, P.Host, Edge,
            [new("HOUSE LIGHTS", Ink, true), new("Which seat are you taking tonight?", Muted)], alternate: P.Listen);
        Block();
        float gap = S(10), width = (Avail - gap) / 2;
        if (RoleTicket("host", width, P.Host, P.HostEdge, P.OnHost, FontAwesomeIcon.BroadcastTower, "HOST",
                "Broadcast your venue's music to guests", "RUN THE MUSIC"))
            PickRole(Role.Host);
        ImGui.SameLine(0, gap);
        if (RoleTicket("listener", width, P.Listen, P.ListenEdge, P.OnListen, FontAwesomeIcon.Headphones, "LISTENER",
                "Tune in with a venue code, or walk in", "TUNE IN"))
            PickRole(Role.Listener);
        Block();
        StyleRow();
    }

    private void PickRole(Role value) { role = value; motion.Go((int)value); Remember(); }

    private bool RoleTicket(string id, float width, Vector4 accent, Vector4 edge, Vector4 onAccent, FontAwesomeIcon icon,
        string title, string subtitle, string stub)
    {
        float height = S(216), line = ImGui.GetTextLineHeight();
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        bool clicked = ImGui.InvisibleButton("##role-" + id, new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(P.Surface), R(6));
        if (hovered) draw.AddRectFilled(min, max, U32(accent, .1f), R(6));
        draw.AddRect(min, max, U32(hovered ? accent : edge), R(6));

        var badge = min + new Vector2(S(14) + S(20), S(16) + S(20));
        draw.AddCircleFilled(badge, S(20), U32(accent));
        Icon(draw, icon, badge, U32(onAccent));
        float x = min.X + S(14), y = min.Y + S(16) + S(40) + S(8);
        draw.AddText(new Vector2(x, y), U32(Ink), title);
        ImGui.SetCursorScreenPos(new Vector2(x, y + line + S(8)));
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width - S(28))) ImGui.TextColored(Muted, subtitle);

        float perforation = min.Y + S(158);
        MarqueeDraw.Dashed(draw, new Vector2(min.X + S(12), perforation), new Vector2(max.X - S(12), perforation), U32(Edge));
        MarqueeDraw.Notch(draw, new Vector2(min.X, perforation), -MathF.PI / 2, edge, radius: 7);
        MarqueeDraw.Notch(draw, new Vector2(max.X, perforation), MathF.PI / 2, edge, radius: 7);
        float stubY = (perforation + max.Y) / 2;
        using (mono.Push())
        {
            var size = ImGui.CalcTextSize(stub);
            draw.AddText(new Vector2(x, stubY - size.Y / 2), U32(accent), stub);
        }
        Icon(draw, FontAwesomeIcon.ChevronRight, new Vector2(max.X - S(20), stubY), U32(accent));
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        MarqueeDraw.Advance(min, max);
        return clicked;
    }

    private void StyleRow()
    {
        float swatch = S(18), gap = S(8), name = S(72);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Muted, "Style");
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - (Theme.Styles.Length * (swatch + gap) + name));
        var draw = ImGui.GetWindowDrawList();
        foreach (var style in Theme.Styles)
        {
            var min = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - swatch) / 2);
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton("##style-" + style.Name, new Vector2(swatch, swatch))) { settings.Style = style.Name; save(); }
            if (ImGui.IsItemHovered()) { ImGui.SetTooltip(style.Name); ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); }
            var center = min + new Vector2(swatch / 2);
            draw.AddCircleFilled(center, swatch / 2 - S(2), U32(style.Host));
            if (settings.Style == style.Name) draw.AddCircle(center, swatch / 2 - S(1), U32(Ink), 0, S(2));
            ImGui.SameLine(0, gap);
        }
        float text = ImGui.CalcTextSize(settings.Style).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + name - text);
        ImGui.Text(settings.Style);
    }

    private void Header(string label, Vector4 fill, Vector4 ink, bool canSwitch)
    {
        var size = ImGui.CalcTextSize(label) + new Vector2(S(16), S(4));
        var min = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - size.Y) / 2);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U32(fill), size.Y / 2);
        draw.AddText(min + new Vector2(S(8), S(2)), U32(ink), label);
        ImGui.Dummy(new Vector2(size.X, ImGui.GetFrameHeight()));
        const string link = "Switch role";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(link).X - S(8));
        using (ImRaii.Disabled(!canSwitch))
            if (LinkButton(link)) PickRole(Role.None);
        Block();
    }

    // ── Host (B5, B6, B2, B7) ────────────────────────────────────────────────

    private void DrawHost(bool busy)
    {
        bool live = session.Broadcasting;
        bool starting = busy && pending == Pending.Broadcast;
        var here = VenueLocation.Current();
        Header("HOST", P.Host, P.OnHost, !busy && !session.Active);

        string hosted = HostedLine(here);
        if (live)
        {
            bool allMuted = session.MusicMuted && (!HasMicrophone || session.MicMuted);
            var lines = new List<SignLine>
            {
                allMuted ? new("ON AIR, BUT EVERYTHING IS MUTED", Warn) : new("NOW PLAYING AT", Muted),
                new(VenueTitle("LIVE VENUE"), Ink),
            };
            if (hosted.Length > 0) lines.Add(new(hosted, Muted));
            if (allMuted) MarqueeDraw.Sign(mono, Bulbs.Dimmed, P.HostDim, P.WarnEdge, lines);
            else MarqueeDraw.Sign(mono, Bulbs.Chasing, P.Host, P.HostEdge, lines);
            Block();
            DrawCode();
            Block();
            Meter("Level", allMuted ? 0 : session.Peak, S(56));
            MusicSlider(S(56));
            if (HasMicrophone) MicSlider(S(56));
            Block();
            SetupSummary(true);
            Block();
            if (Transport(Bar.Live, true, allMuted)) Run(Pending.Stop, session.StopAsync);
            return;
        }

        if (busy)
        {
            int step = Math.Max(0, Array.IndexOf(GoingLive, session.Status));
            int lit = starting ? GoingLiveBulbs[step] : Math.Max(0, 20 - (int)((ImGui.GetTime() - pendingSince) / .06));
            var lines = new List<SignLine>
            {
                new(starting ? "WARMING UP THE LIGHTS" : "STOPPING", Muted),
                new(VenueTitle("LIVE VENUE"), Ink),
            };
            if (hosted.Length > 0) lines.Add(new(hosted, Muted));
            MarqueeDraw.Sign(mono, Bulbs.Filling, P.Host, Edge, lines, lit: lit);
            if (starting) { Block(); Checklist(step); }
            Block();
            PendingCode();
            Block();
            SetupSummary(true);
            Block();
            if (Transport(starting ? Bar.Starting : Bar.Stopping, true, false)) Run(Pending.Stop, session.StopAsync);
            return;
        }

        var preview = new List<SignLine>
        {
            new("PREVIEW · WHAT GUESTS WILL SEE", Muted),
            venueName.Trim().Length > 0 ? new(VenueTitle(""), Ink) : new("[Your venue's name]", Muted),
        };
        if (hosted.Length > 0) preview.Add(new(hosted, Muted));
        MarqueeDraw.Sign(mono, Bulbs.Off, P.Host, Edge, preview);
        Block();
        PlaceholderCode();
        Block();
        if (setupOpen) SetupBlock(here);
        else SetupSummary(false);
        Block();
        bool ready = devices.Any(d => d.Id == sourceId);
        if (Transport(Bar.Idle, ready, false)) StartBroadcast(here);
        if (session.Faulted) Centered(Error, session.Status);
        else if (!ready) Centered(Muted, "Choose an audio source to start.");
    }

    private string VenueTitle(string fallback) => venueName.Trim().Length > 0 ? venueName.Trim().ToUpperInvariant() : fallback;

    private string HostedLine(VenueLocation? here)
    {
        string host = characterName();
        if (host.Length == 0) return here?.Label ?? "";
        return here != null ? $"Hosted by {host} · {here.Label}" : $"Hosted by {host}";
    }

    private void Checklist(int step)
    {
        var draw = ImGui.GetWindowDrawList();
        float pad = S(10), gap = S(6), line = ImGui.GetTextLineHeight(), width = Avail;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, pad * 2 + line * GoingLive.Length + gap * (GoingLive.Length - 1));
        draw.AddRectFilled(min, max, U32(P.Surface), R(4));
        draw.AddRect(min, max, U32(Edge), R(4));
        for (int i = 0; i < GoingLive.Length; i++)
        {
            float y = min.Y + pad + i * (line + gap), mid = y + line / 2;
            var mark = new Vector2(min.X + pad + S(6), mid);
            if (i < step) Icon(draw, FontAwesomeIcon.Check, mark, U32(MeterGreen));
            else if (i == step) draw.AddCircleFilled(mark, S(4), U32(P.Host, MarqueeDraw.Blink(.9) ? 1 : .3f));
            else draw.AddCircle(mark, S(4), U32(Muted));
            string text = MarqueeDraw.Fit(GoingLive[i], width - pad * 2 - S(22));
            draw.AddText(new Vector2(min.X + pad + S(22), y), U32(i == step ? Ink : Muted), text);
        }
        MarqueeDraw.Advance(min, max);
    }

    private void PlaceholderCode()
    {
        var draw = ImGui.GetWindowDrawList();
        float height = S(58), line = ImGui.GetTextLineHeight();
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(Avail, height);
        MarqueeDraw.DashedRect(draw, min, max, U32(Edge));
        float x = min.X + S(14), mid = (min.Y + max.Y) / 2;
        draw.AddText(new Vector2(x, mid - line), U32(Muted), "VENUE CODE");
        draw.AddText(new Vector2(x, mid), U32(Muted),
            MarqueeDraw.Fit("You get a fresh six-character code each time you go live.", max.X - x - S(14)));
        MarqueeDraw.Advance(min, max);
    }

    private void PendingCode()
    {
        const float alpha = .7f;
        var ticket = MarqueeDraw.Ticket(S(58), S(110), Field, null, Edge, alpha);
        var draw = ImGui.GetWindowDrawList();
        float x = ticket.Min.X + S(14), line = ImGui.GetTextLineHeight();
        draw.AddText(new Vector2(x, ticket.Mid - line), U32(Muted, alpha), "VENUE CODE");
        float cell;
        using (mono.Push()) cell = ImGui.CalcTextSize("M").X;
        double t = ImGui.GetTime();
        for (int i = 0; i < 6; i++)
        {
            // Placeholder characters shimmer left to right; the gap after the third stands for the dash.
            float shimmer = (float)(.35 + .3 * Math.Sin(t * 4 - i * .6));
            var a = new Vector2(x + (i + (i >= 3 ? 1 : 0)) * (cell + S(2)), ticket.Mid + S(3));
            draw.AddRectFilled(a, a + new Vector2(cell, line - S(6)), U32(Raised, shimmer * alpha), R(1));
        }
        var size = ImGui.CalcTextSize("Not yet");
        draw.AddText(new Vector2(ticket.Perforation + (ticket.Max.X - ticket.Perforation - size.X) / 2, ticket.Mid - size.Y / 2),
            U32(Muted, alpha), "Not yet");
        MarqueeDraw.Advance(ticket);
    }

    private void DrawCode()
    {
        string code = session.Code;
        if (code.Length == 0 && session.CodeProblem.Length == 0) { PendingCode(); return; }

        var draw = ImGui.GetWindowDrawList();
        float line = ImGui.GetTextLineHeight(), copy = CopyWidth(), button = S(34);
        if (code.Length > 0)
        {
            float stub = button + S(6) + copy + S(16);
            var ticket = MarqueeDraw.Ticket(S(58), stub, Field, null, Edge);
            float x = ticket.Min.X + S(14);
            draw.AddText(new Vector2(x, ticket.Mid - line - S(1)), U32(Muted), hideCodes ? "VENUE CODE · HIDDEN ON STREAM" : "VENUE CODE");
            using (mono.Push())
                draw.AddText(new Vector2(x, ticket.Mid + S(1)), U32(hideCodes ? Muted : Ink), hideCodes ? "•••-•••" : VenueCodes.Display(code));
            ImGui.SetCursorScreenPos(new Vector2(ticket.Perforation + S(8), ticket.Mid - button / 2));
            using (ImRaii.PushColor(ImGuiCol.Button, hideCodes ? P.HostTint : Raised).Push(ImGuiCol.Border, P.Host))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, hideCodes ? 1f : 0f))
                if (IconButton(hideCodes ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, "##hide", button)) { hideCodes = !hideCodes; Remember(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hideCodes ? "Show the code" : "Hide the code while you stream");
            ImGui.SameLine(0, S(6));
            if (CopyButton("code", copy, button)) Copy("code", VenueCodes.Display(code));
            MarqueeDraw.Advance(ticket);
            if (LinkButton(Copied("invite") ? "Full invite copied" : "Copy the full invite instead")) Copy("invite", session.Invite);
        }
        else
        {
            float stub = copy + S(16);
            var ticket = MarqueeDraw.Ticket(S(58), stub, Field, null, Edge);
            float x = ticket.Min.X + S(14);
            draw.AddText(new Vector2(x, ticket.Min.Y + S(6)), U32(Muted), "GUEST INVITE");
            string invite = hideCodes ? new string('•', 24) : session.Invite;
            ImGui.SetCursorScreenPos(new Vector2(x, ticket.Min.Y + S(6) + line + S(2)));
            ImGui.SetNextItemWidth(ticket.Perforation - x - S(14));
            using (mono.Push()) ImGui.InputText("##invite", ref invite, 1024, ImGuiInputTextFlags.ReadOnly);
            ImGui.SetCursorScreenPos(new Vector2(ticket.Perforation + S(8), ticket.Mid - button / 2));
            if (CopyButton("invite", copy, button)) Copy("invite", session.Invite);
            MarqueeDraw.Advance(ticket);
        }
        if (session.CodeProblem.Length > 0) IconLine(FontAwesomeIcon.ExclamationTriangle, Warn, session.CodeProblem);
    }

    private void SetupSummary(bool locked)
    {
        float height = S(32), width = Avail, line = ImGui.GetTextLineHeight();
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        bool clicked = false, hovered = false;
        if (locked) ImGui.Dummy(new Vector2(width, height));
        else { clicked = ImGui.InvisibleButton("##setup", new Vector2(width, height)); hovered = ImGui.IsItemHovered(); }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(hovered ? P.TileHover : P.Surface), R(4));
        draw.AddRect(min, max, U32(Edge), R(4));
        float mid = (min.Y + max.Y) / 2;
        Icon(draw, FontAwesomeIcon.ChevronRight, new Vector2(min.X + S(16), mid), U32(Ink), small: true);
        draw.AddText(new Vector2(min.X + S(28), mid - line / 2), U32(Ink), "SETUP");
        float from = min.X + S(28) + ImGui.CalcTextSize("SETUP").X + S(8), to = max.X - (locked ? S(30) : S(10));
        draw.AddText(new Vector2(from, mid - line / 2), U32(Muted), MarqueeDraw.Fit(SetupText(), to - from));
        if (locked) Icon(draw, FontAwesomeIcon.Lock, new Vector2(max.X - S(16), mid), U32(Muted), small: true);
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) setupOpen = true;
    }

    private string SetupText()
    {
        static string Short(string name) { int cut = name.IndexOf(" ("); return cut > 0 ? name[..cut] : name; }
        string source = Short(devices.FirstOrDefault(d => d.Id == sourceId)?.Name ?? "No source");
        string mic = HasMicrophone ? Short(devices.FirstOrDefault(d => d.Id == microphoneId)?.Name ?? "Mic") : "No mic";
        int index = Array.FindIndex(Qualities, q => q.Bitrate == bitrate);
        string quality = index >= 0 ? Qualities[index].Name.Replace(" · ", " ") : $"{bitrate / 1000} kbps";
        return $"{source} · {mic} · {quality}";
    }

    private void SetupBlock(VenueLocation? here)
    {
        var draw = ImGui.GetWindowDrawList();
        float column = S(64), pad = S(10), header = S(32), line = ImGui.GetTextLineHeight();
        var start = ImGui.GetCursorScreenPos();
        float width = Avail;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        if (ImGui.InvisibleButton("##setup-head", new Vector2(width, header))) setupOpen = false;
        bool hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        float headMid = start.Y + header / 2;
        Icon(draw, FontAwesomeIcon.ChevronDown, new Vector2(start.X + S(16), headMid), U32(Ink), small: true);
        draw.AddText(new Vector2(start.X + S(28), headMid - line / 2), U32(Ink), "SETUP");
        draw.AddLine(new Vector2(start.X, start.Y + header), new Vector2(start.X + width, start.Y + header), U32(Edge));

        ImGui.Indent(pad); inset = pad;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + pad - ImGui.GetStyle().ItemSpacing.Y);

        RowLabel("Venue", column);
        ImGui.SetNextItemWidth(Avail);
        ImGui.InputTextWithHint("##venue", "[Your venue's name]", ref venueName, Invite.MaxLabel);
        if (ImGui.IsItemDeactivatedAfterEdit()) Remember();

        RowLabel("Source", column);
        float refresh = ImGui.GetFrameHeight();
        DevicePicker("##source", ref sourceId, false, Avail - refresh - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        if (IconButton(FontAwesomeIcon.SyncAlt, "##refresh", refresh)) Refresh();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Refresh audio devices");
        if (deviceError.Length > 0) Wrapped(Error, deviceError);

        Meter("Level", 0, column);
        MusicSlider(column);
        Divider();

        RowLabel("Mic", column);
        DevicePicker("##mic", ref microphoneId, true, Avail);
        if (HasMicrophone) MicSlider(column);

        RowLabel("Quality", column);
        QualityPicker(Avail);
        Divider();

        using (ImRaii.PushColor(ImGuiCol.CheckMark, P.Host))
        {
            using (ImRaii.Disabled(here == null))
                if (ImGui.Checkbox("Announce broadcast", ref announce)) Remember();
            if (here == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Stand inside your venue's house to announce your broadcast there.");
            if (here != null) CheckNote(here.Label);
            if (ImGui.Checkbox("Show a floating live badge", ref liveBadge)) Remember();
        }

        ImGui.Unindent(pad); inset = 0;
        var max = new Vector2(start.X + width, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + pad);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, max, U32(P.Surface), R(4));
        if (hovered) draw.AddRectFilled(start, new Vector2(max.X, start.Y + header), U32(P.TileHover), R(4), ImDrawFlags.RoundCornersTop);
        draw.AddRect(start, max, U32(Edge), R(4));
        draw.ChannelsMerge();
        MarqueeDraw.Advance(start, max);
    }

    private void StartBroadcast(VenueLocation? here)
    {
        var source = devices.First(d => d.Id == sourceId);
        string? mic = HasMicrophone ? microphoneId : null;
        var options = new BroadcastOptions(bitrate, venueName, characterName(), announce ? here?.Key : null);
        Run(Pending.Broadcast, () => session.BroadcastAsync(source, mic, false, "", "", automaticInternet: true, options: options));
    }

    private void QualityPicker(float width)
    {
        int index = Array.FindIndex(Qualities, q => q.Bitrate == bitrate);
        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo("##quality", index >= 0 ? Qualities[index].Name : $"{bitrate / 1000} kbps");
        if (!combo) return;
        foreach (var (name, value) in Qualities)
            if (ImGui.Selectable(name, value == bitrate)) { bitrate = value; Remember(); }
    }

    private void MusicSlider(float column)
    {
        bool muted = session.MusicMuted;
        if (MutableSlider("Music", ref music, ref muted, FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute, P.Host, column)) session.MusicGain = music;
        session.MusicMuted = muted;
    }

    private void MicSlider(float column)
    {
        bool muted = session.MicMuted;
        if (MutableSlider("Mic", ref microphone, ref muted, FontAwesomeIcon.Microphone, FontAwesomeIcon.MicrophoneSlash, P.Host, column)) session.MicGain = microphone;
        session.MicMuted = muted;
    }

    /// <summary>The host's broadcast control, docked at the bottom of the screen. Returns true when its action was clicked.</summary>
    private bool Transport(Bar state, bool enabled, bool allMuted)
    {
        var draw = ImGui.GetWindowDrawList();
        float width = Avail, height = S(52), line = ImGui.GetTextLineHeight();
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        float mid = (min.Y + max.Y) / 2, round = R(4);
        bool clicked = false;

        if (state == Bar.Idle)
        {
            using (ImRaii.Disabled(!enabled)) clicked = ImGui.InvisibleButton("##transport", new Vector2(width, height));
            bool hovered = enabled && ImGui.IsItemHovered();
            float fade = enabled ? 1 : .5f;
            draw.AddRectFilled(min, max, U32(P.Host, (hovered ? .2f : .12f) * fade), round);
            draw.AddRect(min, max, U32(P.Host, fade), round);
            const string label = "Start broadcasting";
            float text = ImGui.CalcTextSize(label).X, x = min.X + (width - S(10) - S(10) - text) / 2;
            draw.AddCircleFilled(new Vector2(x + S(5), mid), S(5), U32(P.Host, fade));
            draw.AddText(new Vector2(x + S(20), mid - line / 2), U32(Ink, fade), label);
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            return clicked && enabled;
        }

        if (state == Bar.Live)
        {
            float outer = MarqueeDraw.Breath(2.6), inner = MarqueeDraw.Breath(2.6, .3), fade = allMuted ? .75f : 1;
            draw.AddRectFilled(min - new Vector2(S(4)), max + new Vector2(S(4)), U32(P.HostGlowOuter, outer * fade), round + S(4));
            draw.AddRectFilled(min - new Vector2(S(2)), max + new Vector2(S(2)), U32(P.HostGlowInner, inner * fade), round + S(2));
            draw.AddRectFilled(min, max, U32(P.Host, fade), round);
            var dot = new Vector2(min.X + S(19), mid);
            if (allMuted) draw.AddCircle(dot, S(5), U32(P.OnHost), 0, S(1.5f));
            else draw.AddCircleFilled(dot, S(5), U32(P.OnHost, MarqueeDraw.Breath(1.2)));
            float x = dot.X + S(15);
            draw.AddText(new Vector2(x, mid - line / 2), U32(P.OnHost), "LIVE");
            x += ImGui.CalcTextSize("LIVE").X + S(10);
            using (mono.Push())
            {
                string time = FormatTime(session.LiveFor);
                var size = ImGui.CalcTextSize(time);
                draw.AddText(new Vector2(x, mid - size.Y / 2), U32(P.OnHost), time);
                x += size.X + S(12);
            }
            Icon(draw, FontAwesomeIcon.Headphones, new Vector2(x + S(7), mid), U32(P.OnHost), small: true);
            draw.AddText(new Vector2(x + S(20), mid - line / 2), U32(P.OnHost), session.Listeners.ToString());

            const string stop = "Stop broadcasting";
            float button = ImGui.CalcTextSize(stop).X + S(24);
            ImGui.SetCursorScreenPos(new Vector2(max.X - S(8) - button, mid - S(18)));
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0, 0, 0, .28f)).Push(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, .4f))
                       .Push(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, .5f)).Push(ImGuiCol.Text, P.OnHost).Push(ImGuiCol.Border, new Vector4(1, 1, 1, .25f)))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f))
                clicked = ImGui.Button(stop, new Vector2(button, S(36)));
        }
        else
        {
            draw.AddRectFilled(min, max, U32(P.HostTint), round);
            draw.AddRect(min, max, U32(P.Host), round);
            draw.AddCircleFilled(new Vector2(min.X + S(19), mid), S(5), U32(P.Host, MarqueeDraw.Blink(.9) ? 1 : .25f));
            draw.AddText(new Vector2(min.X + S(34), mid - line / 2), U32(Ink), state == Bar.Starting ? "Going live…" : "Stopping…");
            if (state == Bar.Starting)
            {
                float button = ImGui.CalcTextSize("Cancel").X + S(24);
                ImGui.SetCursorScreenPos(new Vector2(max.X - S(8) - button, mid - S(18)));
                clicked = ColorButton("Cancel", Raised, RaisedHover, Ink, button, S(36));
            }
        }
        MarqueeDraw.Advance(min, max);
        return clicked;
    }

    // ── Listener (B3, B10, B8, B9) ───────────────────────────────────────────

    private void DrawListener(bool busy)
    {
        bool listening = session.Active && !session.Broadcasting;
        Header("LISTENER", P.Listen, P.OnListen, !busy && !session.Active);
        var venue = session.Joined;

        if (listening)
        {
            var lines = new List<SignLine> { new("NOW LISTENING", P.Listen), new(VenueName(venue), Ink) };
            if (!string.IsNullOrEmpty(venue?.HostName)) lines.Add(new("Hosted by " + venue.HostName, Muted));
            MarqueeDraw.Sign(mono, Bulbs.Chasing, P.Listen, P.ListenEdge, lines);
            Block();
            Admitted();
            Block();
            Meter("Signal", session.Peak, S(56));
            ListenVolume();
            Block();
            WalkIn(true);
            Block();
            if (ColorButton("Disconnect", Raised, RaisedHover, Ink, Avail, S(40))) Run(Pending.Stop, session.StopAsync);
            return;
        }

        if (busy)
        {
            bool stopping = pending == Pending.Stop;
            int lit = stopping ? Math.Max(0, 20 - (int)((ImGui.GetTime() - pendingSince) / .06))
                : session.Status.StartsWith("Looking up", StringComparison.Ordinal) ? 10 : 5;
            var lines = new List<SignLine> { new(stopping ? "DISCONNECTING" : "CONNECTING", P.Listen), new(VenueName(venue), Ink) };
            if (!string.IsNullOrEmpty(venue?.HostName)) lines.Add(new("Hosted by " + venue.HostName, Muted));
            MarqueeDraw.Sign(mono, Bulbs.Filling, P.Listen, Edge, lines, lit: lit);
            Block();
            var dot = ImGui.GetCursorScreenPos() + new Vector2(S(5), ImGui.GetTextLineHeight() / 2);
            ImGui.GetWindowDrawList().AddCircleFilled(dot, S(4), U32(P.Listen, MarqueeDraw.Blink(.9) ? 1 : .25f));
            ImGui.Dummy(new Vector2(S(10), ImGui.GetTextLineHeight()));
            ImGui.SameLine(0, S(8));
            Wrapped(Body, session.Status);
            Block();
            using (ImRaii.Disabled(stopping))
                if (ColorButton("Cancel", Raised, RaisedHover, Ink, Avail, S(40))) Run(Pending.Stop, session.StopAsync);
            return;
        }

        bool failed = session.Faulted;
        MarqueeDraw.Sign(mono, Bulbs.Off, P.Listen, Edge,
            [new("THE LIGHTS ARE OFF", Muted), new("Enter a code, or walk into a venue that's announced.", Muted)]);
        Block();
        JoinTicket(failed);
        if (failed) IconLine(FontAwesomeIcon.ExclamationCircle, Error, session.Status);
        Block();
        ListenVolume();
        Block();
        WalkIn(false);
    }

    private static string VenueName(Invite? venue) =>
        string.IsNullOrEmpty(venue?.Venue) ? "LIVE VENUE" : venue.Venue.ToUpperInvariant();

    private void JoinTicket(bool failed)
    {
        var edge = failed ? Error : P.ListenEdge;
        float stub = S(102), line = ImGui.GetTextLineHeight();
        var ticket = MarqueeDraw.Ticket(S(76), stub, P.Surface, edge, edge);
        var draw = ImGui.GetWindowDrawList();
        float x = ticket.Min.X + S(12), top = ticket.Mid - (line + S(4) + ImGui.GetFrameHeight()) / 2;
        draw.AddText(new Vector2(x, top), U32(Muted), "VENUE CODE OR INVITE");

        float paste = ImGui.CalcTextSize("Paste").X + S(20);
        ImGui.SetCursorScreenPos(new Vector2(x, top + line + S(4)));
        ImGui.SetNextItemWidth(ticket.Perforation - S(12) - x - paste - S(6));
        bool entered;
        using (mono.Push()) entered = ImGui.InputTextWithHint("##join", "K3M-9QX", ref invitation, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
        ImGui.SameLine(0, S(6));
        if (ImGui.Button("Paste", new Vector2(paste, 0))) { invitation = ImGui.GetClipboardText().Trim(); Remember(); }

        bool empty = string.IsNullOrWhiteSpace(invitation);
        var min = new Vector2(ticket.Perforation + S(5), ticket.Min.Y + S(8));
        var size = new Vector2(S(92), ticket.Max.Y - ticket.Min.Y - S(16));
        ImGui.SetCursorScreenPos(min);
        bool clicked;
        using (ImRaii.Disabled(empty)) clicked = ImGui.InvisibleButton("##join-button", size);
        bool hovered = !empty && ImGui.IsItemHovered();
        float fade = empty ? .5f : 1;
        draw.AddRectFilled(min, min + size, U32(hovered ? P.ListenButtonHover : P.Listen, fade), R(4));
        string label = failed ? "Try again" : "Join";
        float labelWidth = ImGui.CalcTextSize(label).X, center = min.X + size.X / 2, mid = min.Y + size.Y / 2;
        Icon(draw, failed ? FontAwesomeIcon.Redo : FontAwesomeIcon.Headphones, new Vector2(center, mid - S(9)), U32(P.OnListen, fade), small: true);
        draw.AddText(new Vector2(center - labelWidth / 2, mid + S(1)), U32(P.OnListen, fade), label);
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        MarqueeDraw.Advance(ticket);

        if ((clicked || entered) && !empty) Join();
    }

    private void Join()
    {
        string value = invitation;
        joinedWith = VenueCodes.Normalize(value) is { } code ? VenueCodes.Display(code) : null;
        Run(Pending.Listen, () => session.ListenAsync(value));
    }

    private void Admitted()
    {
        string elapsed = FormatTime(session.LiveFor);
        var ticket = MarqueeDraw.Ticket(S(58), S(110), Field, null, Edge);
        var draw = ImGui.GetWindowDrawList();
        float x = ticket.Min.X + S(14), line = ImGui.GetTextLineHeight();
        string how = session.JoinedLocation != null ? "Walked in · " + (VenueLocation.Current()?.Label ?? "this venue")
            : joinedWith != null ? "Joined with code " + joinedWith : "Joined with an invite";
        draw.AddText(new Vector2(x, ticket.Mid - line - S(1)), U32(Muted), "ADMITTED");
        draw.AddText(new Vector2(x, ticket.Mid + S(1)), U32(Ink), MarqueeDraw.Fit(how, ticket.Perforation - x - S(10)));
        using (mono.Push())
        {
            var size = ImGui.CalcTextSize(elapsed);
            draw.AddText(new Vector2(ticket.Perforation + (ticket.Max.X - ticket.Perforation - size.X) / 2, ticket.Mid - size.Y / 2), U32(Body), elapsed);
        }
        MarqueeDraw.Advance(ticket);
    }

    private void WalkIn(bool compact)
    {
        float pad = S(12);
        var panel = MarqueeDraw.BeginPanel(pad, pad);
        inset = pad;
        Label("WALK-IN VENUES");
        autoJoin = settings.AutoJoin;
        using (ImRaii.PushColor(ImGuiCol.CheckMark, P.Listen))
        {
            if (compact)
            {
                if (ImGui.Checkbox("Auto Discover", ref watchVenues)) Remember();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(GuideWindow.AutoDiscover);
                ImGui.SameLine(0, S(16));
                using (ImRaii.Disabled(!watchVenues))
                    if (ImGui.Checkbox("Auto Join", ref autoJoin)) Remember();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(GuideWindow.AutoJoin);
                if (session.JoinedLocation != null) ImGui.TextColored(Muted, "Leaving the house disconnects you.");
            }
            else
            {
                if (ImGui.Checkbox("Auto Discover Venues", ref watchVenues)) Remember();
                CheckNote(GuideWindow.AutoDiscover);
                using (ImRaii.Disabled(!watchVenues))
                {
                    if (ImGui.Checkbox("Auto Join Venues", ref autoJoin)) Remember();
                    CheckNote(GuideWindow.AutoJoin);
                }
            }
        }
        inset = 0;
        MarqueeDraw.EndPanel(panel, pad, pad, P.Surface, Edge);
    }

    private void ListenVolume()
    {
        bool muted = session.ListenMuted;
        if (MutableSlider("Volume", ref volume, ref muted, FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute, P.Listen, S(56))) session.Volume = volume;
        session.ListenMuted = muted;
    }

    // ── Widgets ──────────────────────────────────────────────────────────────

    private static void Block() => ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(4));

    private static void RowLabel(string text, float column)
    {
        float x = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Muted, text);
        ImGui.SameLine(x + column);
    }

    private void Divider()
    {
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(at, at + new Vector2(Avail, 0), U32(Edge));
        ImGui.Dummy(new Vector2(Avail, S(1)));
    }

    /// <summary>A muted explanation tucked under the checkbox just drawn, aligned with its label.</summary>
    private void CheckNote(string text)
    {
        float indent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - S(6));
        using (ImRaii.PushIndent(indent, false))
        using (ImRaii.TextWrapPos(ImGui.GetWindowContentRegionMax().X - inset))
            ImGui.TextColored(Muted, text);
    }

    private void IconLine(FontAwesomeIcon icon, Vector4 color, string text)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine(0, S(6));
        using (ImRaii.TextWrapPos(ImGui.GetWindowContentRegionMax().X - inset)) ImGui.TextColored(color, text);
    }

    private void Meter(string label, float peak, float column)
    {
        const int segments = 16;
        RowLabel(label, column);
        float width = Avail - S(34) - ImGui.GetStyle().ItemSpacing.X, height = S(12), gap = S(2);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - height) / 2);
        float cell = (width - gap * (segments - 1)) / segments;
        int lit = (int)MathF.Round(Math.Clamp(peak, 0, 1) * segments);
        var draw = ImGui.GetWindowDrawList();
        for (int i = 0; i < segments; i++)
        {
            var color = i >= lit ? Field : i < 10 ? MeterGreen : i < 14 ? MeterAmber : MeterRed;
            var a = origin + new Vector2(i * (cell + gap), 0);
            draw.AddRectFilled(a, a + new Vector2(cell, height), U32(color), R(1));
        }
        ImGui.Dummy(new Vector2(width, ImGui.GetFrameHeight()));
    }

    /// <summary>A labelled 0–100% slider with a mute toggle at its end. Returns true when the level moved.</summary>
    private bool MutableSlider(string label, ref float value, ref bool muted, FontAwesomeIcon on, FontAwesomeIcon off, Vector4 accent, float column)
    {
        using var id = ImRaii.PushId(label);
        RowLabel(label, column);
        float toggle = S(34);
        int percent = (int)MathF.Round(value * 100);
        ImGui.SetNextItemWidth(Avail - toggle - ImGui.GetStyle().ItemSpacing.X);
        bool changed;
        using (ImRaii.Disabled(muted))
        using (ImRaii.PushColor(ImGuiCol.SliderGrab, accent).Push(ImGuiCol.SliderGrabActive, accent))
            changed = ImGui.SliderInt("##level", ref percent, 0, 100, muted ? "Muted" : "%d%%");
        if (changed) value = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, Raised).Push(ImGuiCol.Text, muted ? Error : Ink).Push(ImGuiCol.Border, Error))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, muted ? 1f : 0f))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            if (ImGui.Button((muted ? off : on).ToIconString() + "##mute", new Vector2(toggle, ImGui.GetFrameHeight()))) muted = !muted;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(muted ? $"Unmute {label.ToLowerInvariant()}" : $"Mute {label.ToLowerInvariant()}");
        return changed;
    }

    private static bool ColorButton(string label, Vector4 color, Vector4 hover, Vector4 ink, float width, float height)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, color).Push(ImGuiCol.ButtonHovered, hover).Push(ImGuiCol.ButtonActive, hover).Push(ImGuiCol.Text, ink);
        return ImGui.Button(label, new Vector2(width, height));
    }

    private static bool LinkButton(string text)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero).Push(ImGuiCol.ButtonHovered, Field).Push(ImGuiCol.ButtonActive, Field).Push(ImGuiCol.Text, Muted);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(S(4), S(3)));
        return ImGui.Button(text);
    }

    internal static bool IconButton(FontAwesomeIcon icon, string id, float size)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.Button(icon.ToIconString() + id, new Vector2(size, size));
    }

    private bool Copied(string what) => copiedWhat == what && ImGui.GetTime() - copiedAt < 1.5;
    private void Copy(string what, string text) { ImGui.SetClipboardText(text); copiedWhat = what; copiedAt = ImGui.GetTime(); }
    private static float CopyWidth() =>
        ImGui.CalcTextSize("Copied").X + ImGui.CalcTextSize(FontAwesomeIcon.Copy.ToIconString()).X + S(34);

    private bool CopyButton(string what, float width, float height)
    {
        string label = Copied(what) ? "Copied" : "Copy";
        var min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button("##copy-" + what, new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        string glyph = FontAwesomeIcon.Copy.ToIconString();
        Vector2 glyphSize;
        using (ImRaii.PushFont(UiBuilder.IconFont)) glyphSize = ImGui.CalcTextSize(glyph);
        var textSize = ImGui.CalcTextSize(label);
        float x = min.X + (width - glyphSize.X - S(6) - textSize.X) / 2, mid = min.Y + height / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont)) draw.AddText(new Vector2(x, mid - glyphSize.Y / 2), U32(Ink), glyph);
        draw.AddText(new Vector2(x + glyphSize.X + S(6), mid - textSize.Y / 2), U32(Ink), label);
        return clicked;
    }

    private void DevicePicker(string id, ref string selected, bool microphoneOnly, float width)
    {
        string selectedId = selected;
        var current = devices.FirstOrDefault(d => d.Id == selectedId);
        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo(id, current?.Name ?? (microphoneOnly ? "None" : "Choose an audio source"));
        if (!combo) return;
        if (microphoneOnly && ImGui.Selectable("None", selected.Length == 0)) { selected = ""; Remember(); }
        foreach (var device in devices.Where(d => !microphoneOnly || !d.IsOutput))
        {
            string text = (device.IsOutput ? "Output: " : "Input: ") + device.Name;
            if (ImGui.Selectable(text + "##" + device.Id, device.Id == selected)) { selected = device.Id; Remember(); }
        }
    }

    internal static void Icon(ImDrawListPtr draw, FontAwesomeIcon icon, Vector2 center, uint color, bool small = false)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        string glyph = icon.ToIconString();
        var size = ImGui.CalcTextSize(glyph);
        if (!small) { draw.AddText(center - size / 2, color, glyph); return; }
        // The icon font has one size; "small" icons are drawn at 80% through the draw list's font-size overload.
        float scale = .8f;
        draw.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * scale, center - size * scale / 2, color, glyph);
    }

    private static void Label(string text) => ImGui.TextColored(Muted, text);

    internal static void Wrapped(Vector4 color, string text)
    {
        using var wrap = ImRaii.TextWrapPos(0);
        ImGui.TextColored(color, text);
    }

    private static void Centered(Vector4 color, string text)
    {
        float width = ImGui.GetContentRegionAvail().X, size = ImGui.CalcTextSize(text).X;
        if (size >= width) { Wrapped(color, text); return; }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - size) / 2);
        ImGui.TextColored(color, text);
    }

    internal static string FormatTime(TimeSpan span) => $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
