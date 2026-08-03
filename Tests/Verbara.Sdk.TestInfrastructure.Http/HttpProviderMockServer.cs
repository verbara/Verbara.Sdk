using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WireMock.Logging;
using WireMock.Server;
using WireMock.Settings;

namespace Verbara.Sdk.TestInfrastructure.Http;

/// <summary>
/// Shared WireMock-backed HTTP server for the request/response provider suites (ADR-0041 D1) —
/// loopback-bound, strict-matching by default, and driven by recorded provider responses.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the two divergent copies of <c>MockHttpMessageHandler</c> and the <c>HttpListener</c>
/// fakes on the six HTTP-transport surfaces. Unlike a canned handler it matches on the request, so
/// a misrouted or unauthenticated call gets a 404 instead of the success body — see
/// <see cref="HttpProviderRequest"/>. The WebSocket streaming providers stay on
/// <see cref="WebSocket.WebSocketTestServer"/> (ADR-0041 D2); this is not a staging decision.
/// </para>
/// <para>
/// <see cref="BaseAddress"/> always names the IPv4 literal <c>127.0.0.1</c>. WireMock's own
/// <c>Url</c> property reports the host as <c>localhost</c>, which resolves <c>::1</c> first and is
/// the exact ambiguity ADR-0044 removed from the fake-server seams — so it is never surfaced here.
/// </para>
/// <para>
/// This type is not thread-safe: register every stub before the client under test starts issuing
/// requests, which is how the provider suites are written.
/// </para>
/// </remarks>
public sealed class HttpProviderMockServer : IAsyncDisposable
{
    /// <summary>Free-port attempts before giving up, mirroring the existing in-process fakes.</summary>
    private const int PortAllocationAttempts = 5;

    /// <summary>
    /// Budget for Kestrel to report "started". A cold start measures ~80 ms, so this is generous —
    /// but it is also what a lost port race costs, because WireMock only surfaces a bind failure
    /// once the budget expires. Hence a low value plus retries rather than the 10 s default.
    /// </summary>
    private const int StartTimeoutMilliseconds = 5000;

    private readonly WireMockServer _server;
    private readonly ProviderRecordings? _recordings;
    private int _sequenceCount;
    private bool _disposed;

    private HttpProviderMockServer(WireMockServer server, int port, ProviderRecordings? recordings)
    {
        _server = server;
        _recordings = recordings;
        Port = port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
    }

    /// <summary>The loopback TCP port the server is listening on.</summary>
    public int Port { get; }

    /// <summary>Base address clients under test should dial — always the IPv4 loopback literal.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Every request the server saw, matched or not, in arrival order.</summary>
    public IReadOnlyList<ReceivedHttpRequest> ReceivedRequests =>
        _server.LogEntries.Select(ReceivedHttpRequest.From).ToList();

    /// <summary>
    /// Requests that matched no stub. A non-empty list after an otherwise green assertion usually
    /// means the client dialled the wrong URL or dropped a required header.
    /// </summary>
    public IReadOnlyList<ReceivedHttpRequest> UnmatchedRequests =>
        _server.LogEntries.Where(e => e.MappingGuid is null).Select(ReceivedHttpRequest.From).ToList();

    /// <summary>
    /// Start a server on a free loopback port, retrying on a lost port race under parallel test
    /// execution. <paramref name="recordings"/> overrides capture discovery; when omitted the
    /// suite's <c>Recordings/</c> folder is located lazily on first use.
    /// </summary>
    public static HttpProviderMockServer Start(ProviderRecordings? recordings = null)
    {
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < PortAllocationAttempts; attempt++)
        {
            var port = FreeLoopbackPort();
            try
            {
                var server = WireMockServer.Start(new WireMockServerSettings
                {
                    Urls = [$"http://127.0.0.1:{port}/"],
                    StartAdminInterface = false,
                    // Never answer a near-miss with the closest stub — an unmatched request is a
                    // 404, which is the whole point of the strict contract (ADR-0041 D1).
                    AllowPartialMapping = false,
                    StartTimeout = StartTimeoutMilliseconds,
                });

                return new HttpProviderMockServer(server, port, recordings);
            }
            catch (Exception ex)
            {
                // Another process took the port between the probe and the bind; try a new one.
                lastFailure = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to bind a free loopback port for {nameof(HttpProviderMockServer)} after " +
            $"{PortAllocationAttempts} attempts.",
            lastFailure);
    }

    /// <summary>An <see cref="HttpClient"/> already pointed at <see cref="BaseAddress"/>.</summary>
    public HttpClient CreateClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new HttpClient { BaseAddress = BaseAddress };
    }

    /// <summary>Answer <paramref name="request"/> with <paramref name="response"/>.</summary>
    public HttpProviderMockServer Stub(HttpProviderRequest request, HttpProviderResponse response)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        _server.Given(request.BuildMatcher()).RespondWith(response.Build());
        return this;
    }

    /// <summary>
    /// Answer successive calls to <paramref name="request"/> with successive responses — the shape
    /// a canned handler cannot express at all. The last response is sticky, so a retry policy that
    /// keeps going past the end of the sequence sees the terminal outcome rather than wrapping
    /// back to the first.
    /// </summary>
    public HttpProviderMockServer StubSequence(HttpProviderRequest request, params HttpProviderResponse[] responses)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responses);

        if (responses.Length == 0)
            throw new ArgumentException("A response sequence needs at least one response.", nameof(responses));

        if (responses.Length == 1)
            return Stub(request, responses[0]);

        var scenario = "sequence-" + Interlocked.Increment(ref _sequenceCount).ToString(CultureInfo.InvariantCulture);

        for (var step = 0; step < responses.Length; step++)
        {
            var provider = _server.Given(request.BuildMatcher()).InScenario(scenario);

            // Step 0 answers the scenario's initial (unset) state, so it declares no prior state.
            if (step > 0)
                provider = provider.WhenStateIs(StateName(step));

            var isLast = step == responses.Length - 1;
            provider = provider.WillSetStateTo(StateName(isLast ? step : step + 1));
            provider.RespondWith(responses[step].Build());
        }

        return this;
    }

    /// <summary>Answer <paramref name="request"/> with a captured JSON body — the STT shape.</summary>
    public HttpProviderMockServer StubRecordedJson(
        HttpProviderRequest request,
        string recordingPath,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        Stub(request, HttpProviderResponse.Json(Recordings.ReadText(recordingPath), statusCode));

    /// <summary>Answer <paramref name="request"/> with a captured byte stream — the TTS shape.</summary>
    public HttpProviderMockServer StubRecordedBytes(
        HttpProviderRequest request,
        string recordingPath,
        string contentType,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        Stub(request, HttpProviderResponse.Bytes(Recordings.ReadBytes(recordingPath), contentType, statusCode));

    /// <summary>Stop the listener and release the port.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _server.Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Captures for this suite, discovered on first use so suites without any can still run.</summary>
    private ProviderRecordings Recordings => _recordings ?? ProviderRecordings.Locate();

    private static string StateName(int step) => "step-" + step.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Ask the kernel for an unused loopback port, then release it. The window between the release
    /// and WireMock's bind is what <see cref="PortAllocationAttempts"/> covers.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>
/// A request the server received, in the shape the provider suites assert on — the replacement for
/// <c>MockHttpMessageHandler.LastRequest</c>.
/// </summary>
public sealed record ReceivedHttpRequest(
    string Method,
    string Path,
    string? RawQuery,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
    string? BodyAsString,
    byte[]? BodyAsBytes,
    bool Matched)
{
    /// <summary>The first value of <paramref name="name"/>, or <see langword="null"/> if absent.</summary>
    public string? Header(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
    }

    internal static ReceivedHttpRequest From(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var message = entry.RequestMessage;
        if (message is null)
            throw new InvalidOperationException("WireMock produced a log entry without a request message.");

        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (message.Headers is not null)
        {
            foreach (var (name, values) in message.Headers)
                headers[name] = values.ToArray();
        }

        return new ReceivedHttpRequest(
            message.Method,
            message.Path,
            message.RawQuery,
            headers,
            message.Body,
            message.BodyAsBytes,
            entry.MappingGuid is not null);
    }
}
