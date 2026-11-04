using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using static VenueAudio.Theme;

namespace VenueAudio;

/// <summary>The short walkthrough shown on first open, and again from the title bar's question mark.</summary>
public sealed class GuideWindow : Window
{
    private enum Accent { Plain, Host, Listen }
    private sealed record Page(string Title, FontAwesomeIcon Icon, Accent Accent, string[] Steps, string[] Tips);

    public const string AutoDiscover = "Walking into a house with an announced broadcast automatically asks you to join the venue's listening party.";
    public const string AutoJoin = "Skips the pop-up and joins the venue's listening party as soon as you enter.";

    private static readonly Page[] Pages =
    [
        new("Welcome to Venue Audio", FontAwesomeIcon.Music, Accent.Plain,
        [
            "Hosts stream their venue's music live to everyone listening.",
            "Pick Host if you run the music, or Listener if you're tuning in.",
            "Switch role, top right, takes you back to that choice anytime.",
        ], []),
        new("How to host", FontAwesomeIcon.BroadcastTower, Accent.Host,
        [
            "Choose the audio source your music plays through.",
            "Press Start broadcasting. Going live can take up to a minute.",
            "Share your six-character code with guests. You get a new one each time you go live.",
        ],
        [
            "Picking your speakers or headset sends everything you hear, game sounds included.",
            "To send only music, give it a device of its own: a virtual cable (VB-Audio Virtual Cable), a virtual mixer (VoiceMeeter), or a stream mix from an app like Elgato Wave Link or SteelSeries Sonar. Windows' Volume mixer can send just your music player to that device.",
            "Pick a microphone only if you want your voice in the stream and your mixer doesn't already carry it.",
            "Announce broadcast: stand inside your house before going live. Anyone with Venue Audio who walks in is offered the music, no code needed.",
            "Every guest is a separate copy of the stream on your upload. High quality is about 180 kbps per guest, so 50 guests need about 9 Mbps.",
            "The eye button hides your code while you stream. Set in-game booth music to Off.",
        ]),
        new("How to listen", FontAwesomeIcon.Headphones, Accent.Listen,
        [
            "Type or paste the host's code.",
            "Press Join venue.",
        ],
        [
            "Auto Discover Venues: " + AutoDiscover,
            "Auto Join Venues: " + AutoJoin,
            "If you joined by walking in, leaving the house disconnects you.",
        ]),
        new("Good to know", FontAwesomeIcon.Lightbulb, Accent.Plain, [],
        [
            "While you're live, a small badge floats on screen. Drag it anywhere. Its buttons mute your music or open these controls.",
            "The palette button in the title bar changes the style. Right-click goes back one.",
            "The question mark reopens this guide. /venueaudio opens the window.",
        ]),
    ];

    private const float ContentWidth = 400;

    private readonly Settings settings;
    private readonly Action save;
    private readonly PageMotion motion = new(0);
    private (int, int) pushed;

    public GuideWindow(Settings settings, Action save)
        : base("Venue Audio guide###VenueAudioGuide", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.settings = settings; this.save = save;
    }

    public void Show(Role role)
    {
        motion.Jump(role switch { Role.Host => 1, Role.Listener => 2, _ => 0 });
        IsOpen = true; BringToFront();
    }

    public override void PreDraw()
    {
        pushed = Theme.Push(settings.Style);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Appearing, new Vector2(.5f, .5f));
        ImGui.SetNextWindowSize(new Vector2(S(ContentWidth) + ImGui.GetStyle().WindowPadding.X * 2, motion.Height(S(320), viewport.Size.Y * .9f)));
    }
    public override void PostDraw() => Theme.Pop(pushed);

    public override void OnClose()
    {
        if (settings.SeenGuide) return;
        settings.SeenGuide = true; save();
    }

    public override void Draw()
    {
        var style = ImGui.GetStyle();
        float buttonRow = ImGui.GetFrameHeight();
        float footerY = ImGui.GetWindowHeight() - style.WindowPadding.Y - buttonRow;
        var windowPos = ImGui.GetWindowPos();

        // Clipped above the pinned footer, so a page bigger than the window mid-resize never overlaps the buttons.
        ImGui.PushClipRect(windowPos, windowPos + new Vector2(ImGui.GetWindowWidth(), footerY - S(6)), true);
        motion.Begin();
        DrawPage(Pages[motion.Page]);
        motion.End(S(14) + buttonRow + style.WindowPadding.Y);
        ImGui.PopClipRect();

        ImGui.SetCursorPosY(footerY);
        DrawFooter(buttonRow);
    }

    private static void DrawPage(Page page)
    {
        var p = Current;
        var (badge, ink) = page.Accent switch
        {
            Accent.Host => (p.HostBadge, p.HostBadgeInk),
            Accent.Listen => (p.ListenBadge, p.ListenIcon),
            _ => (Raised, Ink),
        };
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var center = min + new Vector2(S(20), S(20));
        draw.AddCircleFilled(center, S(20), U32(badge));
        VenueWindow.Icon(draw, page.Icon, center, U32(ink));
        ImGui.SetCursorScreenPos(min + new Vector2(S(52), S(20) - ImGui.GetTextLineHeight() / 2));
        ImGui.Text(page.Title);
        ImGui.SetCursorScreenPos(min + new Vector2(0, S(52)));
        ImGui.Dummy(new Vector2(S(ContentWidth), 0));

        float wrap = ImGui.GetCursorPosX() + S(ContentWidth), column = ImGui.GetCursorPosX() + S(22);
        using var wrapping = ImRaii.TextWrapPos(wrap);
        for (int i = 0; i < page.Steps.Length; i++) Line($"{i + 1}.", page.Accent == Accent.Plain ? Muted : ink, page.Steps[i], Ink, column);
        if (page.Steps.Length > 0 && page.Tips.Length > 0) ImGui.Dummy(new Vector2(0, S(4)));
        foreach (var tip in page.Tips) Line("•", Muted, tip, Body, column);
    }

    private void DrawFooter(float height)
    {
        var p = Current;
        float button = S(92), spacing = ImGui.GetStyle().ItemSpacing.X;
        var dots = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        int index = motion.Page;
        for (int i = 0; i < Pages.Length; i++)
            draw.AddCircleFilled(dots + new Vector2(S(4) + i * S(14), height / 2), S(3.5f), U32(i == index ? Ink : Raised));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(ContentWidth) - button * 2 - spacing);
        using (ImRaii.Disabled(index == 0))
            if (ImGui.Button("Back", new Vector2(button, 0))) motion.Go(index - 1);
        ImGui.SameLine();
        bool last = index == Pages.Length - 1;
        using (ImRaii.PushColor(ImGuiCol.Button, p.Host).Push(ImGuiCol.ButtonHovered, p.LiveHover).Push(ImGuiCol.ButtonActive, p.LiveHover).Push(ImGuiCol.Text, p.OnHost))
            if (ImGui.Button(last ? "Done" : "Next", new Vector2(button, 0)))
            {
                if (last) IsOpen = false;
                else motion.Go(index + 1);
            }
    }

    private static void Line(string mark, Vector4 markColor, string text, Vector4 color, float column)
    {
        ImGui.TextColored(markColor, mark);
        ImGui.SameLine(column);
        ImGui.TextColored(color, text);
    }
}
