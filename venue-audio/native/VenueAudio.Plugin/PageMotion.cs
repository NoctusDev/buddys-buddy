using System.Numerics;
using Dalamud.Bindings.ImGui;
using static VenueAudio.Theme;

namespace VenueAudio;

/// <summary>
/// Fits a window's height to the page it shows, easing between sizes, and slides a newly opened page in.
/// The slide moves the page's finished vertices rather than its layout, so widths and wrapping never
/// change mid-animation; only where it is painted does.
/// </summary>
internal sealed class PageMotion
{
    private const double SlideSeconds = .22;
    private const float SlideDistance = 90;

    private readonly Dictionary<int, float> heights = [];
    private float shown;
    private bool snap = true;
    private int direction, vertexStart;
    private double slideStart = -10;

    public PageMotion(int page) => Page = page;

    public int Page { get; private set; }
    public bool Moving { get; private set; }

    /// <summary>Show this page without animating, as when the window opens.</summary>
    public void Jump(int page) { Page = page; snap = true; slideStart = -10; }

    public void Go(int page)
    {
        if (page == Page) return;
        direction = Math.Sign(page - Page); Page = page; slideStart = ImGui.GetTime();
    }

    /// <summary>The height to give the window this frame. Call from PreDraw.</summary>
    public float Height(float fallback, float limit)
    {
        float target = Math.Min(limit, heights.TryGetValue(Page, out var measured) ? measured : shown > 0 ? shown : fallback);
        if (snap) { shown = target; snap = !heights.ContainsKey(Page); }
        else shown += (target - shown) * Math.Min(1, ImGui.GetIO().DeltaTime * 14);
        if (Math.Abs(target - shown) < .5f) shown = target;
        Moving = shown != target || ImGui.GetTime() - slideStart < SlideSeconds;
        return shown;
    }

    public void Begin() => vertexStart = ImGui.GetWindowDrawList().VtxBuffer.Size;

    /// <summary>Records the page's height from where its layout ended, and paints the slide.</summary>
    public unsafe void End(float bottomSpace)
    {
        heights[Page] = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + bottomSpace;
        float t = (float)Math.Clamp((ImGui.GetTime() - slideStart) / SlideSeconds, 0, 1);
        if (t >= 1) return;
        float eased = 1 - MathF.Pow(1 - t, 3);
        var offset = new Vector2(direction * S(SlideDistance) * (1 - eased), 0);
        var buffer = ImGui.GetWindowDrawList().VtxBuffer;
        for (int i = vertexStart; i < buffer.Size; i++)
        {
            ref var vertex = ref buffer.Data[i];
            vertex.Pos += offset;
            uint alpha = (uint)((vertex.Col >> 24) * eased);
            vertex.Col = (vertex.Col & 0x00ffffff) | (alpha << 24);
        }
    }
}
