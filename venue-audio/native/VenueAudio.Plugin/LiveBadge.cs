using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VenueAudio.Core;
using static VenueAudio.Theme;

namespace VenueAudio;

/// <summary>A small always-visible strip while broadcasting, so the host can see they're on air with the main window closed.</summary>
public sealed class LiveBadge : Window
{
    private readonly Session session;
    private readonly Settings settings;
    private readonly IFontHandle mono;
    private readonly Action openControls;
    private (int, int) pushed;

    public LiveBadge(Session session, Settings settings, IFontHandle mono, Action openControls)
        : base("Venue Audio live###VenueAudioLive", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.session = session; this.settings = settings; this.mono = mono; this.openControls = openControls;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() => settings.LiveBadge && session.Broadcasting;

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, S(60)), ImGuiCond.FirstUseEver, new Vector2(.5f, 0));
        pushed = Theme.Push(settings.Style);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(S(10), S(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, R(8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(S(8), S(4)));
        ImGui.PushStyleColor(ImGuiCol.Border, Current.Host with { W = .6f });
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(); ImGui.PopStyleVar(3);
        Theme.Pop(pushed);
    }

    public override void Draw()
    {
        var p = Current;
        float height = ImGui.GetFrameHeight(), pulse = Pulse;
        var dot = ImGui.GetCursorScreenPos() + new Vector2(S(6), height / 2);
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(dot, S(5) + S(4) * pulse, U32(p.Host, .3f * pulse));
        draw.AddCircleFilled(dot, S(5), U32(p.Host));
        ImGui.Dummy(new Vector2(S(12), height));

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(p.HostBadgeInk, "LIVE");
        ImGui.SameLine();
        using (mono.Push()) ImGui.TextColored(Body, VenueWindow.FormatTime(session.LiveFor));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(Muted, FontAwesomeIcon.Headphones.ToIconString());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Listening now");
        ImGui.SameLine(0, S(4));
        ImGui.Text(session.Listeners.ToString());

        if (session.Code.Length > 0 && !settings.HideCodes)
        {
            ImGui.SameLine();
            using (mono.Push()) ImGui.TextColored(Ink, VenueCodes.Display(session.Code));
        }

        ImGui.SameLine();
        bool muted = session.MusicMuted;
        using (ImRaii.PushColor(ImGuiCol.Button, muted ? p.MutedBadge : Raised).Push(ImGuiCol.Text, muted ? Error : Ink))
            if (VenueWindow.IconButton(muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, "##badge-mute", height)) session.MusicMuted = !muted;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(muted ? "Unmute music" : "Mute music");

        ImGui.SameLine();
        if (VenueWindow.IconButton(FontAwesomeIcon.SlidersH, "##badge-open", height)) openControls();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open Venue Audio");
    }
}
