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

    private static readonly (string Name, int Bitrate)[] Qualities =
        [("Voice · 64 kbps", 64000), ("Standard · 96 kbps", 96000), ("High · 128 kbps", 128000), ("Studio · 192 kbps", 192000)];
    // Encryption, framing, WebSocket, TLS and TCP add roughly this much per guest on top of the audio
    // itself (50 packets a second). An estimate, deliberately on the high side.
    private const int OverheadBitsPerSecond = 50_000;

    private readonly Session session;
    private readonly Settings settings;
    private readonly Action save;
    private readonly Func<string> characterName;
    private readonly IFontHandle mono;
    private List<AudioDevice> devices = [];
    private Role role;
    private bool rememberRole, addMicrophone, localHosting, automaticInternet, shortCodes, hideCodes, announce, watchVenues, autoJoin;
    private string sourceId, microphoneId, invitation, remote, advertised, relayKey, venueName, directoryServer;
    private string deviceError = "";
    private float volume, music, microphone;
    private int bitrate;
    private double copiedAt = -10;
    private string copiedWhat = "";
    private Task? operation;
    private Pending pending;
    private (int, int) pushed;

    private static Palette P => Current;

    public VenueWindow(Session session, Settings settings, Action save, Func<string> characterName, IFontHandle mono)
        : base("Venue Audio###VenueAudioMain")
    {
        this.session = session; this.settings = settings; this.save = save; this.characterName = characterName; this.mono = mono;
        role = settings.Role; rememberRole = settings.RememberRole;
        sourceId = settings.SourceId; microphoneId = settings.MicrophoneId; addMicrophone = settings.AddMicrophone;
        invitation = settings.LastInvite; remote = settings.RemoteServer; advertised = settings.AdvertisedHost;
        relayKey = settings.RelayAccessKey; venueName = settings.VenueName; directoryServer = settings.DirectoryServer;
        localHosting = settings.LocalHosting; automaticInternet = settings.AutomaticInternet;
        shortCodes = settings.ShortCodes; hideCodes = settings.HideCodes; announce = settings.Announce;
        watchVenues = settings.WatchVenues; autoJoin = settings.AutoJoin;
        volume = settings.Volume; music = settings.MusicGain; microphone = settings.MicGain; bitrate = settings.Bitrate;
        Size = new Vector2(460, 680); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(float.MaxValue) };
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

    public override void PreDraw() => pushed = Theme.Push(settings.Style);
    public override void PostDraw() => Theme.Pop(pushed);

    private void Refresh()
    {
        try
        {
            devices = AudioDevices.List(); deviceError = "";
            if (!devices.Any(d => d.Id == sourceId)) sourceId =
                devices.FirstOrDefault(d => !d.IsOutput && d.Name.Contains("Stream Mix", StringComparison.OrdinalIgnoreCase))?.Id
                ?? devices.FirstOrDefault(d => d.IsOutput)?.Id ?? "";
            if (!devices.Any(d => d.Id == microphoneId)) microphoneId = devices.FirstOrDefault(d => !d.IsOutput)?.Id ?? "";
        }
        catch (Exception e) { deviceError = e.Message; }
    }

    private void Remember()
    {
        settings.Role = role; settings.RememberRole = rememberRole;
        settings.SourceId = sourceId; settings.MicrophoneId = microphoneId; settings.AddMicrophone = addMicrophone;
        settings.LastInvite = invitation; settings.VenueName = venueName; settings.DirectoryServer = directoryServer;
        settings.RemoteServer = remote; settings.AdvertisedHost = advertised; settings.LocalHosting = localHosting;
        settings.RelayAccessKey = relayKey; settings.AutomaticInternet = automaticInternet;
        settings.ShortCodes = shortCodes; settings.HideCodes = hideCodes; settings.Announce = announce;
        settings.WatchVenues = watchVenues; settings.AutoJoin = autoJoin;
        settings.Volume = volume; settings.MusicGain = music; settings.MicGain = microphone; settings.Bitrate = bitrate;
        save();
    }

    private void Run(Pending kind, Func<Task> action)
    {
        Remember(); pending = kind; operation = Task.Run(action);
    }

    public override void Draw()
    {
        bool busy = operation is { IsCompleted: false };
        if (!busy) pending = Pending.None;
        switch (role)
        {
            case Role.Host: DrawHost(busy); break;
            case Role.Listener: DrawListener(busy); break;
            default: DrawRolePicker(); break;
        }
    }

    // ── Role ─────────────────────────────────────────────────────────────────

    private void DrawRolePicker()
    {
        Gap(4);
        ImGui.Text("How are you using Venue Audio?");
        ImGui.TextColored(Muted, "Pick one to get started. You can switch anytime.");
        Gap(4);
        if (RoleCard("host", FontAwesomeIcon.BroadcastTower, "Host", "Broadcast your venue's music to guests", P.HostBadge, P.HostBadgeInk))
            PickRole(Role.Host);
        if (RoleCard("listener", FontAwesomeIcon.Headphones, "Listener", "Tune in with a venue code, or walk in", P.ListenBadge, P.ListenIcon))
            PickRole(Role.Listener);
        Gap();
        using (ImRaii.PushColor(ImGuiCol.CheckMark, P.Grab))
            if (ImGui.Checkbox("Remember my choice next time", ref rememberRole)) Remember();
        Footer("Choosing a role doesn't start any capture or open your microphone. The palette button in the title bar changes the style.");
    }

    private void PickRole(Role value) { role = value; Remember(); }

    private static bool RoleCard(string id, FontAwesomeIcon icon, string title, string subtitle, Vector4 badge, Vector4 badgeInk)
    {
        float width = ImGui.GetContentRegionAvail().X, height = S(84);
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        bool clicked = ImGui.InvisibleButton("##role-" + id, new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(hovered ? P.TileHover : P.Tile), R(6));
        draw.AddRect(min, max, U32(hovered ? P.EdgeHover : Edge), R(6));
        var center = new Vector2(min.X + S(16) + S(22), min.Y + height / 2);
        draw.AddCircleFilled(center, S(22), U32(badge));
        Icon(draw, icon, center, U32(badgeInk));
        float textX = center.X + S(22) + S(14);
        float line = ImGui.GetTextLineHeight();
        draw.AddText(new Vector2(textX, center.Y - line - S(1)), U32(Ink), title);
        draw.AddText(new Vector2(textX, center.Y + S(1)), U32(Muted), subtitle);
        Icon(draw, FontAwesomeIcon.ChevronRight, new Vector2(max.X - S(22), center.Y), U32(Muted));
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    private void Header(string label, Vector4 badge, Vector4 ink, bool canSwitch)
    {
        var size = ImGui.CalcTextSize(label) + new Vector2(S(18), S(6));
        var min = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U32(badge), size.Y / 2);
        draw.AddText(min + new Vector2(S(9), S(3)), U32(ink), label);
        ImGui.Dummy(size);
        const string link = "Switch role";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(link).X - S(8));
        using (ImRaii.Disabled(!canSwitch))
            if (LinkButton(link)) PickRole(Role.None);
        Gap();
    }

    // ── Host ─────────────────────────────────────────────────────────────────

    private void DrawHost(bool busy)
    {
        bool live = session.Broadcasting;
        bool connecting = busy && pending == Pending.Broadcast;
        bool locked = busy || session.Active;
        var here = VenueLocation.Current();
        Header("HOST", P.HostBadge, P.HostBadgeInk, !locked);

        Label("VENUE NAME");
        using (ImRaii.Disabled(locked))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##venue", "[Your venue's name]", ref venueName, Invite.MaxLabel);
            if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
        }
        string host = characterName();
        ImGui.TextColored(Muted, host.Length > 0 ? $"Guests see this with your name, {host}." : "Guests see this with your character name.");

        Gap();
        DrawShare(live);

        using (ImRaii.Disabled(locked || !shortCodes || here == null))
            if (ImGui.Checkbox("Announce at my venue", ref announce)) Remember();
        Indented(Muted, !shortCodes ? "Needs short codes (Advanced hosting)."
            : here == null ? "Go inside your venue's house to announce it. Guests who walk in get a join prompt."
            : $"{here.Label}. Anyone who walks in gets a join prompt, no code needed.");

        Gap();
        Label("AUDIO");
        using (ImRaii.Disabled(locked))
        {
            float refresh = ImGui.GetFrameHeight();
            DevicePicker("##source", ref sourceId, false, ImGui.GetContentRegionAvail().X - refresh - ImGui.GetStyle().ItemSpacing.X);
            ImGui.SameLine();
            if (IconButton(FontAwesomeIcon.SyncAlt, "##refresh", refresh)) Refresh();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Refresh audio devices");
        }
        if (!string.IsNullOrEmpty(deviceError)) Wrapped(Error, deviceError);
        Meter("Level", live ? session.Peak : 0);
        bool musicMuted = session.MusicMuted;
        if (MutableSlider("Music", ref music, ref musicMuted, FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute, null)) session.MusicGain = music;
        session.MusicMuted = musicMuted;
        using (ImRaii.Disabled(locked))
            if (ImGui.Checkbox("Add my microphone to the mix", ref addMicrophone)) Remember();
        if (addMicrophone)
        {
            using (ImRaii.Disabled(locked)) DevicePicker("##mic", ref microphoneId, true, ImGui.GetContentRegionAvail().X);
            bool micMuted = session.MicMuted;
            if (MutableSlider("Mic", ref microphone, ref micMuted, FontAwesomeIcon.Microphone, FontAwesomeIcon.MicrophoneSlash, null)) session.MicGain = microphone;
            session.MicMuted = micMuted;
        }
        else Indented(Muted, "Leave this off if your mixer already includes your microphone.");

        Gap();
        Label("SOUND QUALITY");
        using (ImRaii.Disabled(locked)) QualityPicker();
        int perGuest = (live ? session.Bitrate : bitrate) + OverheadBitsPerSecond;
        Indented(Muted, live && session.Listeners > 0
            ? $"Uploading about {Mbps(perGuest * session.Listeners)} for {session.Listeners} guest(s). Each guest adds {Mbps(perGuest)}."
            : $"Each guest adds about {Mbps(perGuest)} to your upload. 50 guests ≈ {Mbps(perGuest * 50)}.");

        Gap(6);
        bool ready = devices.Any(d => d.Id == sourceId) && (!addMicrophone || devices.Any(d => d.Id == microphoneId)) &&
            (automaticInternet || localHosting || (!string.IsNullOrWhiteSpace(remote) && !string.IsNullOrWhiteSpace(relayKey)));
        if (live)
        {
            if (BroadcastButton("LIVE  ·  Stop broadcasting", FormatTime(session.LiveFor), true, true)) Run(Pending.Stop, session.StopAsync);
            string onAir = session.MusicMuted && (!addMicrophone || session.MicMuted) ? "On air, but everything is muted." : "You are on air.";
            Centered(P.HostBadgeInk, $"{onAir} {session.Listeners} listening.");
        }
        else if (connecting)
        {
            if (BroadcastButton("Going live…  ·  Cancel", null, false, true)) Run(Pending.Stop, session.StopAsync);
            Centered(Muted, session.Status);
        }
        else if (busy)
        {
            BroadcastButton("Stopping…", null, false, false);
            Centered(Muted, session.Status);
        }
        else
        {
            if (BroadcastButton("Start broadcasting", null, false, ready)) StartBroadcast(here);
            if (session.Faulted) Centered(Error, session.Status);
            else Centered(Muted, ready ? "Nothing is captured until you start." : "Choose an audio source to start.");
        }

        Gap(4);
        using (ImRaii.Disabled(locked)) DrawAdvanced();
        Footer("Penumbra/Lightless visuals can run normally; set booth music to Off.");
    }

    private void DrawShare(bool live)
    {
        string code = session.Code;
        if (live && code.Length > 0)
        {
            Label("VENUE CODE");
            string shown = hideCodes ? "•••-•••" : VenueCodes.Display(code);
            float eye = ImGui.GetFrameHeight();
            float copy = CopyWidth();
            var min = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X - eye - copy - ImGui.GetStyle().ItemSpacing.X * 2, height = ImGui.GetFrameHeight();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + new Vector2(width, height), U32(Field), R(3));
            draw.AddRect(min, min + new Vector2(width, height), U32(Edge), R(3));
            using (mono.Push())
            {
                var size = ImGui.CalcTextSize(shown);
                draw.AddText(min + new Vector2(S(12), (height - size.Y) / 2), U32(Ink), shown);
            }
            ImGui.Dummy(new Vector2(width, height));
            ImGui.SameLine();
            if (IconButton(hideCodes ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, "##hide", eye)) { hideCodes = !hideCodes; Remember(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hideCodes ? "Show the code" : "Hide the code while you stream");
            ImGui.SameLine();
            if (CopyButton("code", copy)) Copy("code", VenueCodes.Display(code));
            Indented(Muted, "Guests type this in Venue Audio. A new code is made each time you go live.");
            if (LinkButton(Copied("invite") ? "Full invite copied" : "Copy the full invite instead")) Copy("invite", session.Invite);
            if (session.CodeProblem.Length > 0) Indented(Warn, session.CodeProblem);
        }
        else if (live)
        {
            Label("GUEST INVITE");
            float copy = CopyWidth();
            string invite = hideCodes ? new string('•', 24) : session.Invite;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - copy - ImGui.GetStyle().ItemSpacing.X);
            using (mono.Push()) ImGui.InputText("##invite", ref invite, 1024, ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            if (CopyButton("invite", copy)) Copy("invite", session.Invite);
            if (session.CodeProblem.Length > 0) Indented(Warn, session.CodeProblem);
            Indented(Muted, "Send the whole invite. It holds the room's audio key, so share it only with people you want listening.");
        }
        else
        {
            Label("VENUE CODE");
            Indented(Muted, shortCodes ? "Start broadcasting to get a six-character code for your guests." : "Start broadcasting to get a guest invite.");
        }
    }

    private void StartBroadcast(VenueLocation? here)
    {
        var source = devices.First(d => d.Id == sourceId);
        string? mic = addMicrophone ? microphoneId : null;
        bool local = localHosting, auto = automaticInternet;
        string address = remote, publicHost = advertised, accessKey = relayKey;
        var options = new BroadcastOptions(bitrate, venueName, characterName(), announce && shortCodes ? here?.Key : null, shortCodes);
        Run(Pending.Broadcast, () => session.BroadcastAsync(source, mic, local, address, publicHost, accessKey, auto, options: options));
    }

    private void QualityPicker()
    {
        int index = Array.FindIndex(Qualities, q => q.Bitrate == bitrate);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##quality", index >= 0 ? Qualities[index].Name : $"{bitrate / 1000} kbps");
        if (!combo) return;
        foreach (var (name, value) in Qualities)
            if (ImGui.Selectable(name, value == bitrate)) { bitrate = value; Remember(); }
    }

    private void DrawAdvanced()
    {
        if (!ImGui.CollapsingHeader("Advanced hosting")) return;
        using var wrap = ImRaii.TextWrapPos(0);
        if (ImGui.Checkbox("Short venue codes", ref shortCodes)) Remember();
        if (shortCodes)
        {
            ImGui.TextColored(Muted, "The full invite is stored, encrypted, in the venue directory while you're live, so guests can type a code instead.");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##directory", "Venue directory address", ref directoryServer, 256);
            if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
            ImGui.TextColored(Muted, "Changes take effect after reloading the plugin.");
        }
        ImGui.Separator();
        if (ImGui.Checkbox("Automatic internet hosting (temporary address)", ref automaticInternet)) Remember();
        if (automaticInternet)
        {
            ImGui.TextColored(Muted, "Opens a temporary secure internet address when you go live, so friends on other networks can join. No account or router changes. The address changes each time.");
            return;
        }
        if (ImGui.Checkbox("Host on this PC instead of a relay", ref localHosting)) Remember();
        if (localHosting)
        {
            ImGui.TextColored(Muted, "For guests on your own network. Windows may ask to allow network access. Audio is still encrypted; the connection itself is not.");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##advertised", "Address guests reach (blank: this PC's local IPv4)", ref advertised, 256);
            if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
            ImGui.TextColored(Muted, $"Listening port: {Wire.Port}.");
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##remote", "Relay address (https://…)", ref remote, 256);
            if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##relaykey", "DJ access key", ref relayKey, 256, ImGuiInputTextFlags.Password);
            if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
            ImGui.TextColored(Muted, "The DJ key is saved on this PC only and is never put in guest invites. A sleeping free server can take about a minute to wake.");
        }
    }

    // ── Listener ─────────────────────────────────────────────────────────────

    private void DrawListener(bool busy)
    {
        bool listening = session.Active && !session.Broadcasting;
        Header("LISTENER", P.ListenBadge, P.ListenBadgeInk, !busy && !session.Active);

        if (listening)
        {
            NowPlayingCard("NOW LISTENING", session.Joined, FormatTime(session.LiveFor));
            Meter("Signal", session.Peak);
            ListenVolume();
            if (ColorButton("Disconnect", Raised, RaisedHover, Ink, -1)) Run(Pending.Stop, session.StopAsync);
            if (session.JoinedLocation != null) Indented(Muted, "You joined by walking in. Leaving the house disconnects you.");
            MicrophoneOff();
            return;
        }
        if (busy)
        {
            bool stopping = pending == Pending.Stop;
            NowPlayingCard(stopping ? "DISCONNECTING" : "CONNECTING", session.Joined, null);
            Wrapped(Muted, session.Status);
            using (ImRaii.Disabled(stopping))
                if (ColorButton("Cancel", Raised, RaisedHover, Ink, -1)) Run(Pending.Stop, session.StopAsync);
            return;
        }

        Label("VENUE CODE OR INVITE");
        float paste = ImGui.CalcTextSize("Paste").X + S(28);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - paste - ImGui.GetStyle().ItemSpacing.X);
        using (mono.Push()) ImGui.InputTextWithHint("##join", "K3M-9QX", ref invitation, 1024);
        if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
        ImGui.SameLine();
        if (ImGui.Button("Paste", new Vector2(paste, 0))) { invitation = ImGui.GetClipboardText().Trim(); Remember(); }
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(invitation)))
            if (ColorButton("Join venue", P.ListenButton, P.ListenButtonHover, P.OnListen, -1)) { string value = invitation; Run(Pending.Listen, () => session.ListenAsync(value)); }
        if (session.Faulted) Wrapped(Error, session.Status);
        Indented(Muted, "Ask the host for their code. Listening never turns on your microphone.");

        Gap();
        ListenVolume();
        Gap();
        Label("WALK-IN VENUES");
        autoJoin = settings.AutoJoin;
        if (ImGui.Checkbox("Watch for venues when I enter a house", ref watchVenues)) Remember();
        using (ImRaii.Disabled(!watchVenues))
        using (ImRaii.PushColor(ImGuiCol.CheckMark, P.Listen))
            if (ImGui.Checkbox("Join automatically, without asking", ref autoJoin)) Remember();
        Indented(Muted, "When on, the house you enter is checked against the venue directory. Only houses whose host chose to announce are found.");
    }

    private void ListenVolume()
    {
        bool muted = session.ListenMuted;
        if (MutableSlider("Volume", ref volume, ref muted, FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute, P.Listen)) session.Volume = volume;
        session.ListenMuted = muted;
    }

    private void NowPlayingCard(string caption, Invite? venue, string? elapsed)
    {
        float height = S(74);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(ImGui.GetContentRegionAvail().X, height);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(P.NowCard), R(6));
        draw.AddRect(min, max, U32(P.NowEdge), R(6));
        var dot = new Vector2(min.X + S(22), min.Y + height / 2);
        float pulse = Pulse;
        draw.AddCircleFilled(dot, S(6) + S(5) * pulse, U32(P.Listen, 0.25f * pulse));
        draw.AddCircleFilled(dot, S(6), U32(P.Listen));
        float x = dot.X + S(18), line = ImGui.GetTextLineHeight(), top = min.Y + (height - line * 3 - S(4)) / 2;
        draw.AddText(new Vector2(x, top), U32(P.ListenBadgeInk), caption);
        string name = string.IsNullOrEmpty(venue?.Venue) ? "Live venue" : venue.Venue;
        draw.AddText(new Vector2(x, top + line + S(2)), U32(Ink), name);
        if (!string.IsNullOrEmpty(venue?.HostName)) draw.AddText(new Vector2(x, top + line * 2 + S(4)), U32(Muted), "Hosted by " + venue.HostName);
        if (elapsed != null)
        {
            using var font = mono.Push();
            var size = ImGui.CalcTextSize(elapsed);
            draw.AddText(new Vector2(max.X - S(14) - size.X, min.Y + (height - size.Y) / 2), U32(Body), elapsed);
        }
        ImGui.Dummy(new Vector2(max.X - min.X, height));
    }

    private static void MicrophoneOff()
    {
        using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(Muted, FontAwesomeIcon.MicrophoneSlash.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(Muted, "Your microphone stays off while listening.");
    }

    // ── Widgets ──────────────────────────────────────────────────────────────

    private bool BroadcastButton(string label, string? trailing, bool live, bool enabled)
    {
        float width = ImGui.GetContentRegionAvail().X, height = S(56);
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        bool clicked;
        using (ImRaii.Disabled(!enabled)) clicked = ImGui.InvisibleButton("##broadcast", new Vector2(width, height));
        bool hovered = enabled && ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        float pulse = Pulse, round = R(5), fade = enabled ? 1 : 0.5f;
        if (live)
        {
            for (int ring = 3; ring >= 1; ring--)
            {
                float grow = S(ring * 3) * (0.4f + 0.6f * pulse);
                draw.AddRectFilled(min - new Vector2(grow), max + new Vector2(grow), U32(P.Host, (0.10f + 0.12f * pulse) / ring), round + grow);
            }
            draw.AddRectFilled(min, max, U32(hovered ? P.LiveHover : P.Live), round);
            draw.AddRect(min, max, U32(P.LiveEdge), round);
        }
        else
        {
            draw.AddRectFilled(min, max, U32(hovered ? P.HostIdleHover : P.HostIdle, fade), round);
            draw.AddRect(min, max, U32(P.Host, enabled ? 1 : 0.4f), round);
        }
        var text = live ? P.OnHost : P.HostLabel;
        var labelSize = ImGui.CalcTextSize(label);
        Vector2 trailingSize = Vector2.Zero;
        if (trailing != null) using (mono.Push()) trailingSize = ImGui.CalcTextSize(trailing);
        float dotSpace = S(20), gap = trailing != null ? S(10) : 0;
        float x = min.X + (width - (dotSpace + labelSize.X + gap + trailingSize.X)) / 2, mid = min.Y + height / 2;
        draw.AddCircleFilled(new Vector2(x + S(5), mid), S(5), live ? U32(P.OnHost, 0.45f + 0.55f * pulse) : U32(P.Host, fade));
        draw.AddText(new Vector2(x + dotSpace, mid - labelSize.Y / 2), U32(text, fade), label);
        if (trailing != null)
            using (mono.Push()) draw.AddText(new Vector2(x + dotSpace + labelSize.X + gap, mid - trailingSize.Y / 2), U32(P.OnHost, 0.9f), trailing);
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked && enabled;
    }

    private static float RowLabelEnd => S(56) + ImGui.GetStyle().WindowPadding.X;

    private static void Meter(string label, float peak)
    {
        const int segments = 16;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Muted, label);
        ImGui.SameLine(RowLabelEnd);
        float width = ImGui.GetContentRegionAvail().X, height = S(8), gap = S(2);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - height) / 2);
        float cell = (width - gap * (segments - 1)) / segments;
        int lit = (int)MathF.Round(Math.Clamp(peak, 0, 1) * segments);
        var draw = ImGui.GetWindowDrawList();
        for (int i = 0; i < segments; i++)
        {
            var color = i >= lit ? MeterOff : i < 10 ? MeterGreen : i < 14 ? MeterAmber : MeterRed;
            var a = origin + new Vector2(i * (cell + gap), 0);
            draw.AddRectFilled(a, a + new Vector2(cell, height), U32(color), R(1));
        }
        ImGui.Dummy(new Vector2(width, ImGui.GetFrameHeight()));
    }

    /// <summary>A labelled 0–100% slider with a mute toggle at its end. Returns true when the level moved.</summary>
    private bool MutableSlider(string label, ref float value, ref bool muted, FontAwesomeIcon on, FontAwesomeIcon off, Vector4? accent)
    {
        using var id = ImRaii.PushId(label);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(muted ? Error : Muted, label);
        ImGui.SameLine(RowLabelEnd);
        float toggle = ImGui.GetFrameHeight();
        int percent = (int)MathF.Round(value * 100);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - toggle - ImGui.GetStyle().ItemSpacing.X);
        bool changed;
        using (ImRaii.Disabled(muted))
        using (ImRaii.PushColor(ImGuiCol.SliderGrab, accent ?? P.Grab).Push(ImGuiCol.SliderGrabActive, accent ?? Ink))
            changed = ImGui.SliderInt("##level", ref percent, 0, 100, muted ? "Muted" : "%d%%");
        if (changed) value = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit()) Remember();
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, muted ? P.MutedBadge : Raised).Push(ImGuiCol.Text, muted ? Error : Ink))
            if (IconButton(muted ? off : on, "##mute", toggle)) muted = !muted;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(muted ? $"Unmute {label.ToLowerInvariant()}" : $"Mute {label.ToLowerInvariant()}");
        return changed;
    }

    private static bool ColorButton(string label, Vector4 color, Vector4 hover, Vector4 ink, float width)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, color).Push(ImGuiCol.ButtonHovered, hover).Push(ImGuiCol.ButtonActive, hover).Push(ImGuiCol.Text, ink);
        return ImGui.Button(label, new Vector2(width, S(44)));
    }

    private static bool LinkButton(string text)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero).Push(ImGuiCol.ButtonHovered, Field).Push(ImGuiCol.ButtonActive, Field).Push(ImGuiCol.Text, Muted);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(S(4), S(3)));
        return ImGui.Button(text);
    }

    private static bool IconButton(FontAwesomeIcon icon, string id, float size)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.Button(icon.ToIconString() + id, new Vector2(size, size));
    }

    private bool Copied(string what) => copiedWhat == what && ImGui.GetTime() - copiedAt < 1.5;
    private void Copy(string what, string text) { ImGui.SetClipboardText(text); copiedWhat = what; copiedAt = ImGui.GetTime(); }
    private static float CopyWidth() =>
        ImGui.CalcTextSize("Copied").X + ImGui.CalcTextSize(FontAwesomeIcon.Copy.ToIconString()).X + S(34);

    private bool CopyButton(string what, float width)
    {
        string label = Copied(what) ? "Copied" : "Copy";
        var min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button("##copy-" + what, new Vector2(width, ImGui.GetFrameHeight()));
        var draw = ImGui.GetWindowDrawList();
        string glyph = FontAwesomeIcon.Copy.ToIconString();
        Vector2 glyphSize;
        using (ImRaii.PushFont(UiBuilder.IconFont)) glyphSize = ImGui.CalcTextSize(glyph);
        var textSize = ImGui.CalcTextSize(label);
        float x = min.X + (width - glyphSize.X - S(6) - textSize.X) / 2, mid = min.Y + ImGui.GetFrameHeight() / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont)) draw.AddText(new Vector2(x, mid - glyphSize.Y / 2), U32(Ink), glyph);
        draw.AddText(new Vector2(x + glyphSize.X + S(6), mid - textSize.Y / 2), U32(Ink), label);
        return clicked;
    }

    private void DevicePicker(string id, ref string selected, bool microphoneOnly, float width)
    {
        string selectedId = selected;
        var current = devices.FirstOrDefault(d => d.Id == selectedId);
        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo(id, current?.Name ?? (microphoneOnly ? "Choose a microphone" : "Choose an audio source"));
        if (!combo) return;
        foreach (var device in devices.Where(d => !microphoneOnly || !d.IsOutput))
        {
            string text = (device.IsOutput ? "Output: " : "Input: ") + device.Name;
            if (ImGui.Selectable(text + "##" + device.Id, device.Id == selected)) { selected = device.Id; Remember(); }
        }
    }

    internal static void Icon(ImDrawListPtr draw, FontAwesomeIcon icon, Vector2 center, uint color)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        string glyph = icon.ToIconString();
        draw.AddText(center - ImGui.CalcTextSize(glyph) / 2, color, glyph);
    }

    private static void Label(string text) => ImGui.TextColored(Muted, text);
    private static void Gap(float height = 2) => ImGui.Dummy(new Vector2(0, S(height)));

    private static void Footer(string text)
    {
        Gap(4);
        ImGui.Separator();
        Wrapped(Muted, text);
    }

    internal static void Wrapped(Vector4 color, string text)
    {
        using var wrap = ImRaii.TextWrapPos(0);
        ImGui.TextColored(color, text);
    }

    private static void Indented(Vector4 color, string text)
    {
        using var indent = ImRaii.PushIndent(S(4), false);
        Wrapped(color, text);
    }

    private static void Centered(Vector4 color, string text)
    {
        float width = ImGui.GetContentRegionAvail().X, size = ImGui.CalcTextSize(text).X;
        if (size >= width) { Wrapped(color, text); return; }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - size) / 2);
        ImGui.TextColored(color, text);
    }

    private static string Mbps(int bitsPerSecond) => bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1_000_000.0:0.#} Mbps" : $"{bitsPerSecond / 1000} kbps";
    private static string FormatTime(TimeSpan span) => $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
