using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Cartesia;

/// <summary>
/// Cartesia Sonic-3 WebSocket streaming TTS provider. Sends a JSON synthesis request and receives
/// PCM audio as base64 inside <c>chunk</c> text frames, until the server emits <c>done</c> (or
/// <c>error</c>) or closes the socket.
/// </summary>
public sealed class CartesiaSpeechSynthesizer : SpeechSynthesizer
{
    /// <summary>
    /// One WebSocket read. Frames larger than this arrive fragmented and are assembled before
    /// parsing — see <see cref="ReceiveFramesAsync"/>.
    /// </summary>
    private const int ReceiveBufferSize = 65536;

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

        // Fire-and-forget: send the synthesis request. Nothing follows it — see SendRequestAsync.
        var sendTask = SendRequestAsync(ws, text, outputFormat, sessionCts.Token);

        // Receive loop writes decoded audio to the channel, stops on `done` / `error`.
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
            Transcript = text,

            // Required by the endpoint, not optional — see CartesiaTtsRequest.ContextId. One per
            // request: it exists to correlate the frames of THIS synthesis, so reusing a value
            // across requests would defeat the only thing it does.
            ContextId = Guid.NewGuid().ToString()
        };

        var json = JsonSerializer.Serialize(request, VoiceAiTtsJsonContext.Default.CartesiaTtsRequest);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(json).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // The request IS the end of input for a non-continued synthesis, and it is the last thing
        // this method sends.
        //
        // No half-close follows, and that is the fix — not an omission. This method used to call
        // CloseOutputAsync(NormalClosure) here, right after the request, behind a guarded 2 s
        // timeout. Measured against the live endpoint with that call as the only variable: with it,
        // 0 frames and 0 bytes arrive; without it, 7 chunk frames and a `done`, 32 694 B of audio in
        // 1.022 s. The vendor reads the client's Close frame as "abandon the request", so the
        // half-close destroyed the synthesis it was meant to finish.
        //
        // This is one of three TTS sites measured with the same defect — LMNT and ElevenLabs are the
        // others — so treat a bare CloseOutputAsync after a request as suspect, not as hygiene.
    }

    private static async Task ReceiveFramesAsync(
        ClientWebSocket ws,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
        CancellationToken ct)
    {
        var buf = new byte[ReceiveBufferSize];
        var assembled = new ArrayBufferWriter<byte>(ReceiveBufferSize);

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

            // Assemble until the message is whole. The vendor sizes these frames, not this client,
            // and a loop that parsed each read as a complete message would hand JSON a truncated
            // document once a frame outgrew this 64 KiB buffer. That failure is length-dependent,
            // which is exactly why no short probe and no fake ever tripped it.
            assembled.Write(buf.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Tolerated without evidence, deliberately. A live run measured zero binary bytes on
                // this surface — but a vendor not sending a mode on one day is not evidence the mode
                // does not exist, and keeping the branch costs nothing. Removing it would be an
                // unmeasured change.
                await writer.WriteAsync(assembled.WrittenSpan.ToArray().AsMemory(), ct)
                    .ConfigureAwait(false);
                assembled.Clear();
                continue;
            }

            var message = JsonSerializer.Deserialize(
                assembled.WrittenSpan,
                VoiceAiTtsJsonContext.Default.CartesiaTtsServerMessage);
            assembled.Clear();

            if (message is null) continue;

            if (string.Equals(message.Type, "chunk", StringComparison.Ordinal) &&
                message.Data is { Length: > 0 } base64)
            {
                await writer.WriteAsync(Convert.FromBase64String(base64).AsMemory(), ct)
                    .ConfigureAwait(false);
                continue;
            }

            // `done` ends the stream; so does `error` — silently, which is the defect underneath
            // this one. An invalid credential, a rejected voice or a malformed request all arrive
            // here as {"type":"error","error":…,"status_code":4xx} and leave the caller an empty
            // stream and no exception. That is Sdk/ADR-0049 D1 on this surface. Surfacing it changes
            // behaviour — a synthesis that silently yields nothing would start throwing — so it
            // belongs to the D1 remedy and its own decision, not inside a frame-format fix.
            if (string.Equals(message.Type, "done", StringComparison.Ordinal) ||
                string.Equals(message.Type, "error", StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    private Uri BuildUri()
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://127.0.0.1:{_fakeServerPort}/tts/websocket");

        return new Uri(_options.BaseUri);
    }
}
