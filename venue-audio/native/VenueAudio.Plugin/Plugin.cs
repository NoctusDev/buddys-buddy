using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VenueAudio;

[Serializable]
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public string SourceId { get; set; } = "";
    public string MicrophoneId { get; set; } = "";
    public string LastInvite { get; set; } = "";
    public string RemoteServer { get; set; } = "";
    public string RelayAccessKey { get; set; } = "";
    public string AdvertisedHost { get; set; } = "";
    public bool LocalHosting { get; set; } = false;
    public bool AutomaticInternet { get; set; } = true;
    public float Volume { get; set; } = .7f;
}

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows = new("VenueAudio");
    private readonly Session session;
    private readonly VenueWindow window;
    private readonly Settings settings;
    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands)
    {
        this.pluginInterface = pluginInterface; this.commands = commands;
        // Dalamud loads assemblies from memory, so Assembly.Location can be empty.
        // Its plugin interface retains the actual installed DLL path.
        session = new Session(Path.Combine(pluginInterface.AssemblyLocation.DirectoryName
            ?? throw new IOException("Venue Audio's installation folder could not be found."), "Tools"));
        try { settings = pluginInterface.GetPluginConfig() as Settings ?? new Settings(); }
        catch { settings = new Settings(); }
        if (settings.Version < 3) { settings.LocalHosting = false; settings.AutomaticInternet = true; settings.Version = 3; }
        session.Volume = settings.Volume;
        window = new VenueWindow(session, settings, () => pluginInterface.SavePluginConfig(settings));
        windows.AddWindow(window);
        commands.AddHandler("/venueaudio", new CommandInfo((_, _) => Toggle()) { HelpMessage = "Open Venue Audio's broadcast and listening controls." });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += Toggle;
        pluginInterface.UiBuilder.OpenMainUi += Toggle;
        // The first load opens the controls so installation does not require a chat command.
        window.IsOpen = true;
    }
    private void Toggle() => window.Toggle();
    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= Toggle;
        commands.RemoveHandler("/venueaudio"); windows.RemoveAllWindows();
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        pluginInterface.SavePluginConfig(settings);
    }
}

public sealed class VenueWindow : Window
{
    private readonly Session session;
    private readonly Settings settings;
    private readonly Action save;
    private List<AudioDevice> devices = [];
    private bool addMicrophone;
    private bool localHosting;
    private bool automaticInternet;
    private string sourceId, microphoneId, invitation, remote, advertised, relayKey;
    private string deviceError = "";
    private float volume, music = .8f, microphone = .8f;
    private Task? operation;
    public VenueWindow(Session session, Settings settings, Action save) : base("Venue Audio###VenueAudioMain")
    {
        this.session = session; this.settings = settings; this.save = save;
        sourceId = settings.SourceId; microphoneId = settings.MicrophoneId;
        invitation = settings.LastInvite; remote = settings.RemoteServer; advertised = settings.AdvertisedHost;
        relayKey = settings.RelayAccessKey;
        localHosting = settings.LocalHosting; volume = settings.Volume;
        automaticInternet = settings.AutomaticInternet;
        Size = new Vector2(650, 650); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 420), MaximumSize = new Vector2(float.MaxValue) };
        Refresh();
    }
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
        settings.SourceId = sourceId; settings.MicrophoneId = microphoneId; settings.LastInvite = invitation;
        settings.RemoteServer = remote; settings.AdvertisedHost = advertised; settings.LocalHosting = localHosting;
        settings.RelayAccessKey = relayKey;
        settings.AutomaticInternet = automaticInternet;
        settings.Volume = volume; save();
    }
    private void Run(Func<Task> action)
    {
        Remember(); operation = Task.Run(action);
    }
    private void DevicePicker(string label, ref string selected, bool microphoneOnly)
    {
        string selectedId = selected;
        var current = devices.FirstOrDefault(d => d.Id == selectedId);
        if (!ImGui.BeginCombo(label, current?.Name ?? "Choose device")) return;
        foreach (var device in devices.Where(d => !microphoneOnly || !d.IsOutput))
        {
            string text = (device.IsOutput ? "Output: " : "Input: ") + device.Name;
            if (ImGui.Selectable(text + "##" + device.Id, device.Id == selected)) selected = device.Id;
        }
        ImGui.EndCombo();
    }
    public override void Draw()
    {
        bool busy = operation is { IsCompleted: false };
        ImGui.TextWrapped("Live venue audio • early test build");
        ImGui.TextWrapped(session.Status);
        ImGui.Separator();
        if (session.Broadcasting)
        {
            ImGui.Text($"Broadcasting to {session.Listeners} listener(s)");
            ImGui.ProgressBar(session.Peak, new Vector2(-1, 18), "Audio level");
            if (ImGui.SliderFloat("Source volume", ref music, 0, 1)) session.MusicGain = music;
            if (addMicrophone && ImGui.SliderFloat("Microphone volume", ref microphone, 0, 1)) session.MicGain = microphone;
            ImGui.TextWrapped(session.Invite);
            if (ImGui.Button("Copy guest invite")) ImGui.SetClipboardText(session.Invite);
        }
        if (session.Active || busy)
        {
            if (ImGui.Button(busy ? "Cancel connection" : session.Broadcasting ? "Stop broadcasting" : "Disconnect")) Run(session.StopAsync);
        }
        if (ImGui.SliderFloat("Listening volume", ref volume, 0, 1)) { session.Volume = volume; Remember(); }
        ImGui.Separator();
        ImGui.BeginDisabled(busy || session.Active);
        if (ImGui.CollapsingHeader("Listen to a venue", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.InputText("Guest invite", ref invitation, 512);
            if (ImGui.Button("Paste invite")) invitation = ImGui.GetClipboardText();
            ImGui.SameLine();
            if (ImGui.Button("Listen")) { string value = invitation; Run(() => session.ListenAsync(value)); }
            ImGui.TextWrapped("Listening never opens your microphone. The complete invite includes the room's audio key.");
        }
        if (ImGui.CollapsingHeader("Broadcast from this PC", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DevicePicker("Audio source", ref sourceId, false);
            if (ImGui.Button("Refresh audio devices")) Refresh();
            if (!string.IsNullOrEmpty(deviceError)) ImGui.TextWrapped(deviceError);
            ImGui.TextWrapped("Output captures everything played through that device, including game sounds. An input can be a virtual DJ mixer. There is no application-only capture yet.");
            ImGui.Checkbox("Add microphone", ref addMicrophone);
            if (addMicrophone)
            {
                DevicePicker("Microphone", ref microphoneId, true);
                ImGui.TextWrapped("Leave this off if your selected mixer already includes your microphone.");
            }
            ImGui.Checkbox("Automatic internet hosting (temporary test address)", ref automaticInternet);
            if (automaticInternet)
            {
                ImGui.TextWrapped("Click Broadcast to open a temporary secure internet tunnel. Friends can connect from other networks. No hosting account or router changes needed. Stop closes the host and tunnel; the address changes each time.");
            }
            else
            {
            ImGui.Checkbox("Advanced: host on this PC instead of the internet relay", ref localHosting);
            if (localHosting)
            {
                ImGui.TextWrapped("No separate server needed for local-network testing. Windows may ask to allow network access. Friends outside your network need a reachable relay or router setup.");
                if (ImGui.TreeNode("Hosting address (optional)"))
                {
                    ImGui.InputText("Address guests reach", ref advertised, 256);
                    ImGui.TextWrapped("Blank uses the first local IPv4 address. Listening port: 45831. Local hosting is unencrypted; use the TLS relay for internet hosting.");
                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.InputText("Internet relay address", ref remote, 256);
                ImGui.InputText("DJ access key", ref relayKey, 256, ImGuiInputTextFlags.Password);
                ImGui.TextWrapped("Use the hosted relay's https:// address. The DJ key is saved locally and is never included in guest invites. A sleeping free server can take about a minute to wake.");
            }
            }
            bool valid = devices.Any(d => d.Id == sourceId) && (!addMicrophone || devices.Any(d => d.Id == microphoneId)) && (automaticInternet || localHosting || (!string.IsNullOrWhiteSpace(remote) && !string.IsNullOrWhiteSpace(relayKey)));
            ImGui.BeginDisabled(!valid);
            if (ImGui.Button("Start broadcasting"))
            {
                var source = devices.First(d => d.Id == sourceId);
                string? mic = addMicrophone ? microphoneId : null;
                bool local = localHosting; string address = remote, publicHost = advertised;
                string accessKey = relayKey;
                bool auto = automaticInternet;
                Run(() => session.BroadcastAsync(source, mic, local, address, publicHost, accessKey, auto));
            }
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextWrapped("No capture starts until you click Broadcast. Stop or unload ends capture. Penumbra/Lightless visuals can run normally; set booth music to Off.");
    }
}
