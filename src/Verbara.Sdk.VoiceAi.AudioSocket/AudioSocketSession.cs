using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.AudioSocket;

/// <summary>
/// A single AudioSocket connection — bidirectional audio stream for one Asterisk channel.
/// </summary>
public sealed class AudioSocketSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly PipeWriter _writer;
    private readonly PipeReader _reader;
    private readonly Channel<ReadOnlyMemory<byte>> _audioChannel;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;

    /// <summary>
    /// Serialises every frame written to <c>_writer</c>, which is not safe for concurrent writes: a hangup
    /// issued while an audio write is in flight waits for that write, so the far end never reads two
    /// frames interleaved. It is never disposed, because a writer may still be waiting on it when the
    /// session tears down; it holds no handle unless one is asked for, and none is.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed; // 0 = live, 1 = torn down — by any cause
    private int _consumerDisposed; // 1 once the owner called DisposeAsync(); see ADR-0053
    private int _hangupFired; // 0 = not fired, 1 = fired

    /// <summary>UUID received from Asterisk (from the first UUID frame).</summary>
    public Guid ChannelId { get; }

    /// <summary>Remote endpoint address.</summary>
    public string RemoteEndpoint { get; }

    /// <summary>Audio format of incoming audio.</summary>
    public AudioFormat InputFormat { get; }

    /// <summary>Whether the session is still connected.</summary>
    public bool IsConnected => _disposed == 0 && !_cts.IsCancellationRequested;

    /// <summary>Raised when the channel hangs up (from either side).</summary>
    /// <remarks>
    /// While the handlers run, the session still holds its channel id on the server that accepted it
    /// and still counts as active there: a connection that presents the same id waits until every
    /// handler has returned before it is served.
    /// </remarks>
    public event Action? OnHangup;

    private readonly TaskCompletionSource _hungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once <see cref="OnHangup"/> has been raised, every handler has returned and
    /// <see cref="Released"/> has run. A connection that presents the same channel id waits on this, so
    /// it is served only after every hangup handler of this session, the consumer's included.
    /// </summary>
    internal Task HungUp => _hungUp.Task;

    /// <summary>
    /// Run once, after every <see cref="OnHangup"/> handler and before <see cref="HungUp"/> completes. The
    /// registering server releases the session's registry entry here.
    /// </summary>
    internal Action? Released { get; set; }

    internal AudioSocketSession(
        Guid channelId,
        TcpClient client,
        PipeReader reader,
        AudioFormat inputFormat,
        ILogger logger)
    {
        ChannelId = channelId;
        RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        InputFormat = inputFormat;
        _client = client;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _audioChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true
            });

        _reader = reader;
        _writer = PipeWriter.Create(client.GetStream());
    }

    /// <summary>Start the background read loop (called by the server after session creation).</summary>
    internal void StartReadLoop() =>
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));

    /// <summary>Read incoming audio frames from Asterisk.</summary>
    /// <remarks>
    /// The sequence <em>ends</em> — it does not fault — whenever the session ends: a hangup or error
    /// frame from the far end, a socket EOF, <see cref="HangupAsync"/>, or a disposal that lands
    /// while enumeration is under way. That holds whatever the ordering, and frames already received
    /// are still delivered. Two things do fault, separated by who ended the session: a cancelled
    /// <paramref name="ct"/> raises <see cref="OperationCanceledException"/> at the next iteration
    /// boundary, and calling this after the owner disposed the session raises
    /// <see cref="ObjectDisposedException"/> from the call itself. See ADR-0053.
    /// </remarks>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(CancellationToken ct = default)
    {
        // An iterator body would defer this to the first MoveNextAsync, which is the whole defect.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _consumerDisposed) == 1, this);
        return ReadAudioCoreAsync(ct);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioCoreAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // No session state is read here: `_cts` may already be disposed by the time this body runs,
        // and the channel's completion is what says the session ended.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_audioChannel.Reader.TryRead(out var chunk))
            {
                yield return chunk; // drain what arrived before the ending
                continue;
            }

            if (!await _audioChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                yield break; // the channel completed: the session ended
        }
    }

    /// <summary>Write PCM audio back to Asterisk (e.g., TTS output).</summary>
    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken ct = default) =>
        WriteAudioAsync(pcmData, AudioSocketFrameType.Audio, ct);

    /// <summary>
    /// Write PCM audio back to Asterisk using the specified audio frame type.
    /// Use <see cref="AudioSocketFrameType.Audio"/> (8 kHz) for standard Asterisk configurations,
    /// or a high-rate type (e.g., <see cref="AudioSocketFrameType.AudioSlin16"/>) for Asterisk 23+.
    /// </summary>
    /// <remarks>
    /// Frames are written one at a time: a write waits for any other write or hangup in flight on the
    /// same session, so every frame reaches the far end whole.
    /// </remarks>
    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcmData, AudioSocketFrameType frameType, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: a hangup that held it may have ended the session meanwhile.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            AudioSocketFrameCodec.WriteFrame(_writer, frameType, pcmData.Span);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        AudioSocketMetrics.FramesSent.Add(1);
        AudioSocketMetrics.BytesSent.Add(pcmData.Length);
    }

    /// <summary>
    /// Signal hangup to Asterisk: writes one hangup frame, then ends the session. On Asterisk 20 and
    /// later, app_audiosocket reads the hangup frame as the end of the AudioSocket turn and the call
    /// continues in the dialplan, where a bare close with the caller's audio still unread resets the
    /// connection and fails the call. On Asterisk 18 any end from the server fails the application,
    /// frame or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent. On a session that has already ended — the far end hung up or closed, the session was
    /// hung up before, or its owner disposed it — the call completes and writes nothing. When the
    /// transport fails under the frame (the far end is already gone, or a teardown raced this call) the
    /// frame is skipped and the session still ends.
    /// </para>
    /// <para>
    /// A hangup issued while an audio write is in flight waits for that write to finish, so the far end
    /// reads the whole audio frame and then the whole hangup frame, never parts of both. Cancelling
    /// <paramref name="ct"/> while it waits, or while the frame is flushed, abandons the hangup with an
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// </remarks>
    public ValueTask HangupAsync(CancellationToken ct = default) => EndCoreAsync(ct);

    /// <summary>
    /// Ends the connection from the server's side, exactly as <see cref="HangupAsync"/> does: a hangup
    /// frame if the session is still live, then the teardown every ending runs.
    /// </summary>
    internal ValueTask EndFromServerAsync() => EndCoreAsync(CancellationToken.None);

    private async ValueTask EndCoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return; // already ended, by whichever side: nothing to tell the far end, nothing to release

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: the session may have ended while this call waited for a write.
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    AudioSocketFrameCodec.WriteFrame(_writer, AudioSocketFrameType.Hangup, []);
                    await _writer.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (IOException) { /* the far end is already gone: nothing to tell it */ }
                catch (ObjectDisposedException) { /* a teardown raced this one: the transport is closed */ }
            }
        }
        finally
        {
            _writeLock.Release();
        }

        await TerminateAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame))
                {
                    switch (frame.Type)
                    {
                        case AudioSocketFrameType.Audio:
                        case AudioSocketFrameType.AudioSlin12:
                        case AudioSocketFrameType.AudioSlin16:
                        case AudioSocketFrameType.AudioSlin24:
                        case AudioSocketFrameType.AudioSlin32:
                        case AudioSocketFrameType.AudioSlin44:
                        case AudioSocketFrameType.AudioSlin48:
                        case AudioSocketFrameType.AudioSlin96:
                        case AudioSocketFrameType.AudioSlin192:
                            AudioSocketMetrics.FramesReceived.Add(1);
                            AudioSocketMetrics.BytesReceived.Add(frame.Payload.Length);
                            var copy = frame.Payload.ToArray();
                            // DropOldest, so this never blocks; TryWrite also tolerates a channel
                            // the teardown has already completed, where WriteAsync would throw.
                            _audioChannel.Writer.TryWrite(copy);
                            break;

                        case AudioSocketFrameType.Hangup:
                        case AudioSocketFrameType.Error:
                            return; // the finally tears the transport down and fires the hangup

                        case AudioSocketFrameType.Uuid:
                        case AudioSocketFrameType.Dtmf:
                        default:
                            // Recognised and skipped by its own declared length, which
                            // TryReadFrame has already done. Nothing here resynchronises the
                            // stream on a frame this session has no behaviour for.
                            break;
                    }
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            AudioSocketLog.ReadLoopEnded(_logger, ex, ChannelId);
        }
        finally
        {
            // Every ending lands here — hangup frame, error frame, EOF, transport error, or the
            // cancellation a disposal raises. Terminate first so OnHangup subscribers observe a
            // session that has genuinely released its transport (ADR-0053).
            await TerminateAsync().ConfigureAwait(false);
            FireHangup();
        }
    }

    private void FireHangup()
    {
        if (Interlocked.CompareExchange(ref _hangupFired, 1, 0) == 0)
        {
            try
            {
                _audioChannel.Writer.TryComplete();
                OnHangup?.Invoke();
            }
            finally
            {
                // The release follows every OnHangup handler, the consumer's included, and precedes
                // HungUp: a connection presenting this channel id waits on HungUp, so it must find the
                // entry already gone when it wakes, and must not be announced while a consumer keyed by
                // channel id is still handling this session's hangup. A throwing handler still releases.
                try
                {
                    Released?.Invoke();
                }
                finally
                {
                    _hungUp.TrySetResult();
                }
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Disposal is the owner's statement that it is finished with the session; afterwards
    /// <see cref="ReadAudioAsync"/> throws like every other member. A session that ends on its own
    /// runs the same teardown without setting that flag — see ADR-0053.</remarks>
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _consumerDisposed, 1);
        return TerminateAsync();
    }

    /// <summary>
    /// Release the transport. Idempotent, and shared by the owner's disposal and by every
    /// session-initiated ending.
    /// </summary>
    private async ValueTask TerminateAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        _audioChannel.Writer.TryComplete(); // a parked consumer ends here, not on a token
        _client.Dispose();                  // must stay last: the FIN it emits is a test ordering edge
    }
}
