using System.Globalization;
using System.Net;

namespace Dotmim.Sync.Samples.Resume.Client;

/// <summary>
/// HttpMessageHandler that simulates network failures deterministically. The demo
/// drives this with a small DSL of "fault rules" so each test scenario can fail
/// at a chosen point in the request stream and be repeated reproducibly.
/// </summary>
internal enum FaultMode
{
    /// <summary>Pass through normally.</summary>
    None = 0,

    /// <summary>Throw <see cref="HttpRequestException"/> before the request even hits the wire.</summary>
    NetworkException,

    /// <summary>Return a 500 status code with no body.</summary>
    ServerError,

    /// <summary>Throw <see cref="TaskCanceledException"/> to simulate a client-side timeout.</summary>
    Timeout,

    /// <summary>Pass through, but cancel the response body stream halfway through.</summary>
    TruncateBody,
}

/// <summary>
/// Describes one synthetic fault: "after the Nth eligible request whose URL contains
/// <see cref="UrlContains"/>, fail with <see cref="Mode"/>". When the fault has fired,
/// it disarms automatically (<see cref="Triggered"/> goes true) so subsequent requests
/// flow through. That's the property the resume engine needs: the *next* sync attempt
/// must succeed.
/// </summary>
internal sealed class FaultRule
{
    public FaultMode Mode { get; init; }
    public int RequestIndex { get; init; }      // 1-based; trigger on the Nth matching request
    public string? UrlContains { get; init; }   // optional substring filter
    public string? StepHeaderEquals { get; init; }  // optional dotmim-sync-step header filter
    public bool Triggered { get; private set; }

    internal void MarkTriggered() => this.Triggered = true;
}

/// <summary>
/// HttpMessageHandler that wraps an inner handler and applies a list of <see cref="FaultRule"/>s.
/// Also keeps a request-by-request log so the test menu can show exactly what happened
/// on the wire.
/// </summary>
internal sealed class FaultInjectingHandler : DelegatingHandler
{
    private readonly object _gate = new();
    private readonly List<FaultRule> _rules = [];
    private readonly List<string> _log = [];
    private int _matchedCounter;

    public FaultInjectingHandler(HttpMessageHandler inner)
        : base(inner) { }

    /// <summary>Total HTTP requests this handler has seen (matched or not).</summary>
    public int TotalRequests { get; private set; }

    /// <summary>Total faults injected since reset.</summary>
    public int TotalFaultsInjected { get; private set; }

    public IReadOnlyList<FaultRule> Rules
    {
        get
        {
            lock (this._gate) return [.. this._rules];
        }
    }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (this._gate) return [.. this._log];
        }
    }

    /// <summary>Removes every armed rule and clears the request log.</summary>
    public void Reset()
    {
        lock (this._gate)
        {
            this._rules.Clear();
            this._log.Clear();
            this._matchedCounter = 0;
            this.TotalRequests = 0;
            this.TotalFaultsInjected = 0;
        }
    }

    public void Arm(FaultRule rule)
    {
        lock (this._gate) this._rules.Add(rule);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        FaultMode mode = FaultMode.None;
        FaultRule? armed = null;
        string url = request.RequestUri?.ToString() ?? "<no-uri>";
        string? step = TryGetHeader(request, "dotmim-sync-step");

        lock (this._gate)
        {
            this.TotalRequests++;

            foreach (var rule in this._rules)
            {
                if (rule.Triggered) continue;
                if (rule.UrlContains is not null && !url.Contains(rule.UrlContains, StringComparison.OrdinalIgnoreCase)) continue;
                if (rule.StepHeaderEquals is not null && step != rule.StepHeaderEquals) continue;

                this._matchedCounter++;
                if (this._matchedCounter < rule.RequestIndex) break; // not yet

                rule.MarkTriggered();
                mode = rule.Mode;
                armed = rule;
                this._matchedCounter = 0;
                this.TotalFaultsInjected++;
                break;
            }
        }

        if (armed is not null)
        {
            var faultDescription = string.Format(
                CultureInfo.InvariantCulture,
                "REQ #{0} step={1} -> INJECT {2}",
                this.TotalRequests, step ?? "?", mode);
            this.AppendLog(faultDescription);

            return mode switch
            {
                FaultMode.NetworkException => throw new HttpRequestException(
                    "Simulated network failure (resume demo)."),

                FaultMode.Timeout => throw new TaskCanceledException(
                    "Simulated client-side timeout (resume demo).",
                    new TimeoutException()),

                FaultMode.ServerError => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = new StringContent("Simulated server error (resume demo)."),
                },

                FaultMode.TruncateBody => await this.SendTruncatedAsync(request, cancellationToken).ConfigureAwait(false),

                _ => await base.SendAsync(request, cancellationToken).ConfigureAwait(false),
            };
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        this.AppendLog(string.Format(
            CultureInfo.InvariantCulture,
            "REQ #{0} step={1} -> {2}",
            this.TotalRequests, step ?? "?", (int)response.StatusCode));
        return response;
    }

    private async Task<HttpResponseMessage> SendTruncatedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Send the request normally, then return a response whose body stream
        // throws halfway through being read. The sync engine surfaces this as a
        // failure during the streaming-batch deserialization, which is exactly
        // the partial-download case we want to test.
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content is null) return response;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var halfway = bytes.Length / 2;
        var truncated = bytes[..halfway];

        var newResponse = new HttpResponseMessage(response.StatusCode) { RequestMessage = request };
        foreach (var header in response.Headers)
            newResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);

        newResponse.Content = new ByteArrayContent(truncated);
        foreach (var header in response.Content.Headers)
            newResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Override the Content-Length so the client expects the full payload but
        // never gets it — that mismatch is what triggers a hash failure / EOF.
        newResponse.Content.Headers.ContentLength = bytes.Length;
        return newResponse;
    }

    private void AppendLog(string entry)
    {
        lock (this._gate) this._log.Add(entry);
    }

    private static string? TryGetHeader(HttpRequestMessage req, string headerName)
        => req.Headers.TryGetValues(headerName, out var values) ? values.FirstOrDefault() : null;
}
