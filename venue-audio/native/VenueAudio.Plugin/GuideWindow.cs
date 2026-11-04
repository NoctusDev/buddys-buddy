using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using static VenueAudio.Theme;

namespace VenueAudio;

/// <summary>The short walkthrough shown on first open, and again from the title bar's question mark.</summary>
public sealed class GuideWindow : Window
{
    private enum Accent { Plain, Host, Listen }
    private sealed record Page(string Title, string Subtitle, FontAwesomeIcon Icon, Accent Accent, string[] Steps, string[] Tips);

    public const string AutoDiscover = "Walking into a house with an announced broadcast automatically asks you to join the venue's listening party.";
    public const string AutoJoin = "Skips the pop-up and joins the venue's listening party as soon as you enter.";

    private static readonly Page[] Pages =
    [
        new("WELCOME TO HOUSE LIGHTS", "Live music for your venue", FontAwesomeIcon.Music, Accent.Plain,
        [
            "Hosts stream their venue's music live to everyone listening.",
            "Pick Host if you run the music, or Listener if you're tuning in.",
            "Switch role, top right, takes you back to that choice anytime.",
        ], []),
        new("HOW TO HOST", "Three steps to go live", FontAwesomeIcon.BroadcastTower, Accent.Host,
        [
            "Choose the audio source your music plays through.",
            "Press Start broadcasting. Going live can take up to a minute.",
            "Share your six-character code with guests. You get a new one each time you go live.",
        ],
        [
            "Picking your speakers or headset sends everything you hear, game sounds included.",
            "To send only music, give it a device of its own: a virtual cable (VB-Audio Virtual Cable), a virtual mixer (VoiceMeeter), or a stream mix from an app like Elgato Wave Link or SteelSeries Sonar. Windows' Volume mixer can send just your music player to that device.",
            "Pick a microphone only if you want your voice in the stream and your mixer doesn't already carry it.",
            "Announce broadcast: stand inside your house before going live. Anyone with House Lights who walks in is offered the music, no code needed.",
            "Every guest is a separate copy of the stream on your upload. High quality is about 180 kbps per guest, so 50 guests need about 9 Mbps.",
            "The eye button hides your code while you stream. Set in-game booth music to Off.",
        ]),
        new("HOW TO LISTEN", "Two steps to tune in", FontAwesomeIcon.Headphones, Accent.Listen,
        [
            "Type or paste the host's code.",
            "Press Join venue.",
        ],
        [
            "Auto Discover Venues: " + AutoDiscover,
            "Auto Join Venues: " + AutoJoin,
            "If you joined by walking in, leaving the house disconnects you.",
        ]),
        new("GOOD TO KNOW", "Small things that help", FontAwesomeIcon.Lightbulb, Accent.Plain, [],
        [
            "While you're live, a small badge floats on screen. Drag it anywhere. Its buttons mute your music or open these controls.",
            "The palette button in the title bar changes the style. Right-click goes back one.",
            "The question mark reopens this guide. /houselights opens the window.",
        ]),
    ];

    private const float ContentWidth = 400;

    private readonly Settings settings;
    private readonly Action save;
    private readonly IFontHandle mono;
    private readonly PageMotion motion = new(0);
    private (int, int) pushed;

    public GuideWindow(Settings settings, Action save, IFontHandle mono)
        : base("House Lights guide###VenueAudioGuide", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.settings = settings; this.save = save; this.mono = mono;
    }

    public void Show(Role role)
    {
        motion.Jump(role switch { Role.Host => 1, Role.Listener => 2, _ => 0 });
        IsOpen = true; BringToFront();
    }

    public override void PreDraw()
    {
        pushed = Theme.Push(settings.Style);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(S(16)));
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2, ImGuiCond.Appearing, new Vector2(.5f, .5f));
        ImGui.SetNextWindowSize(new Vector2(S(ContentWidth) + S(16) * 2, motion.Height(S(320), viewport.Size.Y * .9f)));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop(pushed);
    }

    public override void OnClose()
    {
        if (settings.SeenGuide) return;
        settings.SeenGuide = true; save();
    }

    private static (Vector4 Accent, Vector4 Badge, Vector4 BadgeInk, Vector4 Edge, Vector4 Bulbs) Look(Page page)
    {
        var p = Current;
        return page.Accent switch
        {
            Accent.Host => (p.Host, p.Host, p.OnHost, p.HostEdge, p.Host),
            Accent.Listen => (p.Listen, p.Listen, p.OnListen, p.ListenEdge, p.Listen),
            _ => (Ink, Raised, Ink, Edge, Ink with { W = .6f }),
        };
    }

    public override void Draw()
    {
        var style = ImGui.GetStyle();
        float buttonRow = S(30), rule = S(10);
        float footerY = ImGui.GetWindowHeight() - style.WindowPadding.Y - buttonRow;
        var windowPos = ImGui.GetWindowPos();

        // Clipped above the pinned footer, so a page bigger than the window mid-resize never overlaps the buttons.
        ImGui.PushClipRect(windowPos, windowPos + new Vector2(ImGui.GetWindowWidth(), footerY - rule - S(2)), true);
        motion.Begin();
        DrawPage(Pages[motion.Page]);
        motion.End(S(6) + rule * 2 + buttonRow + style.WindowPadding.Y);
        ImGui.PopClipRect();

        float lineY = windowPos.Y + footerY - rule;
        ImGui.GetWindowDrawList().AddLine(new Vector2(windowPos.X, lineY), new Vector2(windowPos.X + ImGui.GetWindowWidth(), lineY), U32(Edge));
        ImGui.SetCursorPosY(footerY);
        DrawFooter(buttonRow);
    }

    private void DrawPage(Page page)
    {
        var look = Look(page);
        var draw = ImGui.GetWindowDrawList();
        float width = S(ContentWidth), padX = S(12), padY = S(10), gap = S(10), bulb = S(6), badge = S(40), line = ImGui.GetTextLineHeight();

        // The page header is a small marquee: bulbs above and below the badge and title.
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, padY * 2 + bulb * 2 + gap * 2 + badge);
        draw.AddRectFilled(min, max, U32(Current.Surface), R(4));
        draw.AddRect(min, max, U32(look.Edge), R(4));
        MarqueeDraw.BulbRow(draw, min + new Vector2(padX, padY), width - padX * 2, 18, bulb, Bulbs.Chasing, look.Bulbs, null, 0, false);
        MarqueeDraw.BulbRow(draw, new Vector2(min.X + padX, max.Y - padY - bulb), width - padX * 2, 18, bulb, Bulbs.Chasing, look.Bulbs, null, 0, true);
        var center = new Vector2(min.X + padX + badge / 2, (min.Y + max.Y) / 2);
        draw.AddCircleFilled(center, badge / 2, U32(look.Badge));
        VenueWindow.Icon(draw, page.Icon, center, U32(look.BadgeInk));
        float x = center.X + badge / 2 + S(12);
        draw.AddText(new Vector2(x, center.Y - line), U32(Ink), page.Title);
        draw.AddText(new Vector2(x, center.Y), U32(Muted), page.Subtitle);
        MarqueeDraw.Advance(min, max);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(6));

        float left = ImGui.GetCursorPosX();
        using var wrapping = ImRaii.TextWrapPos(left + width);
        var boxBorder = page.Accent == Accent.Plain ? Muted : look.Accent;
        for (int i = 0; i < page.Steps.Length; i++)
        {
            var at = ImGui.GetCursorScreenPos();
            var box = new Vector2(S(28), S(20));
            draw.AddRect(at, at + box, U32(boxBorder), R(4));
            string number = (i + 1).ToString("00");
            using (mono.Push())
            {
                var size = ImGui.CalcTextSize(number);
                draw.AddText(at + (box - size) / 2, U32(Ink), number);
            }
            ImGui.Dummy(box);
            ImGui.SameLine(left + S(38));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (box.Y - line) / 2);
            ImGui.TextColored(Ink, page.Steps[i]);
        }
        if (page.Steps.Length > 0 && page.Tips.Length > 0)
        {
            var at = ImGui.GetCursorScreenPos() + new Vector2(0, S(2));
            MarqueeDraw.Dashed(draw, at, at + new Vector2(width, 0), U32(Edge));
            ImGui.Dummy(new Vector2(width, S(4)));
        }
        foreach (var tip in page.Tips)
        {
            var at = ImGui.GetCursorScreenPos();
            draw.AddCircleFilled(at + new Vector2(S(6.5f), line / 2), S(2.5f), U32(Muted));
            ImGui.Dummy(new Vector2(S(13), line));
            ImGui.SameLine(left + S(23));
            ImGui.TextColored(Body, tip);
        }
    }

    private void DrawFooter(float height)
    {
        var p = Current;
        int index = motion.Page;
        var look = Look(Pages[index]);
        float back = ImGui.CalcTextSize("Back").X + S(28), next = ImGui.CalcTextSize("Next").X + S(28), spacing = S(6);
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        float x = at.X, mid = at.Y + height / 2;
        for (int i = 0; i < Pages.Length; i++)
        {
            float w = i == index ? S(18) : S(6);
            draw.AddRectFilled(new Vector2(x, mid - S(3)), new Vector2(x + w, mid + S(3)), U32(i == index ? look.Accent : Raised), S(3));
            x += w + S(6);
        }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(ContentWidth) - back - next - spacing);
        using (ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, .4f))
        using (ImRaii.Disabled(index == 0))
            if (ImGui.Button("Back", new Vector2(back, height))) motion.Go(index - 1);
        ImGui.SameLine(0, spacing);
        bool last = index == Pages.Length - 1;
        using (ImRaii.PushColor(ImGuiCol.Button, last ? p.Host : Raised).Push(ImGuiCol.ButtonHovered, last ? p.LiveHover : RaisedHover)
                   .Push(ImGuiCol.ButtonActive, last ? p.LiveHover : RaisedHover).Push(ImGuiCol.Text, last ? p.OnHost : Ink))
            if (ImGui.Button(last ? "Done" : "Next", new Vector2(next, height)))
            {
                if (last) IsOpen = false;
                else motion.Go(index + 1);
            }
    }
}
