using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace VenueAudio.Core;

public sealed class RelayClient : IAsyncDisposable
{
    private readonly TcpClient client = new() { NoDelay = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(32);
    private Stream? stream;
    private Task? running;
    private int disposed;
    public event Action<byte[]>? Audio;
    public event Action<Notice>? Notification;
    public event Action<string>? Disconnected;
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(Invite invite, bool host, string key, CancellationToken ct, string serverKey = "", HttpMessageInvoker? transport = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(invite.WebSocket ? 90 : 10));
        if (invite.WebSocket)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            socket.Options.SetRequestHeader("X-Venue-Role", host ? "host" : "listen");
            if (host && !string.IsNullOrWhiteSpace(serverKey)) socket.Options.SetRequestHeader("X-Venue-Broadcast-Key", serverKey.Trim());
            stream = new WebSocketStream(socket);
            try { await socket.ConnectAsync(invite.WebSocketEndpoint, transport, timeout.Token); }
            catch (WebSocketException e) { throw new IOException("Relay connection refused. Check the server address and broadcaster access key.", e); }
        }
        else
        {
            await client.ConnectAsync(invite.Host, invite.Port, timeout.Token);
            stream = client.GetStream();
        }
        if (invite.Tls && !invite.WebSocket)
        {
            var tls = new SslStream(stream);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = invite.Host }, timeout.Token);
            stream = tls;
        }
        await Wire.WriteAsync(stream, Wire.Json(new Hello(host ? "host" : "listen", invite.Room, key)), timeout.Token);
        var notice = Wire.Parse<Notice>(await Wire.ReadAsync(stream, timeout.Token));
        if (notice.Type != "ready") throw new IOException(notice.Message);
        IsConnected = true; Notification?.Invoke(notice);
        running = RunAsync();
    }
    public void SendAudio(byte[] encoded)
    {
        if (!IsConnected) return;
        if (!outgoing.Writer.TryWrite([2, .. encoded]))
        {
            // Disconnect rather than allowing ever-increasing latency or damaged packet sequences.
            lifetime.Cancel(); client.Dispose();
        }
    }
    private async Task RunAsync()
    {
        string reason = "Disconnected.";
        Task? write = null, heartbeat = null;
        try
        {
            write = WriteAsync(); heartbeat = HeartbeatAsync();
            while (!lifetime.IsCancellationRequested)
            {
                var packet = await Wire.ReadAsync(stream!, lifetime.Token);
                if (packet[0] == 2) Audio?.Invoke(packet[1..]);
                else Notification?.Invoke(Wire.Parse<Notice>(packet));
            }
        }
        catch (Exception e)
        { reason = lifetime.IsCancellationRequested ? "Disconnected." : "Broadcast connection closed. " + e.Message; }
        finally
        {
            IsConnected = false; lifetime.Cancel(); client.Dispose(); stream?.Dispose(); outgoing.Writer.TryComplete();
            if (write != null) { try { await write; } catch { } }
            if (heartbeat != null) { try { await heartbeat; } catch { } }
            Disconnected?.Invoke(reason);
        }
    }
    private async Task WriteAsync()
    {
        try { await foreach (var packet in outgoing.Reader.ReadAllAsync(lifetime.Token)) await Wire.WriteAsync(stream!, packet, lifetime.Token); }
        finally { client.Dispose(); stream?.Dispose(); }
    }
    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(lifetime.Token)) outgoing.Writer.TryWrite(Wire.Json(new Notice("ping")));
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); client.Dispose(); stream?.Dispose();
        if (running != null) await running;
        if (stream != null) await stream.DisposeAsync();
        lifetime.Dispose();
    }
}
