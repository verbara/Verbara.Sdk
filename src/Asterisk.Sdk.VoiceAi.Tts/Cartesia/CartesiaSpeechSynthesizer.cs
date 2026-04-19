using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Cartesia;

/// <summary>
/// Cartesia Sonic-3 WebSocket streaming TTS provider. Sends a JSON
/// synthesis request and receives raw PCM audio frames as binary messages
/// until the server emits a <c>done</c> control message or closes the socket.
/// </summary>
public sealed class CartesiaSpeechSynthesizer : SpeechSynthesizer
{
    private readonly CartesiaOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "Cartesia";

    /// <summary>Initializes a new instance for production use.</summary>
    public CartesiaSpeechSynthesizer(IOptions<CartesiaOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal CartesiaSpeechSynthesizer(IOptions<CartesiaOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var uri = BuildUri();
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
        {
            ws.Options.SetRequestHeader("X-API-Key", _options.ApiKey);
            ws.Options.SetRequestHeader("Cartesia-Version", _options.ApiVersion);
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: when the receive loop detects the server is gone (abort / close),
        // cancel the session so the send side unblocks if it is still inside
        // SendAsync / CloseOutputAsync on the half-dead socket.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: send the synthesis request, then half-close.
        var sendTask = SendRequestAsync(ws, text, outputFormat, sessionCts.Token);

        // Receive loop writes binary audio frames to channel, stops on `done` control msg.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveFramesAsync(ws, channel.Writer, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, ct);

        // Yield binary audio frames as they arrive (true streaming).
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;

        // Propagate any exceptions from send/receive tasks.
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private async Task SendRequestAsync(
        ClientWebSocket ws,
        string text,
        AudioFormat outputFormat,
        CancellationToken ct)
    {
        var request = new CartesiaTtsRequest
        {
            ModelId = _options.Model,
            Voice = new CartesiaTtsVoice { Mode = "id", Id = _options.VoiceId },
            OutputFormat = new CartesiaTtsOutputFormat
            {
                Container = "raw",
                Encoding = _options.OutputFormat,
                SampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.OutputSampleRate
            },
            Language = _options.Language,
            Transcript = text
        };

        var json = JsonSerializer.Serialize(request, VoiceAiTtsJsonContext.Default.CartesiaTtsRequest);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(json).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Half-close so the server knows no more input is coming.
        // Guarded timeout: CloseOutputAsync can hang if the server aborted the
        // connection at the socket level before the client's FIN is processed.
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

    private static async Task ReceiveFramesAsync(
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
                // A `done` (or `error`) text message terminates the stream.
                var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                var control = JsonSerializer.Deserialize(
                    json,
                    VoiceAiTtsJsonContext.Default.CartesiaTtsControlMessage);
                if (control is not null &&
                    (string.Equals(control.Type, "done", StringComparison.Ordinal) ||
                     string.Equals(control.Type, "error", StringComparison.Ordinal)))
                {
                    break;
                }
            }
        }
    }

    private Uri BuildUri()
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/tts/websocket");

        return new Uri(_options.BaseUri);
    }
}
