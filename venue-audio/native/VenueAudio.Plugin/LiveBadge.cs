using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using VenueAudio.Core;
using static VenueAudio.Theme;

namespace VenueAudio;

/// <summary>A small always-visible ticket while broadcasting, so the host can see they're on air with the main window closed.</summary>
public sealed class LiveBadge : Window
{
    private readonly Session session;
    private readonly Settings settings;
    private readonly IFontHandle mono;
    private readonly Action openControls;
    private (int, int) pushed;

    public LiveBadge(Session session, Settings settings, IFontHandle mono, Action openControls)
        : base("House Lights live###VenueAudioLive", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.session = session; this.settings = settings; this.mono = mono; this.openControls = openControls;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() => settings.LiveBadge && session.Broadcasting;

    private bool AllMuted => session.MusicMuted && (settings.MicrophoneId.Length == 0 || session.MicMuted);

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, S(60)), ImGuiCond.FirstUseEver, new Vector2(.5f, 0));
        pushed = Theme.Push(settings.Style);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, R(6));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Current.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, AllMuted ? Current.WarnEdge : Current.Host);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2); ImGui.PopStyleVar(4);
        Theme.Pop(pushed);
    }

    public override void Draw()
    {
        var p = Current;
        bool muted = AllMuted;
        float height = S(44), line = ImGui.GetTextLineHeight();
        var draw = ImGui.GetWindowDrawList();

        // Bulb strip over LIVE and the timer.
        string time = VenueWindow.FormatTime(session.LiveFor), state = muted ? "MUTED" : "LIVE";
        Vector2 timeSize;
        using (mono.Push()) timeSize = ImGui.CalcTextSize(time);
        float strip = S(4) * 8 + S(4) * 7, text = ImGui.CalcTextSize(state).X + S(8) + timeSize.X;
        float block = Math.Max(strip, text) + S(24);
        var min = ImGui.GetCursorScreenPos();
        float top = min.Y + (height - S(4) - S(3) - line) / 2;
        MarqueeDraw.BulbRow(draw, new Vector2(min.X + S(12), top), strip, 8, S(4), muted ? Bulbs.Dimmed : Bulbs.Chasing,
            muted ? p.HostDim : p.Host, null, 0, false);
        float textY = top + S(4) + S(3);
        draw.AddText(new Vector2(min.X + S(12), textY), U32(muted ? Warn : Ink), state);
        using (mono.Push())
            draw.AddText(new Vector2(min.X + S(12) + ImGui.CalcTextSize(state).X + S(8), textY + (line - timeSize.Y) / 2), U32(Ink), time);
        ImGui.Dummy(new Vector2(block, height));

        ImGui.SameLine();
        string count = session.Listeners.ToString();
        var at = ImGui.GetCursorScreenPos();
        float listeners = S(10) + S(14) + S(6) + ImGui.CalcTextSize(count).X + S(10);
        VenueWindow.Icon(draw, FontAwesomeIcon.Headphones, new Vector2(at.X + S(17), at.Y + height / 2), U32(Ink), small: true);
        draw.AddText(new Vector2(at.X + S(30), at.Y + (height - line) / 2), U32(Ink), count);
        ImGui.Dummy(new Vector2(listeners, height));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Listening now");

        if (session.Code.Length > 0 && !settings.HideCodes)
        {
            ImGui.SameLine();
            at = ImGui.GetCursorScreenPos();
            string code = VenueCodes.Display(session.Code);
            using (mono.Push())
            {
                var size = ImGui.CalcTextSize(code);
                draw.AddText(new Vector2(at.X + S(4), at.Y + (height - size.Y) / 2), U32(Muted), code);
                ImGui.Dummy(new Vector2(size.X + S(16), height));
            }
        }

        // The ticket's perforation; the notches are left out because the badge floats over the game itself.
        ImGui.SameLine();
        at = ImGui.GetCursorScreenPos();
        MarqueeDraw.Dashed(draw, new Vector2(at.X, at.Y + S(6)), new Vector2(at.X, at.Y + height - S(6)), U32(muted ? p.WarnEdge : p.HostEdge));
        ImGui.Dummy(new Vector2(S(8), height));

        float button = S(30);
        ImGui.SameLine();
        ImGui.SetCursorPosY((height - button) / 2);
        bool musicMuted = session.MusicMuted;
        using (ImRaii.PushColor(ImGuiCol.Button, Raised).Push(ImGuiCol.Text, musicMuted ? Error : Ink).Push(ImGuiCol.Border, Error))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, musicMuted ? 1f : 0f))
            if (VenueWindow.IconButton(musicMuted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, "##badge-mute", button)) session.MusicMuted = !musicMuted;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(musicMuted ? "Unmute music" : "Mute music");
        ImGui.SameLine(0, S(4));
        if (VenueWindow.IconButton(FontAwesomeIcon.SlidersH, "##badge-open", button)) openControls();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open House Lights");
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(S(7), button));
    }
}
