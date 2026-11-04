using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VenueAudio.Core;

namespace VenueAudio;

public enum Role { None, Host, Listener }

[Serializable]
public sealed class Settings : IPluginConfiguration
{
    public const string DefaultDirectory = "https://buddys-jobs.noctusdev.workers.dev";
    public int Version { get; set; } = 5;
    public string SourceId { get; set; } = "";
    public string MicrophoneId { get; set; } = "";
    public string LastInvite { get; set; } = "";
    public string RemoteServer { get; set; } = "";
    public string RelayAccessKey { get; set; } = "";
    public string AdvertisedHost { get; set; } = "";
    public bool LocalHosting { get; set; } = false;
    public bool AutomaticInternet { get; set; } = true;
    public float Volume { get; set; } = .7f;
    public Role Role { get; set; } = Role.None;
    public bool RememberRole { get; set; } = true;
    public string VenueName { get; set; } = "";
    public bool AddMicrophone { get; set; } = false;
    public float MusicGain { get; set; } = .8f;
    public float MicGain { get; set; } = .8f;
    public int Bitrate { get; set; } = 128000;
    public bool ShortCodes { get; set; } = true;
    public string DirectoryServer { get; set; } = DefaultDirectory;
    public bool HideCodes { get; set; } = false;
    public bool Announce { get; set; } = false;
    public bool WatchVenues { get; set; } = true;
    public bool AutoJoin { get; set; } = false;
    public string Style { get; set; } = "Velvet";
}

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows = new("VenueAudio");
    private readonly VenueDirectory directory;
    private readonly Session session;
    private readonly VenueWindow window;
    private readonly VenueWatcher watcher;
    private readonly Settings settings;
    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IPlayerState player, IFramework framework)
    {
        this.pluginInterface = pluginInterface; this.commands = commands;
        try { settings = pluginInterface.GetPluginConfig() as Settings ?? new Settings(); }
        catch { settings = new Settings(); }
        if (settings.Version < 3) { settings.LocalHosting = false; settings.AutomaticInternet = true; }
        if (settings.Version < 5) { settings.ShortCodes = true; settings.WatchVenues = true; settings.Bitrate = 128000; settings.DirectoryServer = Settings.DefaultDirectory; }
        settings.Version = 5;
        if (!settings.RememberRole) settings.Role = Role.None;
        if (!Uri.TryCreate(settings.DirectoryServer, UriKind.Absolute, out _)) settings.DirectoryServer = Settings.DefaultDirectory;
        directory = new VenueDirectory(settings.DirectoryServer);
        // Dalamud loads assemblies from memory, so Assembly.Location can be empty.
        // Its plugin interface retains the actual installed DLL path.
        string tools = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName
            ?? throw new IOException("Venue Audio's installation folder could not be found."), "Tools");
        session = new Session(tools, directory) { Volume = settings.Volume, MusicGain = settings.MusicGain, MicGain = settings.MicGain };
        window = new VenueWindow(session, settings, () => pluginInterface.SavePluginConfig(settings),
            () => player.IsLoaded ? player.CharacterName : "", pluginInterface.UiBuilder.MonoFontHandle);
        var prompt = new JoinPrompt(settings, () => pluginInterface.SavePluginConfig(settings));
        watcher = new VenueWatcher(framework, session, settings, directory, prompt);
        windows.AddWindow(window); windows.AddWindow(prompt);
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
        watcher.Dispose();
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        directory.Dispose();
        pluginInterface.SavePluginConfig(settings);
    }
}
