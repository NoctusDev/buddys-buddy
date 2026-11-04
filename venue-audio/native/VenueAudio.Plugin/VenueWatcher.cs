using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private FoundVenue? offer;
    private bool always;
    private (int, int) pushed;
    public Action<FoundVenue>? Join { get; set; }
    public Action<FoundVenue>? Decline { get; set; }

    public JoinPrompt(Settings settings, Action save)
        : base("Venue Audio###VenueAudioPrompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.settings = settings; this.save = save;
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
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var center = min + new Vector2(S(20), S(20));
        draw.AddCircleFilled(center, S(20), U32(Current.ListenBadge));
        VenueWindow.Icon(draw, FontAwesomeIcon.Music, center, U32(Current.ListenIcon));
        ImGui.SetCursorScreenPos(min + new Vector2(S(52), S(10)));
        ImGui.Text("This venue has Venue Audio");
        ImGui.SetCursorScreenPos(min + new Vector2(0, S(48)));
        string name = venue.Venue.Length > 0 ? venue.Venue : "This venue";
        string by = venue.HostName.Length > 0 ? $", hosted by {venue.HostName}" : "";
        using (ImRaii.TextWrapPos(S(360))) ImGui.TextColored(Body, $"{name} is playing live music{by}. Would you like to join and listen?");
        ImGui.Dummy(new Vector2(S(360), S(4)));
        float half = (S(360) - ImGui.GetStyle().ItemSpacing.X) / 2;
        using (ImRaii.PushColor(ImGuiCol.Button, Current.ListenButton).Push(ImGuiCol.ButtonHovered, Current.ListenButtonHover).Push(ImGuiCol.Text, Current.OnListen))
            if (ImGui.Button("Join", new Vector2(half, S(40))))
            {
                if (always) { settings.AutoJoin = true; save(); }
                offer = null; IsOpen = false; Join?.Invoke(venue);
            }
        ImGui.SameLine();
        if (ImGui.Button("Not now", new Vector2(half, S(40)))) IsOpen = false;
        using (ImRaii.PushColor(ImGuiCol.CheckMark, Current.Listen)) ImGui.Checkbox("Join walk-in venues automatically from now on", ref always);
    }
}
