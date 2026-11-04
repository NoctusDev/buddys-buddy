using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using static VenueAudio.Theme;

namespace VenueAudio;

public enum Bulbs { Off, Chasing, Filling, Dimmed }

public readonly record struct SignLine(string Text, Vector4 Color, bool Mono = false);

public readonly record struct Ticket(Vector2 Min, Vector2 Max, float Perforation)
{
    public float Mid => (Min.Y + Max.Y) / 2;
}

/// <summary>
/// The "Marquee" look shared by every window: a sign framed by rows of bulbs, ticket stubs with a
/// perforation and notches, and plain panels. Shapes are drawn first; callers place real widgets on top.
/// </summary>
internal static class MarqueeDraw
{
    public static bool ChaseOn(int index) => ((int)(ImGui.GetTime() / .7) + index) % 2 == 0;
    public static bool Blink(double period) => ImGui.GetTime() % period < period / 2;
    /// <summary>1 → 0.35 → 1 over <paramref name="period"/> seconds.</summary>
    public static float Breath(double period, double offset = 0) =>
        (float)(.675 + .325 * Math.Cos(2 * Math.PI * (ImGui.GetTime() + offset) / period));

    public static void BulbRow(ImDrawListPtr draw, Vector2 left, float width, int count, float diameter, Bulbs mode,
        Vector4 accent, Vector4? alternate, int lit, bool reverse)
    {
        float step = count > 1 ? (width - diameter) / (count - 1) : 0;
        for (int i = 0; i < count; i++)
        {
            int order = reverse ? count - 1 - i : i;
            var color = mode switch
            {
                Bulbs.Chasing => (alternate is { } other && i % 2 == 1 ? other : accent) with { W = ChaseOn(i) ? 1 : .25f },
                Bulbs.Filling => order < lit || (order == lit && Blink(.5)) ? accent : Current.BulbOff,
                Bulbs.Dimmed => accent,
                _ => Current.BulbOff,
            };
            draw.AddCircleFilled(left + new Vector2(diameter / 2 + i * step, diameter / 2), diameter / 2, U32(color));
        }
    }

    /// <summary>The marquee: a panel with a bulb row top and bottom and up to three centred lines between.</summary>
    public static void Sign(IFontHandle mono, Bulbs mode, Vector4 accent, Vector4 edge, IReadOnlyList<SignLine> lines,
        Vector4? alternate = null, int lit = 0, int bulbs = 20, float width = 0)
    {
        var draw = ImGui.GetWindowDrawList();
        if (width <= 0) width = ImGui.GetContentRegionAvail().X;
        float padX = S(12), padY = S(10), gap = S(10), bulb = S(6), line = ImGui.GetTextLineHeight();
        float height = padY * 2 + bulb * 2 + gap * 2 + line * lines.Count;
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(width, height);
        draw.AddRectFilled(min, max, U32(Current.Surface), R(4));
        draw.AddRect(min, max, U32(edge), R(4));
        float inner = width - padX * 2;
        BulbRow(draw, min + new Vector2(padX, padY), inner, bulbs, bulb, mode, accent, alternate, lit, false);
        BulbRow(draw, new Vector2(min.X + padX, max.Y - padY - bulb), inner, bulbs, bulb, mode, accent, alternate, lit, true);
        float y = min.Y + padY + bulb + gap;
        foreach (var text in lines)
        {
            using (text.Mono ? mono.Push() : null)
            {
                string fitted = Fit(text.Text, inner);
                var size = ImGui.CalcTextSize(fitted);
                draw.AddText(new Vector2(min.X + (width - size.X) / 2, y + (line - size.Y) / 2), U32(text.Color), fitted);
            }
            y += line;
        }
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>Draws a ticket at the cursor without advancing; place widgets inside, then call <see cref="Advance"/>.</summary>
    public static Ticket Ticket(float height, float stubWidth, Vector4 fill, Vector4? edge, Vector4 perforation, float alpha = 1)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(ImGui.GetContentRegionAvail().X, height);
        float x = max.X - stubWidth;
        draw.AddRectFilled(min, max, U32(fill, alpha), R(4));
        if (edge is { } line) draw.AddRect(min, max, U32(line, alpha), R(4));
        Dashed(draw, new Vector2(x, min.Y + S(8)), new Vector2(x, max.Y - S(8)), U32(perforation, alpha));
        Notch(draw, new Vector2(x, min.Y), 0, edge, alpha);
        Notch(draw, new Vector2(x, max.Y), MathF.PI, edge, alpha);
        return new Ticket(min, max, x);
    }

    /// <summary>A half-circle bite out of an edge; <paramref name="from"/> is where its visible inner arc starts.</summary>
    public static void Notch(ImDrawListPtr draw, Vector2 center, float from, Vector4? edge, float alpha = 1, float radius = 6)
    {
        float r = S(radius);
        draw.AddCircleFilled(center, r, U32(Current.Ground));
        if (edge is not { } line) return;
        draw.PathArcTo(center, r, from, from + MathF.PI);
        draw.PathStroke(U32(line, alpha), ImDrawFlags.None, 1);
    }

    /// <summary>Claims the space of a ticket or other hand-drawn block so layout continues below it.</summary>
    public static void Advance(Vector2 min, Vector2 max)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(max - min);
    }

    public static void Advance(Ticket ticket) => Advance(ticket.Min, ticket.Max);

    public static void Dashed(ImDrawListPtr draw, Vector2 a, Vector2 b, uint color)
    {
        var along = b - a;
        float length = along.Length(), dash = S(4), gap = S(3);
        if (length <= 0) return;
        along /= length;
        for (float t = 0; t < length; t += dash + gap)
            draw.AddLine(a + along * t, a + along * Math.Min(t + dash, length), color, 1);
    }

    public static void DashedRect(ImDrawListPtr draw, Vector2 min, Vector2 max, uint color)
    {
        Dashed(draw, min, new Vector2(max.X, min.Y), color);
        Dashed(draw, new Vector2(max.X, min.Y), max, color);
        Dashed(draw, max, new Vector2(min.X, max.Y), color);
        Dashed(draw, new Vector2(min.X, max.Y), min, color);
    }

    /// <summary>
    /// Starts a panel whose background is drawn after its contents are measured: contents go to draw channel 1,
    /// the panel to channel 0 underneath. Panels must not nest.
    /// </summary>
    public static (Vector2 Start, float Width) BeginPanel(float padX, float padY)
    {
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.Indent(padX);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + padY);
        return (start, width);
    }

    public static void EndPanel((Vector2 Start, float Width) panel, float padX, float padY, Vector4 fill, Vector4 edge)
    {
        ImGui.Unindent(padX);
        var draw = ImGui.GetWindowDrawList();
        var max = new Vector2(panel.Start.X + panel.Width, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + padY);
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(panel.Start, max, U32(fill), R(4));
        draw.AddRect(panel.Start, max, U32(edge), R(4));
        draw.ChannelsMerge();
        Advance(panel.Start, max);
    }

    public static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        while (text.Length > 1 && ImGui.CalcTextSize(text + "…").X > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }
}
