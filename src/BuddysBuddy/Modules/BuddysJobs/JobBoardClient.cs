using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuddysBuddy.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuddysBuddy.Modules.BuddysJobs;

/// <summary>The outcome of one action against the board.</summary>
/// <param name="Ok">Whether the board accepted it.</param>
/// <param name="Message">Plain English, suitable for showing to the player either way.</param>
internal readonly record struct JobResult(bool Ok, string Message);

/// <summary>
/// Talks to the jobs board.
/// </summary>
/// <remarks>
/// Every call here runs off the game thread and nothing in it may ever be awaited from a framework
/// tick — a board that has gone away would otherwise freeze the game for the request timeout. The
/// UI never calls the network directly: it reads <see cref="State"/>, which is a snapshot swapped in
/// wholesale by the poll loop, and fires actions that complete in the background.
///
/// The client is deliberately incurious about what it gets back. The server owns job state, so a
/// reply is applied as given rather than merged into what was there before; anything else invents a
/// second source of truth for a shared list, which is how two people end up seeing different boards.
/// </remarks>
internal sealed class JobBoardClient : IDisposable
{
    private const int ProtocolVersion = 1;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a failure holds off the next poll, so a dead board is not hammered.</summary>
    private static readonly TimeSpan BackoffAfterError = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<BuddysJobsConfig> _settings;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private BoardState _state = BoardState.Empty;

    public JobBoardClient(Func<BuddysJobsConfig> settings)
    {
        _settings = settings;

        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BuddysBuddy", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The board as last read. Safe to call from the draw thread.</summary>
    public BoardState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>True once a server and a board key are both set. Nothing leaves the machine before that.</summary>
    public bool IsConfigured
    {
        get
        {
            var settings = _settings();
            return !string.IsNullOrWhiteSpace(settings.ServerUrl) && !string.IsNullOrWhiteSpace(settings.BoardKey);
        }
    }

    // ── The poll loop ────────────────────────────────────────────────────────

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoop(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing to stop.
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var settings = _settings();
            var wait = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 15, 3600));

            if (IsConfigured && settings.AutoRefresh)
            {
                var ok = await Refresh(token).ConfigureAwait(false);
                if (!ok && wait < BackoffAfterError)
                    wait = BackoffAfterError;
            }

            try
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Asks the server whether it is there, without a board key.
    /// </summary>
    /// <remarks>
    /// The point of a separate unauthenticated endpoint is that "the server is down" and "my board
    /// key is wrong" otherwise produce the same silence. Pinging first, then reading the board,
    /// tells the two apart — which is the whole difference between a five-minute fix and an evening.
    /// </remarks>
    public async Task<JobResult> Ping()
    {
        var settings = _settings();

        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
            return new JobResult(false, "No server address set.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(settings.ServerUrl), "v1/health"));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var body = await ReadJson(response).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new JobResult(false, $"The server answered {(int)response.StatusCode} — is the address right?");

            var protocol = body["protocol"]?.Value<int>() ?? 0;
            if (protocol == 0)
                return new JobResult(false, "Something answered, but it is not a jobs board.");

            if (protocol > ProtocolVersion)
                return new JobResult(true, $"Reachable, but it speaks protocol {protocol} and this plugin speaks {ProtocolVersion}.");

            return new JobResult(true, "The server is up. Checking the board key…");
        }
        catch (Exception ex)
        {
            return new JobResult(false, Describe(ex));
        }
    }

    /// <summary>Reads the whole board. Returns false if it could not be read.</summary>
    public async Task<bool> Refresh(CancellationToken token = default)
    {
        if (!IsConfigured)
        {
            Update(s => s with { LastError = "No server or board key set.", Busy = false });
            return false;
        }

        Update(s => s with { Busy = true });

        try
        {
            using var response = await Send(HttpMethod.Get, "v1/jobs", null, token).ConfigureAwait(false);
            var payload = await ReadJson(response).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Update(s => s with { LastError = Explain(response.StatusCode, payload), Busy = false });
                return false;
            }

            var jobs = payload["jobs"]?.ToObject<List<Job>>() ?? new List<Job>();
            var protocol = payload["protocol"]?.Value<int>() ?? ProtocolVersion;

            var warning = protocol > ProtocolVersion
                ? "The board is running a newer protocol than this plugin — update Buddy's Buddy."
                : null;

            Update(_ => new BoardState(jobs, DateTime.Now, warning, false));
            return true;
        }
        catch (Exception ex)
        {
            Update(s => s with { LastError = Describe(ex), Busy = false });
            DalamudServices.Log.Debug(ex, "Reading the jobs board failed.");
            return false;
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    public Task<JobResult> Post(uint itemId, string itemName, int quantity, bool hq, string reward, string note, string actor)
        => Act("v1/jobs", new JObject
        {
            ["itemId"] = itemId,
            ["itemName"] = itemName,
            ["quantity"] = quantity,
            ["hq"] = hq,
            ["reward"] = reward,
            ["note"] = note,
            ["actor"] = actor,
        }, $"Posted {quantity:N0}x {itemName}.");

    public Task<JobResult> Claim(Job job, string actor)
        => Act($"v1/jobs/{job.Id}/claim", Actor(actor), $"Claimed {job.ItemName}.");

    public Task<JobResult> Release(Job job, string actor)
        => Act($"v1/jobs/{job.Id}/release", Actor(actor), $"Put {job.ItemName} back on the board.");

    public Task<JobResult> Cancel(Job job, string actor)
        => Act($"v1/jobs/{job.Id}/cancel", Actor(actor), $"Cancelled {job.ItemName}.");

    public Task<JobResult> Complete(Job job, int amount, string actor)
        => Act($"v1/jobs/{job.Id}/complete", new JObject
        {
            ["actor"] = actor,
            ["amount"] = amount,
        }, $"Delivered {amount:N0}x {job.ItemName}.");

    private static JObject Actor(string actor) => new() { ["actor"] = actor };

    /// <summary>
    /// Posts one action and re-reads the board.
    /// </summary>
    /// <remarks>
    /// Always re-reads, including after a rejection: being told the job was already taken means the
    /// local list is stale, and that is exactly the moment the player most needs it to be right.
    /// </remarks>
    private async Task<JobResult> Act(string path, JObject payload, string success)
    {
        if (!IsConfigured)
            return new JobResult(false, "No server or board key set.");

        try
        {
            Update(s => s with { Busy = true });

            using var response = await Send(HttpMethod.Post, path, payload, CancellationToken.None)
                .ConfigureAwait(false);

            var body = await ReadJson(response).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var message = Explain(response.StatusCode, body);
                Update(s => s with { LastError = message, Busy = false });
                await Refresh().ConfigureAwait(false);
                return new JobResult(false, message);
            }

            await Refresh().ConfigureAwait(false);
            return new JobResult(true, success);
        }
        catch (Exception ex)
        {
            var message = Describe(ex);
            Update(s => s with { LastError = message, Busy = false });
            DalamudServices.Log.Debug(ex, "A jobs board action failed.");
            return new JobResult(false, message);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, JObject? payload, CancellationToken token)
    {
        var settings = _settings();
        var request = new HttpRequestMessage(method, new Uri(BaseUri(settings.ServerUrl), path));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BoardKey.Trim());

        if (payload is not null)
            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

        return await _http.SendAsync(request, token).ConfigureAwait(false);
    }

    /// <summary>Normalises the configured server into something <see cref="Uri"/> can combine with a path.</summary>
    private static Uri BaseUri(string url)
    {
        var trimmed = url.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!trimmed.EndsWith('/'))
            trimmed += "/";

        return new Uri(trimmed);
    }

    /// <summary>Reads a JSON body, tolerating one that is not JSON at all.</summary>
    private static async Task<JObject> ReadJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        try
        {
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }
        catch (JsonException)
        {
            // A proxy or a captive portal answering instead of the board — keep enough to diagnose it.
            return new JObject { ["error"] = text.Length > 200 ? text[..200] : text };
        }
    }

    /// <summary>
    /// Turns a failed response into something worth reading.
    /// </summary>
    /// <remarks>
    /// The status codes carry real meaning here and are worth translating rather than printing:
    /// 401 is nearly always a mistyped board key, and 409 is the one everybody will actually hit —
    /// someone else claimed the job first.
    /// </remarks>
    private static string Explain(HttpStatusCode status, JObject body)
    {
        var detail = body["error"]?.Value<string>();
        var who = body["by"]?.Value<string>();

        return status switch
        {
            HttpStatusCode.Unauthorized => "The board key was rejected — check it for typos.",
            HttpStatusCode.Conflict when !string.IsNullOrEmpty(who) => $"Already taken by {who}.",
            HttpStatusCode.Conflict => detail ?? "Somebody got there first.",
            HttpStatusCode.Forbidden => detail ?? "That is not yours to change.",
            HttpStatusCode.NotFound => "That job is no longer on the board.",
            HttpStatusCode.TooManyRequests => detail ?? "The board is full.",
            _ => detail ?? $"The board answered {(int)status} {status}.",
        };
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "The board did not answer in time.",
        HttpRequestException http => $"Could not reach the board: {http.Message}",
        UriFormatException => "The server address is not a valid URL.",
        _ => ex.Message,
    };

    private void Update(Func<BoardState, BoardState> change)
    {
        lock (_gate)
            _state = change(_state);
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
