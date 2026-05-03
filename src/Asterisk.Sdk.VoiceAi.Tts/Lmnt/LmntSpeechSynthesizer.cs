using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Lmnt;

/// <summary>
/// LMNT TTS provider supporting WebSocket streaming (preferred, sub-200 ms TTFA)
/// and HTTP POST fallback. Selection via <see cref="LmntTtsOptions.Transport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WebSocket path:</strong> connects to <c>wss://api.lmnt.com/v1/ai/speech/stream</c>.
/// Authentication is sent as <c>X-API-Key</c> inside the first JSON message body
/// (not in the HTTP upgrade headers — LMNT requirement).
/// The client sends an init message, optional text messages, a flush command, and then
/// an EOF command. The server responds with binary audio frames interleaved with JSON
/// notification messages.
/// </para>
/// <para>
/// <strong>HTTP path:</strong> POSTs to <c>https://api.lmnt.com/v1/ai/speech/generate</c>
/// with form-encoded body fields. Authentication via the <c>X-API-Key</c> header.
/// The response body is streamed in chunks to keep memory bounded.
/// </para>
/// </remarks>
public sealed class LmntSpeechSynthesizer : SpeechSynthesizer
{
    private const int HttpChunkSize = 8192;

    private readonly LmntTtsOptions _options;
    private readonly int? _fakeWsPort;
    private readonly string? _fakeHttpBaseUri;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public override string ProviderName => "LMNT";

    /// <summary>Initializes a new instance for production use.</summary>
    public LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options)
    {
        _options = options.Value;
        if (_options.Transport == LmntTransport.Http)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds),
            };
            _ownsHttpClient = true;
        }
    }

    /// <summary>Initializes a new instance for testing with a fake WebSocket server.</summary>
    internal LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options, int fakeWsPort)
    {
        _options = options.Value;
        _fakeWsPort = fakeWsPort;
    }

    /// <summary>Initializes a new instance for testing with a fake HTTP server.</summary>
    internal LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options, HttpClient httpClient, string fakeHttpBaseUri)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _fakeHttpBaseUri = fakeHttpBaseUri;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_options.Transport == LmntTransport.Http || _fakeHttpBaseUri is not null)
        {
            await foreach (var chunk in SynthesizeHttpAsync(text, outputFormat, ct).ConfigureAwait(false))
                yield return chunk;
        }
        else
        {
            await foreach (var frame in SynthesizeWebSocketAsync(text, outputFormat, ct).ConfigureAwait(false))
                yield return frame;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WebSocket path
    // ──────────────────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeWebSocketAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var uri = BuildWsUri();
        using var ws = new ClientWebSocket();

        // NOTE: LMNT auth is NOT sent in HTTP upgrade headers — the X-API-Key field
        // is sent as part of the first JSON message body (see LmntInitMessage).
        // No request headers are set on ws.Options; auth is embedded in the first JSON frame.

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: receive loop cancels session when server closes/aborts.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sendTask = SendWsRequestAsync(ws, text, outputFormat, sessionCts.Token);

        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveWsFramesAsync(ws, channel.Writer, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, ct);

        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;

        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private async Task SendWsRequestAsync(
        ClientWebSocket ws,
        string text,
        AudioFormat outputFormat,
        CancellationToken ct)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;

        // Init message — must include X-API-Key, voice, format.
        // LMNT auth is embedded here, not in HTTP upgrade headers.
        var init = new LmntInitMessage
        {
            ApiKey = _options.ApiKey,
            Voice = _options.Voice,
            Format = _options.Format,
            SampleRate = sampleRate,
            Language = _options.Language,
            Speed = _options.Speed,
            Model = _options.Model,
        };

        var initJson = JsonSerializer.Serialize(init, VoiceAiTtsJsonContext.Default.LmntInitMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(initJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Text message.
        var textMsg = new LmntTextMessage { Text = text };
        var textJson = JsonSerializer.Serialize(textMsg, VoiceAiTtsJsonContext.Default.LmntTextMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(textJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Flush command — signals end of current utterance, prompts server to emit buffered audio.
        // Schema {"flush":true} matches LMNT Python SDK; verify against live API at integration test time.
        var flushJson = JsonSerializer.Serialize(LmntFlushMessage.Instance, VoiceAiTtsJsonContext.Default.LmntFlushMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(flushJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // EOF command — tells server no more input is coming; triggers final audio + close.
        var eofJson = JsonSerializer.Serialize(LmntEofMessage.Instance, VoiceAiTtsJsonContext.Default.LmntEofMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(eofJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Half-close: guarded timeout to avoid hanging when the server already closed.
        if (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            closeCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* server is gone, give up */ }
            catch (WebSocketException) { /* peer already closed abruptly */ }
        }
    }

    private static async Task ReceiveWsFramesAsync(
        ClientWebSocket ws,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
        CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = new byte[result.Count];
                buf.AsSpan(0, result.Count).CopyTo(frame);
                await writer.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // LMNT may send JSON notifications (e.g. buffer_empty, error).
                // A buffer_empty or finish notification terminates the stream.
                var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                var notification = JsonSerializer.Deserialize(
                    json,
                    VoiceAiTtsJsonContext.Default.LmntServerNotification);
                if (notification is not null &&
                    (string.Equals(notification.Type, "finish", StringComparison.Ordinal) ||
                     string.Equals(notification.Error, "error", StringComparison.Ordinal) ||
                     string.Equals(notification.Type, "error", StringComparison.Ordinal)))
                {
                    break;
                }
                // buffer_empty: server flushed buffered audio; continue receiving.
            }
        }
    }

    private Uri BuildWsUri()
    {
        if (_fakeWsPort.HasValue)
            return new Uri($"ws://localhost:{_fakeWsPort}/v1/ai/speech/stream");

        return new Uri("wss://api.lmnt.com/v1/ai/speech/stream");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HTTP path
    // ──────────────────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeHttpAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;

        // LMNT HTTP POST accepts application/x-www-form-urlencoded body fields.
        // Field names verified from LMNT REST API docs (https://docs.lmnt.com); confirm at integration test time.
        var formValues = new Dictionary<string, string>
        {
            ["voice"] = _options.Voice,
            ["text"] = text,
            ["format"] = _options.Format,
            ["sample_rate"] = sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = _options.Language,
            ["speed"] = _options.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
        };

        if (_options.Model is not null)
            formValues["model"] = _options.Model;

        var uri = _fakeHttpBaseUri ?? "https://api.lmnt.com/v1/ai/speech/generate";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("lmnt-version", _options.ApiVersion);

        var http = _httpClient!;
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[HttpChunkSize];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }

            if (read == 0) yield break;

            var chunk = new byte[read];
            buf.AsSpan(0, read).CopyTo(chunk);
            yield return chunk.AsMemory();
        }
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_ownsHttpClient) _httpClient?.Dispose();
        return base.DisposeAsync();
    }
}
