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

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] A ControlMessages subscriber threw; the stream and the other subscribers went on: message_type={MessageType} channel_id={ChannelId}")]
    public static partial void ControlMessageSubscriberFailed(ILogger logger, Exception exception, string messageType, string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] A ControlMessages subscriber threw on the completion; the disposal went on: channel_id={ChannelId}")]
    public static partial void ControlMessageSubscriberFailedOnCompletion(ILogger logger, Exception exception, string channelId);
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
/// those instruments count AudioSocket frames. On chan_websocket a hangup is the peer's close frame, which ends the
/// session, and its <c>ERROR</c> is a control message.
/// </remarks>
internal sealed class WebSocketAudioSession : IChanWebSocketSession
{
    /// <summary>
    /// The closing handshake's one bound: the answer to a peer's close, and a disposal's close plus its wait for the
    /// peer's answer, together. A server stop disposes its sessions concurrently, so it spends one bound too.
    /// </summary>
    internal static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(1);

    private readonly WebSocket _webSocket;
    private readonly AudioStreamStateChannel _state;
    private readonly Subject<ChanWebSocketControlMessage> _controlSubject = new();
    private readonly GuardedControlMessages _controlMessages;
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

    // Set once, by the read pump, from the connection's first MEDIA_START. Format and SampleRate read it over the
    // constructor's default, so one reference write publishes all three: no reader sees the new format before it.
    private readonly string _defaultFormat;
    private readonly int _defaultSampleRate;
    private ChanWebSocketMediaStart? _mediaStart;

    // Completed once, by the read pump: with the first frame of any type, or with Ended when the pump ends first.
    private readonly TaskCompletionSource<WebSocketFirstFrame> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completed once, by the first disposal, when its close and its wait for the peer's answer are over, whichever way.
    private readonly TaskCompletionSource _closeHandshake = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>
    /// Completes when the first disposal has sent its close and stopped waiting for the peer's answer: on the answer,
    /// on the bound, or at once when there was nothing to wait for. A second disposal returns immediately, so a caller
    /// that must not release the connection under a handshake in progress awaits this after its own disposal. It is
    /// bounded by <see cref="CloseBound"/> and completes before the disposal publishes anything to a subscriber.
    /// </summary>
    internal Task CloseHandshake => _closeHandshake.Task;
    /// <summary>The server's configured format until the connection's first <c>MEDIA_START</c>, then the one it names.</summary>
    public string Format => MediaStart?.Format ?? _defaultFormat;

    /// <summary>The rate of <see cref="Format"/>, derived as <see cref="ChanWebSocketMediaStart.Rate"/> is.</summary>
    public int SampleRate => MediaStart?.Rate ?? _defaultSampleRate;

    /// <inheritdoc />
    public ChanWebSocketMediaStart? MediaStart => Volatile.Read(ref _mediaStart);
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _webSocket.State == WebSocketState.Open;
    public IObservable<AudioStreamState> StateChanges => _state;
    public IObservable<ChanWebSocketControlMessage> ControlMessages => _controlMessages;

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
        _controlMessages = new GuardedControlMessages(this);
        _defaultFormat = format;
        _defaultSampleRate = AudioFormats.SampleRateOf(format);

        // DropOldest never refuses a write for a full buffer: the channel discards its oldest frame instead, and
        // reports it only through this callback, so TryWrite in the pump cannot see a drop. The read never waits for a
        // slow consumer.
        _audioInChannel = Channel.CreateBounded<ReceivedAudioFrame>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            },
            static _ => AudioStreamMetrics.FramesDropped.Add(1, AudioStreamMetrics.WebSocketTransport));
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
        var transportFailed = false;

        try
        {
            // CloseSent too: a disposal sends its close frame first and this loop then reads until the peer's answer,
            // so the answer is read instead of aborted. Frames read meanwhile are handled as usual; the audio channel is
            // complete by then, so an audio frame goes nowhere.
            while (!ct.IsCancellationRequested
                   && _webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
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
                {
                    // A close before the first frame ends the server's wait to be named first, so a connection whose
                    // answer is still in flight is never announced. Then the answer, before the `finally` publishes
                    // the ending: the peer has its reply before any consumer reacts to Disconnected. When this is the
                    // answer to this side's own close, the socket is Closed and nothing is sent.
                    _firstFrame.TrySetResult(new WebSocketFirstFrame(WebSocketFirstFrameKind.Ended));
                    using var answerBound = new CancellationTokenSource(CloseBound);
                    await CloseOnceAsync(_webSocket.CloseStatus ?? WebSocketCloseStatus.NormalClosure, answerBound.Token);
                    break;
                }

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
            // exception, whatever status the frame carries: the close code says the peer ended the stream, not
            // who failed. Measured through the server: a connection reset raises ConnectionClosedPrematurely
            // over the socket's reset, and a connection that ends without a close frame raises
            // ConnectionClosedPrematurely with no inner exception; both are failed transports, so the ending
            // is Error then Disconnected. The session is still Connected here: only this pump's `finally` and
            // a disposal that has awaited this pump publish its ending. Reported before the `finally`
            // publishes it, so whoever reacts to Error already finds the line and the count.
            // The `_disposed` filter keeps an ending on this side an ending without an error: the disposal,
            // which a server stop also ends in, sets it before it cancels or closes anything, so a connection
            // that fails while that ending is in progress is neither reported nor classified as a failure.
            if (Volatile.Read(ref _disposed) == 0)
            {
                WebSocketAudioSessionLog.TransportFailed(_logger, ex, ChannelId);
                AudioStreamMetrics.TransportFailures.Add(1, AudioStreamMetrics.WebSocketTransport);
                transportFailed = true;
            }
        }
        finally
        {
            // A connection that ends before its first frame ends its server's wait as Ended, distinct from
            // unnamed: the server then neither registers nor announces it.
            _firstFrame.TrySetResult(new WebSocketFirstFrame(WebSocketFirstFrameKind.Ended));
            _audioInChannel.Writer.TryComplete();
            _state.TryEnd(error: transportFailed);
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
        var mediaStart = ChanWebSocketInboundReader.ReadMediaStart(frame, _defaultFormat);
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

    // The refusals a session can predict from its own state are thrown before anything is written, and synchronously:
    // each Send…Async below is the non-async front half (argument, then state, then encoding in the connection's format,
    // then length) and returns the ValueTask of the async back half, which only takes the send lock and writes.

    private const string FormatNotYetKnownMessage =
        "No MEDIA_START has been read yet, so the connection's control format is not yet known: keep " +
        "AudioServerOptions.MediaStartTimeout at its default so the stream is announced once Asterisk's MEDIA_START has " +
        "been read, or call once MediaStart is set.";

    private const string SetMediaDirectionNeedsJsonMessage =
        "The connection's control format is plain text; Asterisk accepts SET_MEDIA_DIRECTION in JSON only — set " +
        "control_message_format = json in chan_websocket.conf, or add f(json) to a dial string you write.";

    private const string NoXonXoffCommandMessage =
        "chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and " +
        "24.0.0-rc2: 'command unknown', no reply); nothing was written. Flow control is Asterisk's to send: read " +
        "ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages.";

    public ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var format = NegotiatedFormat();
        var command = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(new ChanWebSocketMarkMedia(mark), format);
        if (command.Length > ChanWebSocketControlMessageSerializer.MaxCommandBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mark),
                command.Length,
                $"The MARK_MEDIA command for this mark is {command.Length} bytes in {format}, over the " +
                $"{ChanWebSocketControlMessageSerializer.MaxCommandBytes}-byte text frame chan_websocket reads (a longer one is " +
                "dropped unread, with no reply); nothing was written. A mark may carry at most 117 bytes in PlainText, 84 in " +
                "Json before escaping.");
        }

        return SendCommandAsync(command, cancellationToken);
    }

    public ValueTask SendXonAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NoXonXoffCommandMessage);

    public ValueTask SendXoffAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NoXonXoffCommandMessage);

    public ValueTask SendSetMediaDirectionAsync(
        ChanWebSocketMediaDirection direction,
        CancellationToken cancellationToken = default)
    {
        var format = NegotiatedFormat();
        if (format != ChanWebSocketControlMessageFormat.Json)
            throw new InvalidOperationException(SetMediaDirectionNeedsJsonMessage);

        var command = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(new ChanWebSocketSetMediaDirection(direction), format);
        return SendCommandAsync(command, cancellationToken);
    }

    // The connection's control format: the first MEDIA_START's, read once from the one write that publishes it. Before
    // one has been read there is none, and no command is written.
    private ChanWebSocketControlMessageFormat NegotiatedFormat() =>
        MediaStart?.ControlMessageFormat ?? throw new InvalidOperationException(FormatNotYetKnownMessage);

    private async ValueTask SendCommandAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(command, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The one place a close frame is sent: the pump's answer to a peer's close and a disposal's own close both come
    // here. WebSocket allows one send at a time, so it takes the send lock; it sends only from Open or CloseReceived,
    // read under that lock, so the answer and a racing disposal send one close between them. `bound` is the caller's
    // own CloseBound source, never a token that asked for the ending. A close that cannot be sent is not a second
    // ending: every failure the send can raise ends here.
    private async Task CloseOnceAsync(WebSocketCloseStatus status, CancellationToken bound)
    {
        try
        {
            await _sendLock.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A write holds the lock past the bound, on a peer that stopped reading: the ending goes on without the
            // close frame rather than wait on that write.
            return;
        }

        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _webSocket.CloseOutputAsync(status, statusDescription: null, bound).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // The connection already failed or closed under the send: the peer cannot hear a close any more.
        }
        catch (IOException)
        {
            // The transport refused the write (a reset or a closed socket): the same as above, one layer down.
        }
        catch (ObjectDisposedException)
        {
            // The socket was released under the send; nothing remains to close.
        }
        catch (OperationCanceledException)
        {
            // The bound ran out mid-send, on a peer that is not reading: the ending does not wait longer.
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

        _audioInChannel.Writer.TryComplete();

        // The close goes out before anything stops the read, whether or not a read is pending: a cancelled receive
        // aborts the socket, and an aborted socket sends nothing. Then the pump, still reading, takes the peer's
        // answer and leaves on it. One bound covers the send and that wait together, so a peer that never answers
        // costs the disposal one bound.
        try
        {
            using var closeBound = new CancellationTokenSource(CloseBound);
            await CloseOnceAsync(WebSocketCloseStatus.NormalClosure, closeBound.Token);

            if (_readPumpTask is not null)
                await _readPumpTask.WaitAsync(closeBound.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            // The handshake is over, answered or not: a server handler that joined this disposal may release the
            // connection now.
            _closeHandshake.TrySetResult();
        }

        // Last: aborts a receive still pending once the bound is spent, on a peer that never answered.
        await _cts.CancelAsync();

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

    // One failure of a ControlMessages subscriber: written once at Warning, with the exception, and counted once.
    // A null message is the completion.
    private void ReportControlSubscriberFailure(Exception exception, ChanWebSocketControlMessage? message)
    {
        if (message is null)
            WebSocketAudioSessionLog.ControlMessageSubscriberFailedOnCompletion(_logger, exception, ChannelId);
        else
            WebSocketAudioSessionLog.ControlMessageSubscriberFailed(_logger, exception, message.GetType().Name, ChannelId);

        AudioStreamMetrics.ControlMessageSubscriberFaults.Add(1);
    }

    /// <summary>
    /// <see cref="ControlMessages"/>: each subscriber behind its own guard. An exception a subscriber throws on the
    /// delivering thread, on a message or on the completion, is reported and goes no further: the read pump, a
    /// disposal and the other subscribers go on, and the subscriber stays subscribed. A subscription composed through
    /// an operator is ended by that operator on its first failure; the guard sees the exception after that and
    /// reports it the same way. A plain observable, not an Rx producer, so subscribing adds no safeguard of Rx's own.
    /// </summary>
    private sealed class GuardedControlMessages(WebSocketAudioSession owner) : IObservable<ChanWebSocketControlMessage>
    {
        public IDisposable Subscribe(IObserver<ChanWebSocketControlMessage> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            return owner._controlSubject.Subscribe(new GuardedObserver(owner, observer));
        }
    }

    /// <summary>One <see cref="ControlMessages"/> subscriber, behind the guard. A <c>Subject</c> replays nothing, so every
    /// notification is a publication.</summary>
    private sealed class GuardedObserver(WebSocketAudioSession owner, IObserver<ChanWebSocketControlMessage> inner)
        : IObserver<ChanWebSocketControlMessage>
    {
        public void OnNext(ChanWebSocketControlMessage value)
        {
            try
            {
                inner.OnNext(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Consumer code failed on a message: the line and the count are the only trace of it.
                owner.ReportControlSubscriberFailure(ex, value);
            }
        }

        public void OnCompleted()
        {
            try
            {
                inner.OnCompleted();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Raised by the disposal, which goes on to release the connection.
                owner.ReportControlSubscriberFailure(ex, message: null);
            }
        }

        public void OnError(Exception error)
        {
            try
            {
                inner.OnError(error);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The session never fails the sequence; guarded the same way should that change.
                owner.ReportControlSubscriberFailure(ex, message: null);
            }
        }
    }
}
