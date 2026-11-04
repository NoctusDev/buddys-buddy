using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VenueAudio.Core;

namespace VenueAudio;

public sealed class Session : IAsyncDisposable
{
    private readonly string? installedToolsFolder;
    public Session(string? installedToolsFolder = null) => this.installedToolsFolder = installedToolsFolder;
    private RelayServer? relay;
    private RelayClient? client;
    private AudioBroadcaster? broadcaster;
    private AudioListener? listener;
    private AudioCipher? audioCipher;
    private InternetHost? internetHost;
    private CancellationTokenSource? connecting;
    private readonly SemaphoreSlim operation = new(1, 1);
    private volatile string status = "Ready. Audio capture is off.";
    private volatile string invite = "";
    private volatile bool active;
    private volatile bool broadcasting;
    private volatile int listeners;
    private volatile float volume = .7f;
    private volatile float musicGain = .8f, micGain = .8f;
    public string Status => status;
    public string Invite => invite;
    public bool Active => active;
    public bool Broadcasting => broadcasting;
    public int Listeners => listeners;
    public float Peak => broadcaster?.Peak ?? 0;
    public float Volume { set { volume = value; if (listener != null) listener.Volume = value; } }
    public float MusicGain { set { musicGain = value; if (broadcaster != null) broadcaster.MusicGain = value; } }
    public float MicGain { set { micGain = value; if (broadcaster != null) broadcaster.MicGain = value; } }

    public Task BroadcastAsync(AudioDevice source, string? microphone, bool local, string remote, string advertised, string serverKey = "", bool automaticInternet = false, string? toolsFolder = null) => PerformAsync(async () =>
    {
        await StopInternalAsync();
        status = "Starting broadcast…";
        connecting = new CancellationTokenSource();
        string room = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Invite connection;
        Invite shared;
        if (automaticInternet)
        {
            status = "Opening a temporary internet relay… This can take about a minute.";
            internetHost = new InternetHost();
            string folder = toolsFolder ?? installedToolsFolder
                ?? throw new IOException("The plugin's hosting-file location is unavailable. Reload Venue Audio and try again.");
            string endpoint = await internetHost.StartAsync(folder, connecting.Token);
            serverKey = internetHost.AccessKey;
            shared = Core.Invite.Hosted(endpoint, room);
            connection = new Invite(internetHost.LocalEndpoint!.Host, internetHost.LocalEndpoint.Port, room, false, true);
        }
        else if (local)
        {
            relay = new RelayServer(IPAddress.Any);
            connection = new Invite("127.0.0.1", relay.Port, room);
            shared = new Invite(string.IsNullOrWhiteSpace(advertised) ? LocalAddress() : advertised.Trim(), relay.Port, room);
            // Validate the advertised hostname before capturing anything.
            _ = Core.Invite.Parse(shared.ToString());
        }
        else
        {
            if (string.IsNullOrWhiteSpace(serverKey)) throw new InvalidOperationException("Paste the relay's broadcaster access key in DJ settings.");
            connection = shared = Core.Invite.Hosted(remote, room);
        }
        client = new RelayClient();
        Attach(client);
        await client.ConnectAsync(connection, true, key, connecting.Token, serverKey);
        string audioKey = AudioCipher.NewKey();
        audioCipher = new AudioCipher(audioKey);
        var cipher = audioCipher; var connectionClient = client;
        broadcaster = new AudioBroadcaster(source, microphone, data => connectionClient.SendAudio(cipher.Encrypt(data))) { MusicGain = musicGain, MicGain = micGain };
        broadcaster.Failed += reason => _ = Task.Run(() => FailAsync(reason));
        broadcaster.Start();
        invite = (shared with { AudioKey = audioKey }).ToString(); active = true; broadcasting = true;
        status = automaticInternet ? "Broadcasting over the internet. Copy the guest invite for your friends." : local ? "Broadcasting. Local hosting is active; share the invite with reachable listeners." : "Broadcasting through the hosted relay.";
    });
    public Task ListenAsync(string invitation) => PerformAsync(async () =>
    {
        await StopInternalAsync(); status = "Connecting to venue…";
        connecting = new CancellationTokenSource();
        var target = Core.Invite.Parse(invitation);
        listener = new AudioListener(volume);
        client = new RelayClient(); Attach(client);
        if (!string.IsNullOrEmpty(target.AudioKey))
        {
            audioCipher = new AudioCipher(target.AudioKey);
            var cipher = audioCipher; var playback = listener;
            client.Audio += packet => playback.Receive(cipher.Decrypt(packet));
        }
        else client.Audio += listener.Receive;
        await client.ConnectAsync(target, false, "", connecting.Token);
        active = true; broadcasting = false; status = "Listening live. Your microphone is off.";
    });
    private void Attach(RelayClient value)
    {
        value.Notification += notice => { listeners = notice.Listeners; };
        value.Disconnected += OnDisconnect;
    }
    private Task FailAsync(string reason) => PerformAsync(async () => { await StopInternalAsync(); status = reason; });
    public Task StopAsync()
    {
        try { connecting?.Cancel(); } catch (ObjectDisposedException) { }
        return PerformAsync(async () => { await StopInternalAsync(); status = "Stopped. Audio capture is off."; });
    }
    private async Task PerformAsync(Func<Task> action)
    {
        await operation.WaitAsync();
        try { await action(); }
        catch (Exception e) { await StopInternalAsync(); status = "Could not continue: " + e.Message; }
        finally { operation.Release(); }
    }
    private async Task StopInternalAsync()
    {
        active = false; broadcasting = false; invite = ""; listeners = 0;
        broadcaster?.Dispose(); broadcaster = null;
        listener?.Dispose(); listener = null;
        if (client != null)
        {
            var old = client; client = null;
            // Prevent intentional disconnect from scheduling another stop operation.
            old.Disconnected -= OnDisconnect;
            await old.DisposeAsync();
        }
        audioCipher?.Dispose(); audioCipher = null;
        if (relay != null) { await relay.DisposeAsync(); relay = null; }
        if (internetHost != null) { await internetHost.DisposeAsync(); internetHost = null; }
        connecting?.Dispose(); connecting = null;
    }
    private void OnDisconnect(string reason) => _ = FailAsync(reason);
    public static string LocalAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName()).First(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToString();
        }
        catch { return "127.0.0.1"; }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
