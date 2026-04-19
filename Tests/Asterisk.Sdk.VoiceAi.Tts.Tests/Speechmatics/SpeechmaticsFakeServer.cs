using System.Net;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Speechmatics;

/// <summary>
/// In-process HTTP server that speaks the Speechmatics TTS REST wire protocol.
/// </summary>
/// <remarks>
/// Accepts <c>POST /generate</c>, records the request JSON + <c>Authorization</c>
/// header, and replies with the caller-configured status code and audio body.
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

    /// <summary>HTTP status code to respond with. Defaults to 200.</summary>
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Audio bytes to return in the response body. Defaults to 640 B of silence.</summary>
    public byte[] ResponseAudio { get; set; } = new byte[640];

    /// <summary>Content-Type for the response. Defaults to <c>audio/wav</c>.</summary>
    public string ResponseContentType { get; set; } = "audio/wav";

    public int Port { get; }

    public string BaseUri => $"http://localhost:{Port}/generate";

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
            listener.Prefixes.Add($"http://localhost:{port}/");
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

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                ReceivedRequestJson = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

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
