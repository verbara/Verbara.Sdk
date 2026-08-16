using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Speechmatics;

/// <summary>
/// Speechmatics Realtime STT provider over WebSocket. Sends a <c>StartRecognition</c>
/// JSON frame, streams raw PCM audio as binary messages, and yields transcript events
/// parsed from <c>AddPartialTranscript</c> (interim) and <c>AddTranscript</c> (final)
/// messages. Lifecycle messages (<c>RecognitionStarted</c>, <c>EndOfTranscript</c>,
/// <c>Error</c>, <c>Warning</c>, <c>Info</c>) are observed but not surfaced as results.
/// </summary>
public sealed class SpeechmaticsSpeechRecognizer : SpeechRecognizer
{
    private readonly SpeechmaticsOptions _options;

    /// <inheritdoc />
    public override string ProviderName => "Speechmatics";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// There is no second, test-only constructor. Tests reach a fake by configuring
    /// <see cref="SpeechmaticsOptions.BaseUri"/>, which is the same seam an operator uses to reach a
    /// regional endpoint — so the suite drives the production path rather than one built for it.
    /// </remarks>
    public SpeechmaticsSpeechRecognizer(IOptions<SpeechmaticsOptions> options)
        => _options = options.Value;

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of scheduling/mock latency.
        ct.ThrowIfCancellationRequested();

        var wsUri = BuildUri();
        using var ws = new ClientWebSocket();
        ApplyCredential(ws);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);

        // Send the Speechmatics StartRecognition config as the first text frame.
        var sampleRate = format.SampleRate > 0 ? format.SampleRate : _options.SampleRate;
        var start = new SpeechmaticsStartRecognitionMessage
        {
            AudioFormat = new SpeechmaticsAudioFormat
            {
                Type = "raw",
                Encoding = "pcm_s16le",
                SampleRate = sampleRate,
            },
            TranscriptionConfig = new SpeechmaticsTranscriptionConfig
            {
                Language = _options.Language,
                OperatingPoint = _options.OperatingPoint,
                EnablePartials = _options.EnablePartials,
                MaxDelay = _options.MaxDelaySeconds,
            },
        };
        var startJson = JsonSerializer.Serialize(
            start,
            VoiceAiSttJsonContext.Default.SpeechmaticsStartRecognitionMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(startJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

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
                VoiceAiSttJsonContext.Default.SpeechmaticsTranscriptMessage);

            if (msg is null) continue;

            // Only transcript messages carry content. Lifecycle (RecognitionStarted,
            // EndOfTranscript, Error, Warning, Info) is observed but not surfaced.
            var isPartial = string.Equals(msg.Message, "AddPartialTranscript", StringComparison.Ordinal);
            var isFinal = string.Equals(msg.Message, "AddTranscript", StringComparison.Ordinal);
            if (!isPartial && !isFinal) continue;
            if (msg.Results is null || msg.Results.Length == 0) continue;

            // Concatenate results[*].alternatives[0].content; average the confidences.
            var sb = new StringBuilder();
            var confSum = 0f;
            var confCount = 0;
            foreach (var r in msg.Results)
            {
                if (r.Alternatives is null || r.Alternatives.Length == 0) continue;
                var alt = r.Alternatives[0];
                if (sb.Length > 0 && !string.IsNullOrEmpty(alt.Content)) sb.Append(' ');
                sb.Append(alt.Content);
                confSum += alt.Confidence;
                confCount++;
            }

            if (sb.Length == 0) continue;

            var avgConf = confCount > 0 ? confSum / confCount : 0f;
            var stt = new SpeechRecognitionResult(
                sb.ToString(),
                avgConf,
                isFinal,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The session URI: the configured base with the language pack appended as a path segment.
    /// </summary>
    /// <remarks>
    /// No credential goes in the URL — see <see cref="ApplyCredential"/>. There is deliberately no
    /// "am I under test?" branch here: <see cref="SpeechmaticsOptions.BaseUri"/> admits
    /// <c>ws://</c>, so the suite points it at its fake and every test then executes this line —
    /// the same one production executes. A branch would have left the production expression
    /// unexecuted by anything, which is how a URL can carry a credential no test can see.
    /// </remarks>
    private Uri BuildUri() => new($"{_options.BaseUri}/{_options.Language}");

    /// <summary>
    /// Authenticate the upgrade request with <c>Authorization: Bearer &lt;ApiKey&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This client previously sent the long-lived API key as a <c>?jwt=</c> query parameter, which
    /// the service does not accept. The rejection is <b>in-band</b>: the upgrade succeeds with
    /// <c>101</c> and the socket is then closed with code <c>4001 not_authorised</c>, so a handshake
    /// status proves nothing here and no session ever opened. Measured 2026-08-15, one variable per
    /// arm — same credential, same host, seconds apart:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>?jwt=&lt;long-lived API key&gt;</c>, what this client shipped → closed
    ///     <c>4001 not_authorised</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Authorization: Bearer &lt;same API key&gt;</c>, no query parameter → accepted, reached
    ///     <c>RecognitionStarted</c>. This is what the code above does.
    ///   </description></item>
    ///   <item><description>
    ///     <c>?jwt=&lt;temporary key&gt;</c> minted at the vendor's management endpoint → also
    ///     accepted. Measured, and not chosen: it adds a request before every session, a key lifetime
    ///     to manage, and an HTTP dependency to a type that has none.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The third arm is what closes the competing hypothesis: the key was <em>not</em> missing a
    /// realtime entitlement, since the same credential opened a session through two other channels.
    /// The defect was the scheme, not the key.
    /// </para>
    /// <para>
    /// This runs unconditionally. Gating a credential behind a "is this a test?" check is what
    /// leaves a fake unable to see the thing it is supposed to be checking, and it is the shape this
    /// change is removing elsewhere; the test seam that used to invite it here is gone with
    /// <see cref="BuildUri"/>'s branch.
    /// </para>
    /// </remarks>
    private void ApplyCredential(ClientWebSocket ws)
        => ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
}
