using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VenueAudio.Core;

namespace VenueAudio.WebRelay;

public static class RelayApplication
{
    public static WebApplication Create(string[] args, string? broadcastKey = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        string secret = broadcastKey ?? builder.Configuration["VENUE_BROADCAST_KEY"] ?? "";
        if (secret.Length < 32) throw new InvalidOperationException("Set VENUE_BROADCAST_KEY to a random secret of at least 32 characters.");
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        builder.Services.AddSingleton(_ => new RelayServer(IPAddress.Loopback, 0));
        var app = builder.Build();
        var relay = app.Services.GetRequiredService<RelayServer>();
        var slots = new SemaphoreSlim(RelayServer.MaxListeners + 16, RelayServer.MaxListeners + 16);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.MapGet("/health", () => Results.Ok(new { service = "venue-audio", version = 1 }));
        app.MapGet("/", () => Results.Text("Venue Audio relay is online. Connect using the in-game plugin. No audio is recorded."));
        app.Map("/audio", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            string role = context.Request.Headers["X-Venue-Role"].ToString();
            if (role is not ("host" or "listen")) { context.Response.StatusCode = 400; return; }
            if (role == "host")
            {
                string supplied = context.Request.Headers["X-Venue-Broadcast-Key"].ToString();
                if (supplied.Length > 256 || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                { context.Response.StatusCode = 401; return; }
            }
            if (!await slots.WaitAsync(0)) { context.Response.StatusCode = 503; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var stream = new WebSocketStream(socket);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var first = await Wire.ReadAsync(stream, handshake.Token);
                var hello = Wire.Parse<Hello>(first);
                if (hello.Role != role) return;
                // Reuse the tested room and audio protocol behind the secure public WebSocket endpoint.
                using var connection = new TcpClient { NoDelay = true };
                await connection.ConnectAsync(IPAddress.Loopback, relay.Port, handshake.Token);
                await using var raw = connection.GetStream();
                await Wire.WriteAsync(raw, first, handshake.Token);
                var upstream = stream.CopyToAsync(raw, 8192, session.Token);
                var downstream = raw.CopyToAsync(stream, 8192, session.Token);
                await Task.WhenAny(upstream, downstream);
                session.Cancel(); connection.Dispose(); stream.Dispose();
                try { await Task.WhenAll(upstream, downstream); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
            { /* Failed/closed connections contain no useful public error detail. */ }
            finally { slots.Release(); }
        });
        return app;
    }
}
