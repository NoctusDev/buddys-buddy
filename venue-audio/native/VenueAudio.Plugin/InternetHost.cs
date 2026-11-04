using System.Diagnostics;
using System.Security.Cryptography;

namespace VenueAudio;

/// <summary>Starts a private local gateway plus temporary public tunnel, with no console windows.</summary>
public sealed class InternetHost : IAsyncDisposable
{
    private Process? process;
    public Action<string>? Diagnostic { get; set; }
    public Uri? LocalEndpoint { get; private set; }
    public string AccessKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public async Task<string> StartAsync(string toolsFolder, CancellationToken ct)
    {
        string gateway = Path.Combine(toolsFolder, "Relay", "VenueAudio.WebRelay.exe");
        string tunnel = Path.Combine(toolsFolder, "cloudflared.exe");
        if (!File.Exists(gateway) || !File.Exists(tunnel)) throw new IOException("Internet hosting helpers are missing. Use the complete House Lights test folder.");
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo(gateway)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(gateway)!,
        };
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["VENUE_BROADCAST_KEY"] = AccessKey;
        start.Environment["VENUE_QUICK_TUNNEL"] = "1";
        start.Environment["VENUE_TUNNEL_EXE"] = tunnel;
        start.Environment["VENUE_PARENT_PID"] = Environment.ProcessId.ToString();
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            const string localMarker = "VENUE_LOCAL_READY=";
            if (e.Data?.StartsWith(localMarker, StringComparison.Ordinal) == true) LocalEndpoint = new Uri(e.Data[localMarker.Length..]);
            const string marker = "VENUE_INTERNET_READY=";
            if (e.Data?.StartsWith(marker, StringComparison.Ordinal) == true) ready.TrySetResult(e.Data[marker.Length..]);
        };
        // Drain errors, but do not forward process output or room credentials into game logs.
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => ready.TrySetException(new IOException("The internet host stopped before it was ready. Check your internet connection and try again."));
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        string endpoint = await ready.Task.WaitAsync(timeout.Token);
        Diagnostic?.Invoke("Temporary address: " + endpoint);
        // Cloudflared confirms the edge connection. The DJ uses its local gateway, avoiding
        // an unnecessary round-trip (and dependence on the DJ's DNS resolving a fresh hostname).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (LocalEndpoint == null) throw new IOException("The internet host did not report its local connection.");
        using var response = await http.GetAsync(new Uri(LocalEndpoint, "/health"), timeout.Token);
        response.EnsureSuccessStatusCode();
        return endpoint;
    }
    public async ValueTask DisposeAsync()
    {
        if (process == null) return;
        if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        process.Dispose(); process = null;
    }
}
