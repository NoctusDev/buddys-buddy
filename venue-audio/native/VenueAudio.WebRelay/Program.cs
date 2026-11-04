using VenueAudio.WebRelay;
using System.Diagnostics;
using System.Text.RegularExpressions;

await using var app = RelayApplication.Create(args);
await app.StartAsync();
Console.WriteLine("VENUE_LOCAL_READY=" + app.Urls.Single());
Process? tunnel = null;
try
{
    if (Environment.GetEnvironmentVariable("VENUE_QUICK_TUNNEL") == "1")
    {
        string executable = Environment.GetEnvironmentVariable("VENUE_TUNNEL_EXE") ?? throw new InvalidOperationException("Tunnel helper is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--protocol", "http2", "--url", app.Urls.Single() }) start.ArgumentList.Add(argument);
        tunnel = new Process { StartInfo = start, EnableRaisingEvents = true };
        bool announced = false, connected = false;
        string? publicAddress = null;
        object announcementGate = new();
        void ReadLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (announcementGate)
            {
                if (announced) return;
                var match = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (match.Success) publicAddress = match.Value;
                if (e.Data.Contains("Registered tunnel connection", StringComparison.Ordinal)) connected = true;
                if (connected && publicAddress != null) { announced = true; Console.WriteLine("VENUE_INTERNET_READY=" + publicAddress); }
            }
        }
        tunnel.OutputDataReceived += ReadLine; tunnel.ErrorDataReceived += ReadLine;
        tunnel.Exited += (_, _) => app.Lifetime.StopApplication();
        tunnel.Start(); tunnel.BeginOutputReadLine(); tunnel.BeginErrorReadLine();
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("VENUE_PARENT_PID"), out var parentId))
    {
        _ = Task.Run(async () =>
        {
            try { using var parent = Process.GetProcessById(parentId); await parent.WaitForExitAsync(); }
            catch (ArgumentException) { }
            app.Lifetime.StopApplication();
        });
    }
    await app.WaitForShutdownAsync();
}
finally
{
    if (tunnel != null)
    {
        if (!tunnel.HasExited) { tunnel.Kill(true); await tunnel.WaitForExitAsync(); }
        tunnel.Dispose();
    }
}
