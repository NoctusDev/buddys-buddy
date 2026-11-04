using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using VenueAudio.Core;
using static VenueAudio.Theme;

namespace VenueAudio;

public sealed record FoundVenue(string Code, string LocationKey, string Venue, string HostName);

/// <summary>
/// Notices the listener walking into an announced house and offers (or makes) the join, then ends a
/// walk-in session when they leave. Location is only sent when entering a house, never polled remotely.
/// </summary>
public sealed class VenueWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly Session session;
    private readonly Settings settings;
    private readonly VenueDirectory directory;
    private readonly JoinPrompt prompt;
    private readonly HashSet<string> declined = [];
    private volatile string? here;
    private DateTime nextCheck;

    public VenueWatcher(IFramework framework, Session session, Settings settings, VenueDirectory directory, JoinPrompt prompt)
    {
        this.framework = framework; this.session = session; this.settings = settings; this.directory = directory; this.prompt = prompt;
        prompt.Join = venue => Join(venue);
        prompt.Decline = venue => declined.Add(venue.LocationKey);
        framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework sender)
    {
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow.AddSeconds(1);
        string? now = VenueLocation.Current()?.Key;
        if (now == here) return;
        here = now;
        prompt.Dismiss();
        if (session.JoinedLocation != null && session.JoinedLocation != now) _ = session.StopAsync();
        if (now != null && settings.Role == Role.Listener && settings.WatchVenues && !session.Active && !declined.Contains(now))
            _ = LookAsync(now);
    }

    private async Task LookAsync(string location)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var codes = await directory.AtAsync(location, timeout.Token);
            if (codes.Length == 0 || here != location) return;
            var invite = Invite.Parse(await directory.ResolveAsync(codes[0], timeout.Token));
            var found = new FoundVenue(codes[0], location, invite.Venue, invite.HostName);
            if (here != location || session.Active) return;
            if (settings.AutoJoin) Join(found);
            else prompt.Offer(found);
        }
        catch { /* The directory being unreachable just means no prompt. */ }
    }

    private void Join(FoundVenue venue)
    {
        if (here == venue.LocationKey && !session.Active) _ = session.ListenAsync(venue.Code, venue.LocationKey);
    }

    public void Dispose() => framework.Update -= OnUpdate;
}

public sealed class JoinPrompt : Window
{
    private readonly Settings settings;
    private readonly Action save;
    private readonly IFontHandle mono;
    private FoundVenue? offer;
    private bool always;
    private (int, int) pushed;
    public Action<FoundVenue>? Join { get; set; }
    public Action<FoundVenue>? Decline { get; set; }

    public JoinPrompt(Settings settings, Action save, IFontHandle mono)
        : base("House Lights###VenueAudioPrompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.settings = settings; this.save = save; this.mono = mono;
        RespectCloseHotkey = true;
    }

    /// <summary>Safe from any thread; the window places itself on its next frame.</summary>
    public void Offer(FoundVenue venue)
    {
        always = false; offer = venue; IsOpen = true;
    }

    public void Dismiss() { offer = null; IsOpen = false; }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2 - S(196), viewport.Size.Y * 0.28f), ImGuiCond.Appearing);
        pushed = Theme.Push(settings.Style);
    }
    public override void PostDraw() => Theme.Pop(pushed);
    public override void OnClose() { if (offer != null) Decline?.Invoke(offer); offer = null; }

    public override void Draw()
    {
        if (offer == null) { IsOpen = false; return; }
        var venue = offer;
        var p = Current;
        float width = S(360);
        var lines = new List<SignLine>
        {
            new("NOW PLAYING", p.Listen),
            new(venue.Venue.Length > 0 ? venue.Venue.ToUpperInvariant() : "LIVE VENUE", Ink),
        };
        if (venue.HostName.Length > 0) lines.Add(new("hosted by " + venue.HostName, Muted));
        MarqueeDraw.Sign(mono, Bulbs.Chasing, p.Listen, p.ListenEdge, lines, bulbs: 18, width: width);

        const string question = "This venue is playing live music. Join and listen?";
        float start = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(start + Math.Max(0, (width - ImGui.CalcTextSize(question).X) / 2));
        ImGui.TextColored(Body, question);

        float half = (width - ImGui.GetStyle().ItemSpacing.X) / 2, height = S(40);
        var min = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##join", new Vector2(half, height)))
        {
            if (always) { settings.AutoJoin = true; save(); }
            offer = null; IsOpen = false; Join?.Invoke(venue);
        }
        bool hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(half, height), U32(hovered ? p.ListenButtonHover : p.Listen), R(4));
        float label = ImGui.CalcTextSize("Join").X, x = min.X + (half - S(22) - label) / 2, mid = min.Y + height / 2;
        VenueWindow.Icon(draw, FontAwesomeIcon.Headphones, new Vector2(x + S(8), mid), U32(p.OnListen), small: true);
        draw.AddText(new Vector2(x + S(22), mid - ImGui.GetTextLineHeight() / 2), U32(p.OnListen), "Join");
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SameLine();
        if (ImGui.Button("Not now", new Vector2(half, height))) IsOpen = false;
        using (ImRaii.PushColor(ImGuiCol.CheckMark, p.Listen).Push(ImGuiCol.Text, Muted))
            ImGui.Checkbox("Join walk-in venues automatically from now on", ref always);
    }
}
