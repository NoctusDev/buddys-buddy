using System.Net;
using System.Security.Cryptography.X509Certificates;
using VenueAudio.Core;

var certificatePath = Environment.GetEnvironmentVariable("VENUE_TLS_PFX");
using var certificate = string.IsNullOrWhiteSpace(certificatePath) ? null :
    X509CertificateLoader.LoadPkcs12FromFile(certificatePath, Environment.GetEnvironmentVariable("VENUE_TLS_PASSWORD"));
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var requested) ? requested : Wire.Port;
await using var relay = new RelayServer(IPAddress.Any, port, certificate);
Console.WriteLine($"Venue Audio relay listening on {relay.Port}; TLS: {certificate != null}");
var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.TrySetResult();
await quit.Task;
