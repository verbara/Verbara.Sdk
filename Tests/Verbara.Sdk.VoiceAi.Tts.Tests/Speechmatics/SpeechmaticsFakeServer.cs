using System.Net;
using System.Text;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Speechmatics;

/// <summary>
/// In-process HTTP server that speaks the Speechmatics TTS REST wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// Accepts <c>POST /generate/{voice}</c> only, records the request JSON, the resolved voice
/// segment and the <c>Authorization</c> header, and replies with the caller-configured status code
/// and audio body. Anything else gets <c>404</c>, matching what the live API returns.
/// </para>
/// <para>
/// <b>Matching on method and path is the point, not a detail</b> (<c>Sdk/ADR-0048</c>, conformance
/// §3.12). This fake previously never inspected <c>Request.Url</c> and so answered any route, which
/// is why a suite that was fully green shipped a client whose every request returned <c>404</c>
/// against the real endpoint. A fake more permissive than the vendor cannot fail on a wrong route,
/// so it certifies one.
/// </para>
/// </remarks>
internal sealed class SpeechmaticsFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>The raw JSON body received from the client.</summary>
    public string? ReceivedRequestJson { get; private set; }

    /// <summary>The Authorization header value received from the client.</summary>
    public string? ReceivedAuthorization { get; private set; }

    /// <summary>The absolute path of the last request, whether or not it matched.</summary>
    public string? ReceivedPath { get; private set; }

    /// <summary>The HTTP method of the last request, whether or not it matched.</summary>
    public string? ReceivedMethod { get; private set; }

    /// <summary>
    /// The voice taken from the <c>/generate/{voice}</c> path segment, URL-decoded. Null when the
    /// request did not match the expected route.
    /// </summary>
    public string? ReceivedVoice { get; private set; }

    /// <summary>Number of requests that failed to match <c>POST /generate/{voice}</c>.</summary>
    public int UnmatchedRequestCount { get; private set; }

    /// <summary>HTTP status code to respond with. Defaults to 200.</summary>
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Audio bytes to return in the response body. Defaults to 640 B of silence.</summary>
    public byte[] ResponseAudio { get; set; } = new byte[640];

    /// <summary>Content-Type for the response. Defaults to <c>audio/wav</c>.</summary>
    public string ResponseContentType { get; set; } = "audio/wav";

    public int Port { get; }

    /// <summary>
    /// The server <b>origin</b>. The client appends <c>/generate/{voice}</c> itself, exactly as it
    /// does in production, so the route under test is the route that ships.
    /// </summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    public SpeechmaticsFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
        }

        if (_listener is null)
            throw new InvalidOperationException("Failed to allocate a port for the fake Speechmatics TTS server.");
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            ReceivedAuthorization = ctx.Request.Headers["Authorization"];
            ReceivedMethod = ctx.Request.HttpMethod;
            ReceivedPath = ctx.Request.Url?.AbsolutePath;

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                ReceivedRequestJson = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // Match on method AND path. A request that does not match gets 404 — the same answer
            // the live API gives — instead of being served anyway.
            var voice = MatchGenerateRoute(ctx.Request.HttpMethod, ReceivedPath);
            if (voice is null)
            {
                UnmatchedRequestCount++;
                ReceivedVoice = null;
                var notFound = Encoding.UTF8.GetBytes("{\"detail\":\"Not Found\"}");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = notFound.Length;
                await ctx.Response.OutputStream.WriteAsync(notFound.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }

            ReceivedVoice = voice;
            ctx.Response.StatusCode = (int)ResponseStatus;
            if (ResponseStatus == HttpStatusCode.OK)
            {
                ctx.Response.ContentType = ResponseContentType;
                ctx.Response.ContentLength64 = ResponseAudio.Length;
                await ctx.Response.OutputStream.WriteAsync(ResponseAudio.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                var body = Encoding.UTF8.GetBytes($"{{\"error\":\"{ResponseStatus}\"}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }

            ctx.Response.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    /// <summary>
    /// Returns the URL-decoded voice when <paramref name="method"/> and <paramref name="path"/>
    /// match <c>POST /generate/{voice}</c>, and null otherwise. Bare <c>/generate</c> does not
    /// match: that is the route the shipped client used, and the live API answers it <c>404</c>.
    /// </summary>
    private static string? MatchGenerateRoute(string method, string? path)
    {
        if (!string.Equals(method, "POST", StringComparison.Ordinal) || path is null)
            return null;

        const string Prefix = "/generate/";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var segment = path[Prefix.Length..];
        if (segment.Length == 0 || segment.Contains('/', StringComparison.Ordinal))
            return null;

        return Uri.UnescapeDataString(segment);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
