using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace VenueAudio;

/// <summary>
/// One visual style. Only the core colours are chosen by hand; every hover, badge, card and glow shade
/// is mixed from them, so a style stays coherent without tuning thirty values.
/// </summary>
public sealed class Palette
{
    public string Name { get; }
    public float Round { get; }
    /// <summary>False for the style that borrows the player's own Dalamud colours instead of pushing ours.</summary>
    public bool PushColors { get; }
    public Vector4 Ground, Surface, TileHover, Field, FieldHover, FieldActive, Raised, RaisedHover, RaisedActive,
        Edge, Border, Ink, Body, Muted, Grab, Title,
        Host, LiveHover, OnHost, Listen, ListenButtonHover, OnListen, Error, Warn,
        HostTint, HostEdge, HostGlowOuter, HostGlowInner, HostDim, ListenTint, ListenEdge, WarnEdge, ErrorEdge, BulbOff;

    public Palette(string name, uint ground, uint surface, uint field, uint raised, uint edge, uint ink, uint muted, uint title,
        uint host, uint onHost, uint listen, uint error, uint warn, float round, bool pushColors = true)
        : this(name, Theme.Rgb(ground), Theme.Rgb(surface), Theme.Rgb(field), Theme.Rgb(raised), Theme.Rgb(edge), Theme.Rgb(ink),
            Theme.Rgb(muted), Theme.Rgb(title), Theme.Rgb(host), Theme.Rgb(onHost), Theme.Rgb(listen),
            Theme.Rgb(error), Theme.Rgb(warn), round, pushColors) { }

    public Palette(string name, Vector4 ground, Vector4 surface, Vector4 field, Vector4 raised, Vector4 edge, Vector4 ink, Vector4 muted,
        Vector4 title, Vector4 host, Vector4 onHost, Vector4 listen, Vector4 error, Vector4 warn, float round, bool pushColors)
    {
        Name = name; Round = round; PushColors = pushColors;
        Ground = ground; Surface = surface; Field = field; Raised = raised; Edge = edge; Ink = ink; Muted = muted; Title = title;
        // Every listener accent is light, so text on it uses the window's own dark ground.
        Host = host; OnHost = onHost; Listen = listen; OnListen = ground; Error = error; Warn = warn;
        HostTint = host with { W = .12f }; HostEdge = host with { W = .4f }; HostDim = host with { W = .3f };
        HostGlowOuter = host with { W = .12f }; HostGlowInner = host with { W = .25f };
        ListenTint = listen with { W = .11f }; ListenEdge = listen with { W = .4f };
        WarnEdge = warn with { W = .5f }; ErrorEdge = error with { W = .5f }; BulbOff = raised;
        Body = Mix(muted, ink, .6f);
        TileHover = Mix(Mix(ground, surface, 1.4f), ink, .05f);
        FieldHover = Mix(field, ink, .05f); FieldActive = Mix(field, ink, .09f);
        RaisedHover = Mix(raised, ink, .1f); RaisedActive = Mix(raised, ink, .16f);
        Border = Mix(ground, edge, .6f);
        Grab = Mix(muted, raised, .3f);
        LiveHover = Mix(host, ink, .08f);
        ListenButtonHover = Mix(listen, ink, .15f);
    }

    private static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
}

internal static class Theme
{
    public static readonly Palette[] Styles =
    [
        // The original look: crimson broadcast, blue listening, near-black panels.
        new("Velvet", 0x161617, 0x1d1d20, 0x2a2a2c, 0x3d3d40, 0x3a3a3e, 0xf4f2ef, 0xa9a6a1, 0x5e2222,
            0xd94141, 0xffffff, 0x4ea1d9, 0xff8a8a, 0xe0b84a, 1f),
        // Club lighting: deep navy, hot pink on air, cyan for guests, softer corners.
        new("Midnight", 0x0d0f1c, 0x151933, 0x1d2240, 0x2a3058, 0x323a66, 0xeef0ff, 0x9aa0c8, 0x3b1a5c,
            0xff4fa3, 0xffffff, 0x3ad6f0, 0xff7a9a, 0xffd166, 1.6f),
        // A lounge: warm black, gold on air with dark lettering, jade for guests, crisp corners.
        new("Gilded", 0x14110c, 0x1d1912, 0x28221a, 0x3a3224, 0x4a3f2b, 0xf6eedd, 0xb5a88e, 0x4a3512,
            0xd6a84a, 0x1a1408, 0x6fb7a8, 0xff8f7a, 0xe8c46a, .5f),
        // Pastel night: violet on air, mint for guests, rounded like pills.
        new("Moonstone", 0x17151d, 0x1f1c28, 0x2a2635, 0x3a3549, 0x413b52, 0xf3f0fa, 0xaaa3bd, 0x3d2f5e,
            0xb18cff, 0x160f26, 0x6ee7c8, 0xff9db0, 0xf2d27a, 2.2f),
    ];
    public const string MatchDalamud = "Dalamud";
    public static IEnumerable<string> Names => Styles.Select(s => s.Name).Append(MatchDalamud);

    /// <summary>The style being drawn right now. Set by <see cref="Push"/> for each window.</summary>
    public static Palette Current { get; private set; } = Styles[0];

    public static string Next(string name, int step)
    {
        var names = Names.ToList();
        int index = Math.Max(0, names.IndexOf(name));
        return names[((index + step) % names.Count + names.Count) % names.Count];
    }

    public static Vector4 Rgb(uint rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);
    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);
    public static uint U32(Vector4 color, float alpha) => ImGui.GetColorU32(color with { W = color.W * alpha });
    public static float S(float value) => value * ImGuiHelpers.GlobalScale;
    /// <summary>A corner radius in this style's shape language.</summary>
    public static float R(float value) => S(value * Current.Round);
    // A slow 0..1 wave shared by every "live" indicator so they breathe together.
    public static float Pulse => (float)(0.5 + 0.5 * Math.Sin(ImGui.GetTime() * Math.PI / 0.9));

    public static Vector4 Ground => Current.Ground;
    public static Vector4 Field => Current.Field;
    public static Vector4 Edge => Current.Edge;
    public static Vector4 Raised => Current.Raised;
    public static Vector4 RaisedHover => Current.RaisedHover;
    public static Vector4 Ink => Current.Ink;
    public static Vector4 Body => Current.Body;
    public static Vector4 Muted => Current.Muted;
    public static Vector4 Error => Current.Error;
    public static Vector4 Warn => Current.Warn;
    public static Vector4 White => new(1, 1, 1, 1);
    public static readonly Vector4 MeterGreen = Rgb(0x5fbf7a), MeterAmber = Rgb(0xe0b84a), MeterRed = Rgb(0xe05a5a);
    public static Vector4 MeterOff => Current.Raised with { W = Current.Raised.W * .6f };

    /// <summary>Selects the named style and pushes its look; returns the counts <see cref="Pop"/> needs.</summary>
    public static (int Colors, int Styles) Push(string style)
    {
        Current = style == MatchDalamud ? FromDalamud() : Styles.FirstOrDefault(s => s.Name == style) ?? Styles[0];
        int colors = 0, styles = 0;
        void Style(ImGuiStyleVar slot, Vector2 value) { ImGui.PushStyleVar(slot, value); styles++; }
        Style(ImGuiStyleVar.WindowPadding, new Vector2(S(12), S(12))); Style(ImGuiStyleVar.FramePadding, new Vector2(S(8), S(5)));
        Style(ImGuiStyleVar.ItemSpacing, new Vector2(S(6), S(8)));
        if (!Current.PushColors) return (colors, styles);
        void Rounding(ImGuiStyleVar slot, float value) { ImGui.PushStyleVar(slot, value); styles++; }
        Rounding(ImGuiStyleVar.WindowRounding, R(4)); Rounding(ImGuiStyleVar.FrameRounding, R(3)); Rounding(ImGuiStyleVar.ChildRounding, R(6));
        Rounding(ImGuiStyleVar.GrabRounding, R(3)); Rounding(ImGuiStyleVar.PopupRounding, R(4));
        var p = Current;
        void Color(ImGuiCol slot, Vector4 value) { ImGui.PushStyleColor(slot, value); colors++; }
        Color(ImGuiCol.WindowBg, p.Ground); Color(ImGuiCol.ChildBg, p.Surface); Color(ImGuiCol.PopupBg, p.Surface);
        Color(ImGuiCol.TitleBg, p.Title); Color(ImGuiCol.TitleBgActive, p.Title); Color(ImGuiCol.TitleBgCollapsed, p.Title);
        Color(ImGuiCol.Border, p.Border); Color(ImGuiCol.Separator, p.Border);
        Color(ImGuiCol.FrameBg, p.Field); Color(ImGuiCol.FrameBgHovered, p.FieldHover); Color(ImGuiCol.FrameBgActive, p.FieldActive);
        Color(ImGuiCol.Button, p.Raised); Color(ImGuiCol.ButtonHovered, p.RaisedHover); Color(ImGuiCol.ButtonActive, p.RaisedActive);
        Color(ImGuiCol.Header, p.Raised); Color(ImGuiCol.HeaderHovered, p.RaisedHover); Color(ImGuiCol.HeaderActive, p.RaisedActive);
        Color(ImGuiCol.Text, p.Ink); Color(ImGuiCol.TextDisabled, p.Muted);
        Color(ImGuiCol.CheckMark, p.Host); Color(ImGuiCol.SliderGrab, p.Grab); Color(ImGuiCol.SliderGrabActive, p.Ink);
        return (colors, styles);
    }

    public static void Pop((int Colors, int Styles) pushed)
    {
        ImGui.PopStyleVar(pushed.Styles); ImGui.PopStyleColor(pushed.Colors);
    }

    // Read every frame, so changing the Dalamud theme carries straight into this style.
    private static Palette FromDalamud()
    {
        static Vector4 C(ImGuiCol slot) => ImGui.GetStyle().Colors[(int)slot];
        var ground = C(ImGuiCol.WindowBg) with { W = 1 };
        return new Palette(MatchDalamud, ground, Mix(ground, C(ImGuiCol.Text), .04f), C(ImGuiCol.FrameBg), C(ImGuiCol.Button),
            C(ImGuiCol.Border), C(ImGuiCol.Text), C(ImGuiCol.TextDisabled), C(ImGuiCol.TitleBgActive),
            Rgb(0xd94141), White, Rgb(0x4ea1d9), Rgb(0xff8a8a), Rgb(0xe0b84a), 1f, false);
    }

    private static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
}
