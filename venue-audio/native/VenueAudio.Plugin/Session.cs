using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VenueAudio.Core;

namespace VenueAudio;

public sealed record BroadcastOptions(int Bitrate = 128000, string Venue = "", string HostName = "", string? Location = null, bool ShortCode = true);

public sealed class Session : IAsyncDisposable
{
    private readonly string? installedToolsFolder;
    private readonly VenueDirectory? directory;
    public Session(string? installedToolsFolder = null, VenueDirectory? directory = null)
    {
        this.installedToolsFolder = installedToolsFolder; this.directory = directory;
    }
    private RelayServer? relay;
    private RelayClient? client;
    private AudioBroadcaster? broadcaster;
    private AudioListener? listener;
    private AudioCipher? audioCipher;
    private InternetHost? internetHost;
    private CancellationTokenSource? connecting;
    private Task? keepingCode;
    private readonly SemaphoreSlim operation = new(1, 1);
    private volatile string status = "Ready. Audio capture is off.";
    private volatile string invite = "", code = "", codeProblem = "";
    private volatile bool active;
    private volatile bool broadcasting;
    private volatile bool faulted;
    private volatile int listeners;
    private volatile int bitrate;
    private volatile float volume = .7f;
    private volatile float musicGain = .8f, micGain = .8f;
    private volatile bool musicMuted, micMuted, listenMuted;
    private volatile Invite? joined;
    private volatile string? joinedLocation;
    private long liveSinceTicks;
    public string Status => status;
    public string Invite => invite;
    /// <summary>The short code guests can type instead of the invite; empty when the directory was unavailable.</summary>
    public string Code => code;
    public string CodeProblem => codeProblem;
    public bool Active => active;
    public bool Broadcasting => broadcasting;
    public bool Faulted => faulted;
    public int Listeners => listeners;
    public int Bitrate => bitrate;
    public Invite? Joined => joined;
    /// <summary>Where a location-detected join happened, so walking out can end it.</summary>
    public string? JoinedLocation => joinedLocation;
    public TimeSpan LiveFor => active ? DateTime.UtcNow - new DateTime(Interlocked.Read(ref liveSinceTicks), DateTimeKind.Utc) : TimeSpan.Zero;
    public float Peak => broadcaster?.Peak ?? listener?.Peak ?? 0;
    public float Volume { set { volume = value; if (listener != null) listener.Volume = value; } }
    public float MusicGain { set { musicGain = value; if (broadcaster != null) broadcaster.MusicGain = value; } }
    public float MicGain { set { micGain = value; if (broadcaster != null) broadcaster.MicGain = value; } }
    public bool MusicMuted { get => musicMuted; set { musicMuted = value; if (broadcaster != null) broadcaster.MusicMuted = value; } }
    public bool MicMuted { get => micMuted; set { micMuted = value; if (broadcaster != null) broadcaster.MicMuted = value; } }
    public bool ListenMuted { get => listenMuted; set { listenMuted = value; if (listener != null) listener.Muted = value; } }

    public Task BroadcastAsync(AudioDevice source, string? microphone, bool local, string remote, string advertised, string serverKey = "", bool automaticInternet = false, string? toolsFolder = null, BroadcastOptions? options = null) => PerformAsync(async () =>
    {
        options ??= new BroadcastOptions();
        await StopInternalAsync();
        faulted = false;
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
        broadcaster = new AudioBroadcaster(source, microphone, data => connectionClient.SendAudio(cipher.Encrypt(data)), options.Bitrate)
        {
            MusicGain = musicGain, MicGain = micGain, MusicMuted = musicMuted, MicMuted = micMuted,
        };
        broadcaster.Failed += reason => _ = Task.Run(() => FailAsync(reason));
        broadcaster.Start();
        bitrate = options.Bitrate;
        invite = (shared with { AudioKey = audioKey, Venue = Core.Invite.Label(options.Venue), HostName = Core.Invite.Label(options.HostName) }).ToString();
        MarkLive(); broadcasting = true;
        status = automaticInternet ? "Broadcasting over the internet." : local ? "Broadcasting. Local hosting is active; share the invite with reachable listeners." : "Broadcasting through the hosted relay.";
        if (options.ShortCode && directory != null) await PublishCodeAsync(invite, options.Location, connecting.Token);
    });

    private async Task PublishCodeAsync(string value, string? location, CancellationToken ct)
    {
        try
        {
            status = "Broadcasting. Reserving a short code…";
            string reserved = await directory!.PublishAsync(value, location, ct);
            code = reserved;
            status = location == null ? "Broadcasting. Share the code with your guests." : "Broadcasting and announced here. Guests who walk in get a join prompt.";
            keepingCode = KeepCodeAsync(reserved, value, location, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            codeProblem = "Short code unavailable (" + e.Message + "). Share the full invite instead.";
            status = "Broadcasting. Share the full invite with your guests.";
        }
    }

    private async Task KeepCodeAsync(string reserved, string value, string? location, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(4));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (!await directory!.RefreshAsync(reserved, value, location, ct)) { code = ""; codeProblem = "The short code expired. Share the full invite instead."; return; }
                    codeProblem = "";
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    codeProblem = "Couldn't refresh the short code; retrying.";
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task ListenAsync(string invitation, string? location = null) => PerformAsync(async () =>
    {
        await StopInternalAsync(); faulted = false; status = "Connecting to venue…";
        connecting = new CancellationTokenSource();
        string text = invitation.Trim();
        if (VenueCodes.Normalize(text) is { } typed)
        {
            if (directory == null) throw new InvalidOperationException("Short codes need the venue directory. Paste the full invite instead.");
            status = "Looking up venue code…";
            text = await directory.ResolveAsync(typed, connecting.Token);
        }
        var target = Core.Invite.Parse(text);
        joined = target; joinedLocation = location;
        listener = new AudioListener(volume, listenMuted);
        client = new RelayClient(); Attach(client);
        if (!string.IsNullOrEmpty(target.AudioKey))
        {
            audioCipher = new AudioCipher(target.AudioKey);
            var cipher = audioCipher; var playback = listener;
            client.Audio += packet => playback.Receive(cipher.Decrypt(packet));
        }
        else client.Audio += listener.Receive;
        await client.ConnectAsync(target, false, "", connecting.Token);
        MarkLive(); broadcasting = false; status = "Listening live. Your microphone is off.";
    });
    private void MarkLive()
    {
        Interlocked.Exchange(ref liveSinceTicks, DateTime.UtcNow.Ticks);
        active = true;
    }
    private void Attach(RelayClient value)
    {
        value.Notification += notice => { listeners = notice.Listeners; };
        value.Disconnected += OnDisconnect;
    }
    private Task FailAsync(string reason) => PerformAsync(async () => { await StopInternalAsync(); status = reason; faulted = true; });
    public Task StopAsync()
    {
        try { connecting?.Cancel(); } catch (ObjectDisposedException) { }
        return PerformAsync(async () => { await StopInternalAsync(); faulted = false; status = "Stopped. Audio capture is off."; });
    }
    private async Task PerformAsync(Func<Task> action)
    {
        await operation.WaitAsync();
        try { await action(); }
        catch (Exception e) { await StopInternalAsync(); status = "Could not continue: " + e.Message; faulted = true; }
        finally { operation.Release(); }
    }
    private async Task StopInternalAsync()
    {
        active = false; broadcasting = false; invite = ""; listeners = 0; joined = null; joinedLocation = null; codeProblem = "";
        try { connecting?.Cancel(); } catch (ObjectDisposedException) { }
        if (keepingCode != null) { try { await keepingCode; } catch { } keepingCode = null; }
        if (code.Length > 0 && directory != null)
        {
            // Best effort: an undeleted code expires on its own within the directory's TTL.
            string old = code; code = "";
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await directory.DeleteAsync(old, quick.Token); } catch { }
        }
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
