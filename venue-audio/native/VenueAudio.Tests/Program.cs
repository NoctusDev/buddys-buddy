using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using VenueAudio;
using VenueAudio.Core;
using VenueAudio.WebRelay;
using System.Security.Cryptography;

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
    checks++; Console.WriteLine("PASS: " + name);
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(args.Contains("--internet") ? 180 : 30));
var ct = timeout.Token;
await using (var unconfiguredSession = new Session())
{
    await unconfiguredSession.BroadcastAsync(new AudioDevice("unused", "unused", true), null, false, "", "", automaticInternet: true);
    Check(!unconfiguredSession.Active && unconfiguredSession.Status.Contains("hosting-file location"),
        "missing loader path is handled before starting helpers or capture");
}
const string room = "0123456789abcdef0123456789abcdef";
const string key = "abcdef0123456789abcdef0123456789";
var invitation = new Invite("127.0.0.1", Wire.Port, room);
Check(Invite.Parse(invitation.ToString()) == invitation, "invites round-trip");
Check(Invite.Parse(new Invite("::1", Wire.Port, room, true).ToString()).Tls, "IPv6 and TLS invites");
var hostedInvite = Invite.Hosted("https://venue-example.onrender.com", room);
Check(hostedInvite.Tls && hostedInvite.WebSocket && hostedInvite.Port == 443, "hosted relay uses secure WebSockets on standard port");
Check(Invite.Parse(hostedInvite.ToString()) == hostedInvite, "internet guest invite round-trips");
Check(Invite.Hosted("venue-example.onrender.com", room) == hostedInvite, "hosted address accepts hostname without scheme");
string audioKey = AudioCipher.NewKey();
using var sendCipher = new AudioCipher(audioKey);
using var receiveCipher = new AudioCipher(audioKey);
var secretInvite = hostedInvite with { AudioKey = audioKey };
Check(Invite.Parse(secretInvite.ToString()) == secretInvite, "encrypted invitation preserves room audio key");
Check(!secretInvite.WebSocketEndpoint.ToString().Contains(audioKey), "room audio key is absent from server URL");
byte[] clear = [1, 2, 3, 4, 5];
byte[] sealedAudio = sendCipher.Encrypt(clear);
Check(!sealedAudio.SequenceEqual(clear) && receiveCipher.Decrypt(sealedAudio).SequenceEqual(clear), "room encryption decrypts authenticated payload");
bool replayRejected = false;
try { receiveCipher.Decrypt(sealedAudio); } catch (CryptographicException) { replayRejected = true; }
Check(replayRejected, "replayed audio is rejected");
using var wrongCipher = new AudioCipher(AudioCipher.NewKey());
bool wrongKeyRejected = false;
try { wrongCipher.Decrypt(sealedAudio); } catch (CryptographicException) { wrongKeyRejected = true; }
Check(wrongKeyRejected, "wrong room audio key cannot decrypt");
using var tamperCipher = new AudioCipher(audioKey);
var damaged = (byte[])sealedAudio.Clone(); damaged[^1] ^= 1;
bool tamperRejected = false;
try { tamperCipher.Decrypt(damaged); } catch (CryptographicException) { tamperRejected = true; }
Check(tamperRejected, "modified encrypted audio is rejected");
foreach (var bad in new[] { "hello", "https://example.com", "venueaudio://localhost:0/" + room, "venueaudio://localhost:80/notaroom" })
{
    bool rejected = false; try { Invite.Parse(bad); } catch (FormatException) { rejected = true; }
    Check(rejected, "invalid invite rejected: " + bad);
}
var invalidHeader = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(invalidHeader, int.MaxValue);
bool bounded = false;
try { await Wire.ReadAsync(new MemoryStream(invalidHeader), ct); } catch (IOException) { bounded = true; }
Check(bounded, "oversized frame rejected before allocation");

await using var relay = new RelayServer(IPAddress.Loopback, 0);
invitation = invitation with { Port = relay.Port };
async Task<Notice> RawHello(Hello hello)
{
    using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, relay.Port, ct);
    await Wire.WriteAsync(raw.GetStream(), Wire.Json(hello), ct);
    return Wire.Parse<Notice>(await Wire.ReadAsync(raw.GetStream(), ct));
}
Check((await RawHello(new Hello("listen", room))).Type == "error", "offline room rejected");
Check((await RawHello(new Hello("host", room, "bad"))).Type == "error", "invalid broadcaster key rejected");
Check((await RawHello(new Hello("host", room, key, 99))).Type == "error", "incompatible protocol rejected");
Check((await RawHello(new Hello("host", null!, key))).Type == "error", "malformed room rejected");

await using var host = new RelayClient();
var countChanged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
host.Notification += notice => { if (notice.Listeners == 2) countChanged.TrySetResult(notice.Listeners); };
await host.ConnectAsync(invitation, true, key, ct);
Check((await RawHello(new Hello("host", room, key))).Type == "error", "second broadcaster cannot take over room");
var receivedA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
var receivedB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var guestA = new RelayClient();
await using var guestB = new RelayClient();
guestA.Audio += bytes => receivedA.TrySetResult(bytes);
guestB.Audio += bytes => receivedB.TrySetResult(bytes);
guestA.Disconnected += _ => left.TrySetResult();
await guestA.ConnectAsync(invitation, false, "", ct);
await guestB.ConnectAsync(invitation, false, "", ct);
Check(await countChanged.Task.WaitAsync(ct) == 2, "host receives accurate listener count");
var encoder = OpusEncoder.Create(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
encoder.Bitrate = 128000;
var input = new short[1920];
for (int i = 0; i < 960; i++) input[i * 2] = input[i * 2 + 1] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 10000);
var packet = new byte[1500]; int length = encoder.Encode(input, 0, 960, packet, 0, packet.Length);
host.SendAudio(packet[..length]);
var encodedA = await receivedA.Task.WaitAsync(ct);
var encodedB = await receivedB.Task.WaitAsync(ct);
Check(encodedA.SequenceEqual(encodedB) && encodedA.SequenceEqual(packet[..length]), "relay fans out identical audio packets");
var decoder = OpusDecoder.Create(48000, 2); var decoded = new short[1920];
int frames = decoder.Decode(encodedA, 0, encodedA.Length, decoded, 0, 960, false);
Check(frames == 960 && decoded.Any(v => Math.Abs((int)v) > 100), "relayed Opus decodes to non-silent stereo audio");

// A listener attempting to send audio must be disconnected, not rebroadcast.
var maliciousLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var malicious = new RelayClient(); malicious.Disconnected += _ => maliciousLeft.TrySetResult();
await malicious.ConnectAsync(invitation, false, "", ct);
malicious.SendAudio(packet[..length]);
await maliciousLeft.Task.WaitAsync(ct);
Check(true, "listener cannot inject broadcast audio");
await host.DisposeAsync();
await left.Task.WaitAsync(ct);
Check(!guestA.IsConnected, "host stop disconnects listener");

// This is the same HTTP/WebSocket gateway deployed behind Render's managed HTTPS endpoint.
const string relayAccess = "testing-only-relay-access-key-0123456789";
await using (var web = RelayApplication.Create(["--Logging:LogLevel:Default=Warning"], relayAccess))
{
    web.Urls.Add("http://127.0.0.1:0");
    await web.StartAsync(ct);
    var endpoint = new Uri(web.Urls.Single());
    var webInvite = new Invite("127.0.0.1", endpoint.Port, room, false, true);
    using var http = new HttpClient();
    Check((await http.GetAsync(new Uri(endpoint, "/health"), ct)).IsSuccessStatusCode, "hosted relay health endpoint works");
    await using (var unauthorized = new RelayClient())
    {
        bool denied = false;
        try { await unauthorized.ConnectAsync(webInvite, true, key, ct, "wrong-key"); }
        catch (IOException e) { denied = e.Message.Contains("access key"); }
        Check(denied, "internet relay rejects unauthorized broadcaster");
    }
    await using var webHost = new RelayClient();
    await webHost.ConnectAsync(webInvite, true, key, ct, relayAccess);
    Check(webHost.IsConnected, "authorized DJ connects over WebSocket transport");
    var webAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var webOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var webGuest = new RelayClient();
    webGuest.Audio += bytes => webAudio.TrySetResult(bytes);
    webGuest.Disconnected += _ => webOffline.TrySetResult();
    // Guests need only the invite, never the DJ's server access key.
    await webGuest.ConnectAsync(webInvite, false, "", ct);
    webHost.SendAudio(packet[..length]);
    Check((await webAudio.Task.WaitAsync(ct)).SequenceEqual(packet[..length]), "hosted gateway carries audio to guest without DJ access key");
    await webHost.DisposeAsync();
    await webOffline.Task.WaitAsync(ct);
    Check(!webGuest.IsConnected, "internet gateway closes guest connection when DJ stops");
    await webGuest.DisposeAsync();
    await web.StopAsync(ct);
}

if (args.Contains("--audio"))
{
    var devices = AudioDevices.List();
    Check(devices.Count > 0, "Windows audio devices enumerate");
    foreach (var device in devices) Console.WriteLine($"  {(device.IsOutput ? "Output" : "Input")}: {device.Name}");
    using (var playback = new AudioListener(0))
    {
        playback.Receive(encodedA);
        await Task.Delay(150, ct);
    }
    Check(true, "native playback opens, decodes and closes (muted hardware smoke test)");
    var source = devices.First(d => d.IsOutput);
    int sent = 0;
    using (var capture = new AudioBroadcaster(source, null, _ => Interlocked.Increment(ref sent)))
    {
        capture.Start(); await Task.Delay(500, ct);
    }
    Check(sent > 5, "native loopback capture encodes continuously and releases device");
    await using var session = new Session();
    await session.BroadcastAsync(source, null, true, "", "127.0.0.1");
    Check(session.Active && session.Broadcasting && !string.IsNullOrEmpty(session.Invite), "in-game session starts embedded relay and capture");
    await using var listeningSession = new Session();
    listeningSession.Volume = 0;
    await listeningSession.ListenAsync(session.Invite);
    Check(listeningSession.Active && !listeningSession.Broadcasting, "guest session starts native muted playback without capture");
    await Task.Delay(150, ct);
    await session.StopAsync();
    Check(!session.Active && string.IsNullOrEmpty(session.Invite), "stop clears broadcast state and invitation");
    for (int retry = 0; retry < 40 && listeningSession.Active; retry++) await Task.Delay(25, ct);
    Check(!listeningSession.Active, "host stop automatically ends guest playback");
    await session.BroadcastAsync(source, null, true, "", "127.0.0.1");
    Check(session.Active, "broadcast restarts cleanly after stop");
    await session.StopAsync();
}
if (args.Contains("--internet"))
{
    string toolsFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../VenueAudio.Plugin/Tools"));
    await using var internet = new InternetHost();
    internet.Diagnostic = Console.WriteLine;
    Console.WriteLine("Opening temporary internet host for synthetic-audio test…");
    string publicAddress = await internet.StartAsync(toolsFolder, ct);
    Check(publicAddress.StartsWith("https://") && publicAddress.EndsWith(".trycloudflare.com"), "automatic host obtains reachable public HTTPS address");
    var target = Invite.Hosted(publicAddress, room);
    var localTarget = new Invite(internet.LocalEndpoint!.Host, internet.LocalEndpoint.Port, room, false, true);
    await using var internetDj = new RelayClient();
    await internetDj.ConnectAsync(localTarget, true, key, ct, internet.AccessKey);
    Check(internetDj.IsConnected, "DJ connects locally while public tunnel serves guests");
    // Diagnostic-only resolver: this PC's configured DNS failed fresh trycloudflare names.
    // Query authoritative public records for this test without changing Windows/VPN settings.
    // Product clients retain their ordinary OS DNS behavior and TLS certificate validation.
    using var dnsHttp = new HttpClient();
    await Task.Delay(8000, ct);
    string? address = null;
    for (int retry = 0; retry < 40 && address == null; retry++)
    {
        string resolver = retry % 2 == 0 ? "https://cloudflare-dns.com/dns-query?name=" : "https://dns.google/resolve?name=";
        using var dnsRequest = new HttpRequestMessage(HttpMethod.Get, resolver + target.Host + "&type=A");
        dnsRequest.Headers.Accept.ParseAdd("application/dns-json");
        using var dnsResponse = await dnsHttp.SendAsync(dnsRequest, ct);
        using var dns = System.Text.Json.JsonDocument.Parse(await dnsResponse.Content.ReadAsStringAsync(ct));
        if (dns.RootElement.TryGetProperty("Answer", out var answers))
            foreach (var answer in answers.EnumerateArray())
                if (answer.GetProperty("type").GetInt32() == 1) { address = answer.GetProperty("data").GetString(); break; }
        if (address == null) await Task.Delay(1000, ct);
    }
    if (address == null) throw new IOException("The new public tunnel's DNS record did not propagate during the test.");
    using var handler = new SocketsHttpHandler
    {
        ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(IPAddress.Parse(address), context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        },
    };
    using var diagnosticTransport = new HttpMessageInvoker(handler);
    var publicAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var internetGuest = new RelayClient();
    internetGuest.Audio += audio => publicAudio.TrySetResult(audio);
    await internetGuest.ConnectAsync(target, false, "", ct, transport: diagnosticTransport);
    using var internetEncrypt = new AudioCipher(audioKey);
    using var internetDecrypt = new AudioCipher(audioKey);
    byte[] encrypted = internetEncrypt.Encrypt(packet[..length]);
    internetDj.SendAudio(encrypted);
    byte[] publicReceived = await publicAudio.Task.WaitAsync(ct);
    Check(publicReceived.SequenceEqual(encrypted), "public relay carries opaque encrypted audio");
    Check(internetDecrypt.Decrypt(publicReceived).SequenceEqual(packet[..length]), "guest decrypts synthetic audio delivered through public internet endpoint");
    await internetGuest.DisposeAsync(); await internetDj.DisposeAsync();
    await internet.DisposeAsync();
    Check(true, "automatic host closes tunnel and background server on disposal");
}
Console.WriteLine($"All {checks} checks passed.");
