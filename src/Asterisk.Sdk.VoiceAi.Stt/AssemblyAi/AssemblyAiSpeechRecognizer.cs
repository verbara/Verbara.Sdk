using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.AssemblyAi;

/// <summary>
/// AssemblyAI Universal Streaming STT provider over WebSocket (v3 wire).
/// Streams raw PCM audio frames as binary messages and yields transcript events
/// parsed from <c>Turn</c> messages. Lifecycle messages (<c>Begin</c>, <c>Termination</c>)
/// are observed but not surfaced as <see cref="SpeechRecognitionResult"/>.
/// </summary>
public sealed class AssemblyAiSpeechRecognizer : SpeechRecognizer
{
    private readonly AssemblyAiOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "AssemblyAI";

    /// <summary>Initializes a new instance for production use.</summary>
    public AssemblyAiSpeechRecognizer(IOptions<AssemblyAiOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal AssemblyAiSpeechRecognizer(IOptions<AssemblyAiOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var wsUri = BuildUri();
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
        {
            // AssemblyAI auth: raw API key in Authorization header (no "Bearer " prefix).
            ws.Options.SetRequestHeader("Authorization", _options.ApiKey);
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Fire-and-forget: stream audio frames to the server as binary WebSocket messages.
        var sendTask = SendLoopAsync(ws, audioFrames, ct);

        // Receive loop writes transcripts to channel, then completes the writer.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveLoopAsync(ws, channel.Writer, ct).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        // Yield results as they arrive from the receive loop.
        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        // Ensure both tasks complete (propagate exceptions via AggregateException).
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }

            // Signal end-of-audio (half-close) so the server flushes any pending transcript.
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        ChannelWriter<SpeechRecognitionResult> writer,
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
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(
                json,
                VoiceAiSttJsonContext.Default.AssemblyAiTurnMessage);

            if (msg is null) continue;

            // Only "Turn" messages carry transcripts. Begin/Termination are lifecycle.
            if (!string.Equals(msg.Type, "Turn", StringComparison.Ordinal)) continue;

            var stt = new SpeechRecognitionResult(
                msg.Transcript,
                // AssemblyAI v3 Turn messages do not include a per-turn confidence scalar;
                // Note: surface 0f (consistent with other providers when absent).
                0f,
                msg.EndOfTurn,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    private Uri BuildUri()
    {
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"?sample_rate={_options.SampleRate}&format_turns={_options.FormatTurns}&end_of_turn_confidence_threshold={_options.EndOfTurnConfidenceThreshold}");

        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/v3/ws{query}");

        return new Uri(_options.BaseUri + query);
    }
}
