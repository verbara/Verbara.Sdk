// Architectural note: this session implements IChanWebSocketSession (sub-interface of IAudioStream)
// instead of folding chan_websocket-specific methods into IAudioStream, because the AudioSocket
// TCP protocol doesn't share the text-frame control channel. Consumers cast the IAudioStream
// returned by IAudioServer.GetStream when they know they're talking to chan_websocket.
using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class WebSocketAudioSessionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] Transport failed under a live session, which ends as Disconnected: channel_id={ChannelId}")]
    public static partial void TransportFailed(ILogger logger, Exception exception, string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] A second MEDIA_START on one connection is ignored: path_segment={PathSegment} first_channel_id={FirstChannelId} ignored_channel_id={IgnoredChannelId}")]
    public static partial void SecondMediaStartIgnored(ILogger logger, string pathSegment, string firstChannelId, string ignoredChannelId);
}

/// <summary>How a WebSocket audio connection's wait for its first frame ended.</summary>
internal enum WebSocketFirstFrameKind
{
    /// <summary>A text frame that parses as <c>MEDIA_START</c> and names a call.</summary>
    MediaStart,

    /// <summary>A binary frame: the stream's first audio frame.</summary>
    Binary,

    /// <summary>A text frame that is not a <c>MEDIA_START</c> naming a call.</summary>
    Text,

    /// <summary>The connection ended before any frame: a close frame, a reset, or the session's disposal.</summary>
    Ended,
}

/// <summary>The first frame of a WebSocket audio connection, as far as its key is concerned.</summary>
/// <param name="Kind">What the first frame was.</param>
/// <param name="Value">For <see cref="WebSocketFirstFrameKind.MediaStart"/> the <c>channel_id</c>; for <see cref="WebSocketFirstFrameKind.Text"/> the event the frame names, if any.</param>
internal readonly record struct WebSocketFirstFrame(WebSocketFirstFrameKind Kind, string? Value = null);

/// <summary>
/// A single WebSocket audio connection. Receives binary frames via ManagedWebSocket
/// (audio payload) and text frames read in the format Asterisk sends them: <c>MEDIA_START</c> as the stream's
/// state (<see cref="MediaStart"/>), every other frame as a <see cref="ChanWebSocketControlMessage"/>.
/// </summary>
/// <remarks>
/// Records the <c>Verbara.Sdk.Ari.Audio</c> instruments, each measurement tagged
/// <c>audio.transport=websocket</c>: the stream opened at its start and closed at its one ending, the
/// non-empty binary messages and their payload bytes received and sent, the time from receipt to the
/// consumer's read, and a transport failure under a live session. It records no hangup or error frames:
/// those instruments count AudioSocket frames; chan_websocket ends a call with a close frame, and its <c>ERROR</c> is a
/// control message.
/// </remarks>
internal sealed class WebSocketAudioSession : IChanWebSocketSession
{
    private readonly WebSocket _webSocket;
    private readonly AudioStreamStateChannel _state;
    private readonly Subject<ChanWebSocketControlMessage> _controlSubject = new();
    private readonly Channel<ReceivedAudioFrame> _audioInChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private readonly string _pathSegment;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _readPumpTask;
    private int _disposed;
    private string _channelId;

    // The channel_id of the connection's first MEDIA_START, written and read by the read pump only.
    private string? _firstMediaStartChannelId;
    private volatile bool _isNamed;
    private bool _firstFrameRead;
    private int _mediaStarts;

    // Set once, by the read pump, from the connection's first MEDIA_START: the format first, then the record, so a
    // consumer that reads MediaStart reads the format it carried.
    private volatile string _format;
    private volatile int _sampleRate;
    private ChanWebSocketMediaStart? _mediaStart;

    // Completed once, by the read pump: with the first frame of any type, or with Ended when the pump ends first.
    private readonly TaskCompletionSource<WebSocketFirstFrame> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The key this stream is registered under: the <c>channel_id</c> its first frame named when that frame was
    /// <c>MEDIA_START</c>, otherwise the upgrade path's last segment. It changes at most once, before the server
    /// registers or announces the session, so no consumer sees two values.
    /// </summary>
    public string ChannelId => Volatile.Read(ref _channelId);

    /// <summary>
    /// True once the session is keyed by the call its <c>MEDIA_START</c> named; false while it is keyed by its path
    /// segment. A named session outranks an unnamed one that presents the same value.
    /// </summary>
    internal bool IsNamed => _isNamed;

    /// <summary>Completes with the connection's first frame, or with <see cref="WebSocketFirstFrameKind.Ended"/>.</summary>
    internal Task<WebSocketFirstFrame> FirstFrame => _firstFrame.Task;
    /// <summary>The server's configured format until the connection's first <c>MEDIA_START</c>, then the one it names.</summary>
    public string Format => _format;

    /// <summary>The rate of <see cref="Format"/>, derived as <see cref="ChanWebSocketMediaStart.Rate"/> is.</summary>
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public ChanWebSocketMediaStart? MediaStart => Volatile.Read(ref _mediaStart);
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _webSocket.State == WebSocketState.Open;
    public IObservable<AudioStreamState> StateChanges => _state;
    public IObservable<ChanWebSocketControlMessage> ControlMessages => _controlSubject;

    /// <summary>
    /// Completes when the session's ending is published, before any observer is notified of it: the
    /// signal its server waits on instead of the consumers' observable.
    /// </summary>
    internal Task Ended => _state.Ended;

    /// <param name="webSocket">The upgraded connection.</param>
    /// <param name="channelId">The upgrade path's last segment: the key until <see cref="Rekey"/>.</param>
    /// <param name="format">The audio format the connection carries.</param>
    /// <param name="logger">
    /// Where a consumer's <see cref="StateChanges"/> observer that throws, and a transport failure under a
    /// live session, are reported. Required, so a call site cannot compile without it and silently lose
    /// those lines; the server passes its own.
    /// </param>
    internal WebSocketAudioSession(WebSocket webSocket, string channelId, string format, ILogger logger)
    {
        _webSocket = webSocket;
        _logger = logger;
        _channelId = channelId;
        _pathSegment = channelId;
        _state = new AudioStreamStateChannel(AudioStreamState.Connected, logger, () => ChannelId);
        _format = format;
        _sampleRate = AudioFormats.SampleRateOf(format);

        _audioInChannel = Channel.CreateBounded<ReceivedAudioFrame>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Keys the session by the call its <c>MEDIA_START</c> named. The server calls it once, before it registers or
    /// announces the session.
    /// </summary>
    internal void Rekey(string channelId)
    {
        Volatile.Write(ref _channelId, channelId);
        _isNamed = true;
    }

    internal void Start()
    {
        // The stream is open from here: the server starts the session right after the upgrade. Its one
        // ending, whoever publishes it, counts it closed with the same tag.
        _state.RecordOpened(AudioStreamMetrics.WebSocketTransport);
        _readPumpTask = Task.Run(() => ReadPumpAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ReadPumpAsync(CancellationToken ct)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(8192);

        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                bufferWriter.Clear();
                ValueWebSocketReceiveResult result;

                do
                {
                    var memory = bufferWriter.GetMemory(4096);
                    result = await _webSocket.ReceiveAsync(memory, ct);
                    bufferWriter.Advance(result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // A text frame is read once: whether it is MEDIA_START, the call it names and the record it maps to all
                // come from this one read, so they cannot disagree.
                var text = result.MessageType == WebSocketMessageType.Text
                    ? ChanWebSocketMediaStartParser.Read(bufferWriter.WrittenSpan)
                    : null;
                var head = text?.Head ?? default;
                var isMediaStart = text is not null
                    && string.Equals(head.Event, ChanWebSocketTextFrameHead.MediaStartEvent, StringComparison.Ordinal);

                // Stream state, never a control message, and read before the first-frame decision below: a naming
                // MEDIA_START has filled MediaStart, Format and SampleRate before the server can announce the stream.
                if (isMediaStart)
                    OnMediaStart(text!, head.ChannelId);

                // The first frame decides the key, once, whatever it is; the server reads the decision before it
                // registers the session. The frame itself is handled below like any other: a binary first frame is
                // the stream's first audio frame.
                if (!_firstFrameRead)
                {
                    _firstFrameRead = true;
                    _firstFrame.TrySetResult(result.MessageType != WebSocketMessageType.Text
                        ? new WebSocketFirstFrame(WebSocketFirstFrameKind.Binary)
                        : head.IsMediaStart
                            ? new WebSocketFirstFrame(WebSocketFirstFrameKind.MediaStart, head.ChannelId)
                            : new WebSocketFirstFrame(WebSocketFirstFrameKind.Text, head.Event));
                }

                if (bufferWriter.WrittenCount == 0)
                    continue;

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Binary:
                        // Non-empty by the check above, so an empty message counts as no frame received,
                        // as an empty AudioSocket audio frame does.
                        AudioStreamMetrics.FramesReceived.Add(1, AudioStreamMetrics.WebSocketTransport);
                        AudioStreamMetrics.BytesReceived.Add(bufferWriter.WrittenCount, AudioStreamMetrics.WebSocketTransport);
                        _audioInChannel.Writer.TryWrite(new ReceivedAudioFrame(bufferWriter.WrittenMemory.ToArray(), Stopwatch.GetTimestamp()));
                        break;

                    case WebSocketMessageType.Text when isMediaStart:
                        // Handled above, as state.
                        break;

                    case WebSocketMessageType.Text:
                        TryDispatchControlMessage(text!);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Pump cancelled — normal shutdown path.
        }
        catch (WebSocketException ex)
        {
            // The peer's orderly ending is its close frame, which the loop above leaves on without an
            // exception. Measured through the server: a connection reset raises ConnectionClosedPrematurely
            // over the socket's reset, and a connection that ends without a close frame raises
            // ConnectionClosedPrematurely with no inner exception; both are abnormal endings. The session
            // is still Connected here: only this pump's `finally` and a disposal that has awaited this pump
            // publish its ending. Reported before the `finally` publishes Disconnected, so whoever reacts
            // to the ending already finds the line and the count. The ending stays Disconnected.
            // The `_disposed` filter is defensive: an owner's disposal sets it before it cancels or closes
            // anything, and that teardown surfaces here as OperationCanceledException; no measured
            // teardown has reached this catch.
            if (Volatile.Read(ref _disposed) == 0)
            {
                WebSocketAudioSessionLog.TransportFailed(_logger, ex, ChannelId);
                AudioStreamMetrics.TransportFailures.Add(1, AudioStreamMetrics.WebSocketTransport);
            }
        }
        finally
        {
            // A connection that ends before its first frame ends its server's wait as Ended, distinct from
            // unnamed: the server then neither registers nor announces it.
            _firstFrame.TrySetResult(new WebSocketFirstFrame(WebSocketFirstFrameKind.Ended));
            _audioInChannel.Writer.TryComplete();
            _state.TryEnd(error: false);
        }
    }

    // The first MEDIA_START on the connection, whenever it arrives, fills MediaStart and the stream's format, once, and
    // is traced once. Only the first frame names the stream (the server decides that from FirstFrame); a later one
    // re-keys nothing, and a second one on the connection changes nothing and is recorded at Debug.
    private void OnMediaStart(ChanWebSocketTextFrame frame, string? channelId)
    {
        if (Interlocked.Increment(ref _mediaStarts) != 1)
        {
            LogSecondMediaStart(channelId);
            return;
        }

        _firstMediaStartChannelId = channelId;
        var mediaStart = ChanWebSocketInboundReader.ReadMediaStart(frame, _format);
        _format = mediaStart.Format;
        _sampleRate = mediaStart.Rate;
        Volatile.Write(ref _mediaStart, mediaStart);
        EmitMediaStarted(mediaStart);
    }

    // The MEDIA_START that fills MediaStart, mirrored once onto Activity.Current, tagged with the call the frame names
    // (not the key, which a late frame never changes). No media.channels tag: it is 1 on every frame.
    private static void EmitMediaStarted(ChanWebSocketMediaStart mediaStart)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        activity.AddEvent(new ActivityEvent(
            VerbaraSemanticConventions.Events.MediaStarted,
            tags: new ActivityTagsCollection
            {
                [VerbaraSemanticConventions.Channel.Id] = mediaStart.ChannelId,
                [VerbaraSemanticConventions.Media.Codec] = mediaStart.Format,
                [VerbaraSemanticConventions.Media.SampleRate] = mediaStart.Rate,
            }));
    }

    // Names the path segment and the connection's first MEDIA_START, never the session's key: the server re-keys
    // the session on its own continuation, so the pump can read a second MEDIA_START before the key has changed.
    // When that MEDIA_START was the first frame and ended the server's wait to be named, its channel_id is the key.
    private void LogSecondMediaStart(string? ignoredChannelId)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var segment = ChanWebSocketMediaStartParser.ForLog(_pathSegment);
            var first = ChanWebSocketMediaStartParser.ForLog(_firstMediaStartChannelId);
            var ignored = ChanWebSocketMediaStartParser.ForLog(ignoredChannelId);
            WebSocketAudioSessionLog.SecondMediaStartIgnored(_logger, segment, first, ignored);
        }
    }

    // Every text frame that is not MEDIA_START is published: as the typed record its event maps to, or as the generic
    // record with its event name and exact text. None is dropped. The reader maps MEDIA_START to no record, so it can
    // never be published here even if a caller routed one this way.
    private void TryDispatchControlMessage(ChanWebSocketTextFrame frame)
    {
        var message = ChanWebSocketInboundReader.ReadMessage(frame);
        if (message is null)
            return;

        EmitActivityEvent(message);
        _controlSubject.OnNext(message);
    }

    // Mirrors event-shaped control messages onto Activity.Current as span events
    // (not span tags) so consumer traces capture DTMF arrivals and mark acknowledgements without
    // overwriting span attributes. No-op if no span is active on the current
    // execution context, matching OTel event semantics. DTMF_END carries no duration, so no duration tag.
    private void EmitActivityEvent(ChanWebSocketControlMessage message)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        switch (message)
        {
            case ChanWebSocketDtmf dtmf:
                activity.AddEvent(new ActivityEvent(
                    VerbaraSemanticConventions.Events.DtmfReceived,
                    tags: new ActivityTagsCollection
                    {
                        [VerbaraSemanticConventions.Channel.Id] = ChannelId,
                        ["asterisk.dtmf.digit"] = dtmf.Digit,
                    }));
                break;
            case ChanWebSocketMediaMarkProcessed markProcessed:
                activity.AddEvent(new ActivityEvent(
                    VerbaraSemanticConventions.Events.MediaMarkProcessed,
                    tags: new ActivityTagsCollection
                    {
                        [VerbaraSemanticConventions.Channel.Id] = ChannelId,
                        ["asterisk.media.mark"] = markProcessed.Mark,
                    }));
                break;
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (await _audioInChannel.Reader.WaitToReadAsync(cancellationToken)
            && _audioInChannel.Reader.TryRead(out var frame))
        {
            // Only non-empty messages are queued, so every read records one value.
            AudioStreamMetrics.FrameLatency.Record(
                Stopwatch.GetElapsedTime(frame.ReceivedTimestamp).TotalMilliseconds,
                AudioStreamMetrics.WebSocketTransport);
            return frame.Payload;
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(audioData, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        // Counted once the message was sent, and no frame for a write that carried no audio.
        if (!audioData.IsEmpty)
        {
            AudioStreamMetrics.FramesSent.Add(1, AudioStreamMetrics.WebSocketTransport);
            AudioStreamMetrics.BytesSent.Add(audioData.Length, AudioStreamMetrics.WebSocketTransport);
        }
    }

    public ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return SendControlMessageAsync(new ChanWebSocketMarkMedia(mark), cancellationToken);
    }

    public ValueTask SendXonAsync(CancellationToken cancellationToken = default)
        => SendControlMessageAsync(new ChanWebSocketMediaXon(), cancellationToken);

    public ValueTask SendXoffAsync(CancellationToken cancellationToken = default)
        => SendControlMessageAsync(new ChanWebSocketMediaXoff(), cancellationToken);

    public ValueTask SendSetMediaDirectionAsync(
        ChanWebSocketMediaDirection direction,
        CancellationToken cancellationToken = default)
        => SendControlMessageAsync(new ChanWebSocketSetMediaDirection(direction), cancellationToken);

    private async ValueTask SendControlMessageAsync(
        ChanWebSocketControlMessage message,
        CancellationToken cancellationToken)
    {
        var utf8 = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(utf8, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }


    public async ValueTask DisposeAsync()
    {
        // Atomic: the connection handler that owns this session disposes it on its way out, and a
        // consumer holding it as an IAudioStream can dispose it at the same moment.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();
        _audioInChannel.Writer.TryComplete();

        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                // Bounded: a read pump that has not started receiving yet is not aborted by the
                // cancellation above, so the socket is still open here and CloseAsync waits for the
                // peer's close frame, which a peer that is not reading never sends.
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            }
            catch { /* Best effort */ }
        }

        if (_readPumpTask is not null)
            await _readPumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Publishes the ending only if the pump did not (a pump never started); then completes it.
        _firstFrame.TrySetResult(new WebSocketFirstFrame(WebSocketFirstFrameKind.Ended));
        _state.TryEnd(error: false);
        _state.Dispose();
        _controlSubject.OnCompleted();
        _controlSubject.Dispose();
        _webSocket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
