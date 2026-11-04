using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace VenueAudio.Core;

/// <summary>Small audio fan-out relay. Slow listeners are disconnected rather than building latency.</summary>
public sealed class RelayServer : IAsyncDisposable
{
    private sealed class Peer(TcpClient client, Stream stream)
    {
        public TcpClient Client { get; } = client;
        public Stream Stream { get; } = stream;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(64);
        public bool Send(byte[] packet)
        {
            if (Queue.Writer.TryWrite(packet)) return true;
            Client.Dispose(); return false;
        }
    }
    private sealed class Room(Peer host, string key)
    {
        public Peer Host { get; } = host;
        public string Key { get; } = key;
        public HashSet<Peer> Listeners { get; } = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private readonly ConcurrentDictionary<TcpClient, Task> connections = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TcpListener listener;
    private readonly X509Certificate2? certificate;
    private readonly Task accepting;
    private int active;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ListenerCount { get { lock (gate) return rooms.Values.Sum(r => r.Listeners.Count); } }

    public RelayServer(IPAddress address, int port = Wire.Port, X509Certificate2? certificate = null)
    {
        this.certificate = certificate;
        listener = new TcpListener(address, port);
        listener.Start(64);
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (Interlocked.Increment(ref active) > 128) { Interlocked.Decrement(ref active); client.Dispose(); continue; }
                var task = HandleAsync(client);
                connections[client] = task;
                _ = task.ContinueWith(_ => { connections.TryRemove(client, out var ignored); Interlocked.Decrement(ref active); }, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        Peer? peer = null;
        Room? joined = null;
        Hello? hello = null;
        Task? writing = null;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                if (certificate != null)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, handshake.Token);
                    stream = ssl;
                }
                peer = new Peer(client, stream);
                hello = Wire.Parse<Hello>(await Wire.ReadAsync(stream, handshake.Token));
            }
            string? error = null;
            lock (gate)
            {
                if (hello.Version != 1 || !Wire.ValidSecret(hello.Room)) error = "Invalid room or protocol version.";
                else if (hello.Role == "host")
                {
                    if (!Wire.ValidSecret(hello.Key)) error = "Invalid broadcaster key.";
                    else if (rooms.ContainsKey(hello.Room)) error = "Room already has a broadcaster.";
                    else if (rooms.Count >= 16) error = "Relay room limit reached.";
                    else { joined = new Room(peer, hello.Key); rooms.Add(hello.Room, joined); }
                }
                else if (hello.Role == "listen")
                {
                    if (!rooms.TryGetValue(hello.Room, out joined)) error = "This room is offline. Ask the DJ for a current invite.";
                    else if (joined.Listeners.Count >= 64) { joined = null; error = "Room is full."; }
                    else joined.Listeners.Add(peer);
                }
                else error = "Invalid role.";
                if (error == null)
                {
                    peer.Send(Wire.Json(new Notice("ready", Listeners: joined!.Listeners.Count)));
                    joined.Host.Send(Wire.Json(new Notice("count", Listeners: joined.Listeners.Count)));
                }
            }
            if (error != null) { await Wire.WriteAsync(stream, Wire.Json(new Notice("error", error)), session.Token); return; }
            writing = WriteLoopAsync(peer, session.Token);
            while (!session.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(20));
                var packet = await Wire.ReadAsync(stream, idle.Token);
                if (packet[0] == 2)
                {
                    if (hello.Role != "host" || packet.Length is < 2 or > 1601) throw new IOException("Invalid audio packet");
                    lock (gate) foreach (var guest in joined!.Listeners) guest.Send(packet);
                }
                else if (Wire.Parse<Notice>(packet).Type != "ping") throw new IOException("Invalid message");
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or System.Security.Authentication.AuthenticationException) { }
        finally
        {
            lock (gate)
            {
                if (joined != null && peer != null)
                {
                    if (hello?.Role == "host" && ReferenceEquals(joined.Host, peer))
                    {
                        rooms.Remove(hello.Room);
                        // Closing the sockets makes every listener stop playback immediately.
                        foreach (var guest in joined.Listeners) guest.Client.Dispose();
                    }
                    else if (joined.Listeners.Remove(peer)) joined.Host.Send(Wire.Json(new Notice("count", Listeners: joined.Listeners.Count)));
                }
            }
            session.Cancel(); peer?.Queue.Writer.TryComplete(); client.Dispose();
            if (writing != null) { try { await writing; } catch { /* Transport already closed. */ } }
            if (peer?.Stream is SslStream tls) await tls.DisposeAsync();
        }
    }
    private static async Task WriteLoopAsync(Peer peer, CancellationToken ct)
    {
        try { await foreach (var packet in peer.Queue.Reader.ReadAllAsync(ct)) await Wire.WriteAsync(peer.Stream, packet, ct); }
        finally { peer.Client.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop();
        foreach (var client in connections.Keys) client.Dispose();
        await accepting;
        await Task.WhenAll(connections.Values);
        lifetime.Dispose();
    }
}
