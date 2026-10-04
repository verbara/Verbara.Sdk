using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class AudioSocketSessionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] Transport failed under a live session, which ends as Disconnected: channel_id={ChannelId}")]
    public static partial void TransportFailed(ILogger logger, Exception exception, string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] A second identification frame was ignored: channel_id={ChannelId} ignored_id={IgnoredId}")]
    public static partial void SecondIdentificationIgnored(ILogger logger, string channelId, string ignoredId);
}

/// <summary>
/// A single AudioSocket connection. Read pump parses frames via AudioSocketProtocol,
/// audio frames are queued for consumer via Channel&lt;T&gt;.
/// </summary>
internal sealed class AudioSocketSession : IAudioStream
{
    private readonly Stream _stream;
    private readonly Pipe _inputPipe;
    private readonly AudioStreamStateChannel _state;
    private readonly TaskCompletionSource<bool> _identified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ReadOnlyMemory<byte>> _audioInChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Exception? _transportFailure;
    private Task? _readPumpTask;
    private Task? _pipeFillTask;
    private int _disposed;
    private bool _secondIdentificationReported;

    /// <summary>The id of the connection's first identification frame; empty until it arrives, and never changed after.</summary>
    public string ChannelId { get; private set; } = string.Empty;
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _state.Value == AudioStreamState.Connected;
    public IObservable<AudioStreamState> StateChanges => _state;

    /// <summary>
    /// Completes when the session's ending is published, before any observer is notified of it: the
    /// signal its server waits on instead of the consumers' observable.
    /// </summary>
    internal Task Ended => _state.Ended;

    /// <summary>
    /// Completes <see langword="true"/> at the first identification frame that names a non-empty id,
    /// and <see langword="false"/> when the read ends before one: the signal the server's
    /// identification wait ends on.
    /// </summary>
    internal Task<bool> Identified => _identified.Task;

    /// <param name="stream">The connection's stream, read until it ends.</param>
    /// <param name="format">The audio format the connection carries.</param>
    /// <param name="logger">
    /// Where a transport failure under a live session is reported. Required, not optional: a call
    /// site that forgot it would compile and silently lose the only line that tells a reset from a
    /// hangup. The server passes its own logger, so the line carries the server's category.
    /// </param>
    internal AudioSocketSession(Stream stream, string format, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
        _state = new AudioStreamStateChannel(AudioStreamState.Connecting, logger, () => ChannelId);
        Format = format;
        SampleRate = FormatToSampleRate(format);

        var pipeOptions = new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            pauseWriterThreshold: 512 * 1024,
            resumeWriterThreshold: 256 * 1024,
            readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.Inline,
            useSynchronizationContext: false);

        _inputPipe = new Pipe(pipeOptions);
        _audioInChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    internal void Start()
    {
        var ct = _cts.Token;
        _pipeFillTask = Task.Run(() => FillPipeAsync(ct), CancellationToken.None);
        _readPumpTask = Task.Run(() => ReadPumpAsync(ct), CancellationToken.None);
    }

    /// <summary>Reads from the network stream into the input pipe.</summary>
    private async Task FillPipeAsync(CancellationToken ct)
    {
        var writer = _inputPipe.Writer;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(4096);
                var bytesRead = await _stream.ReadAsync(memory, ct);
                if (bytesRead == 0) break;

                writer.Advance(bytesRead);
                var result = await writer.FlushAsync(ct);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (OperationCanceledException)
        {
            // The session's own teardown. `ct` is `_cts.Token`, a source this type creates and that
            // only `DisposeAsync` cancels — directly, or through the server's `StopAsync` walking its
            // sessions — so a cancellation here is the session being disposed, never a read that
            // failed. The `finally` still completes the pipe writer, which is what lets the read pump
            // on the other end of the pipe finish instead of waiting for bytes that stopped coming.
            /* Best effort — the session is being disposed */
        }
        catch (IOException ex)
        {
            // The transport failed: the connection was reset, or the read otherwise broke. It is
            // recorded here and reported where the session ends, not here, because only the read
            // pump knows whether this ended a live session: a connection that never identified
            // itself is no session, and a hangup frame already read ahead of the failure has ended
            // the session as a hangup. The write happens before the writer is completed below, and
            // the read pump reads it only after the pipe reports that completion, so it sees it.
            Volatile.Write(ref _transportFailure, ex);
        }
        finally
        {
            // Completed without the exception on purpose: completing it with one would make the read
            // pump's next read throw instead of returning what is still buffered in the pipe. The
            // reader runs inline on each flush, so bytes are left buffered only when the failure
            // lands before the read pump's first read, and there the identification frame and the
            // audio behind it would be lost.
            await writer.CompleteAsync();
        }
    }

    /// <summary>Reads frames from the input pipe and dispatches them.</summary>
    private async Task ReadPumpAsync(CancellationToken ct)
    {
        var reader = _inputPipe.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                var sequenceReader = new SequenceReader<byte>(buffer);
                while (AudioSocketProtocol.TryParseFrame(ref sequenceReader, out var frameType, out var payload))
                {
                    switch (frameType)
                    {
                        case AudioFrameType.Uuid:
                            Identify(ParseUuid(payload));
                            break;

                        case AudioFrameType.Audio:
                            _audioInChannel.Writer.TryWrite(payload.ToArray());
                            break;

                        case AudioFrameType.Hangup:
                            _state.TryEnd(error: false);
                            _audioInChannel.Writer.TryComplete();
                            return;

                        case AudioFrameType.Error:
                            // Error and then Disconnected, in this one step: the disposal publishes the
                            // ending only when no one has, so a Disconnected left to it would be lost.
                            _state.TryEnd(error: true);
                            _audioInChannel.Writer.TryComplete();
                            return;
                    }
                }

                reader.AdvanceTo(sequenceReader.Position, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException)
        {
            // The same teardown as `FillPipeAsync`'s: `ct` is `_cts.Token`, owned by this type and
            // cancelled only by `DisposeAsync`, so it is the session being disposed and not a frame
            // that failed to parse. The `finally` still completes the reader, closes the audio channel
            // and moves a still-connected session to Disconnected, which is the ending a consumer
            // waiting on `StateChanges` is watching for.
            /* Best effort — the session is being disposed */
        }
        // No IOException arm: the fill loop completes the pipe writer without an exception, so a
        // transport failure never reaches `reader.ReadAsync` as one. It arrives as the pipe's end,
        // with the failure recorded for the report below. A consumer's `StateChanges` observer cannot
        // throw into this loop either: the state channel's guard catches and logs what it throws.
        finally
        {
            await reader.CompleteAsync();

            // A session still Connected here is ending without a hangup or error frame. If the fill
            // loop recorded a transport failure, and the owner is not the one tearing the session
            // down, the transport broke under a live call: report it before either ending a consumer
            // can observe, the audio channel's completion and Disconnected, so whoever reacts to the
            // ending already finds the line and the count. The ending itself stays Disconnected.
            var endsLive = _state.Value == AudioStreamState.Connected;
            var failure = Volatile.Read(ref _transportFailure);
            if (endsLive && failure is not null && Volatile.Read(ref _disposed) == 0)
            {
                AudioSocketSessionLog.TransportFailed(_logger, failure, ChannelId);
                AudioStreamMetrics.TransportFailures.Add(1);
            }

            _audioInChannel.Writer.TryComplete();
            if (endsLive)
                _state.TryEnd(error: false);

            // A read that ended before any identification frame: the server's wait ends now, and the
            // connection gives its place back instead of holding it until the idle deadline.
            _identified.TrySetResult(false);
        }
    }

    /// <summary>
    /// The identification frame's case. The first frame that names a non-empty id sets
    /// <see cref="ChannelId"/>, publishes Connected and ends the server's wait; the id a server registered
    /// the session under therefore never changes. A later frame is ignored, with one Warning per
    /// session naming both ids. A frame whose payload names nothing (a zero-length payload parses to
    /// an empty id) identifies nothing.
    /// </summary>
    private void Identify(string id)
    {
        if (ChannelId.Length == 0)
        {
            if (id.Length == 0)
                return;

            ChannelId = id;
            _state.PublishConnected();
            _identified.TrySetResult(true);
            return;
        }

        if (_secondIdentificationReported)
            return;

        _secondIdentificationReported = true;
        AudioSocketSessionLog.SecondIdentificationIgnored(_logger, ChannelId, id);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (await _audioInChannel.Reader.WaitToReadAsync(cancellationToken)
            && _audioInChannel.Reader.TryRead(out var frame))
        {
            return frame;
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[AudioSocketProtocol.HeaderSize + audioData.Length];
        var writer = new ArrayBufferWriter<byte>(buffer.Length);
        AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Audio, audioData.Span);
        await _stream.WriteAsync(writer.WrittenMemory, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private static string ParseUuid(ReadOnlySequence<byte> payload)
    {
        if (payload.Length >= 16)
        {
            Span<byte> bytes = stackalloc byte[16];
            payload.Slice(0, 16).CopyTo(bytes);
            // RFC 4122 order, most significant byte first — which is how Asterisk puts the
            // dialplan's UUID on the wire, and what the capture shows. Guid's own layout is
            // little-endian for the first three fields, so without bigEndian: true the first
            // eleven characters of every channel id come out reversed.
            return new Guid(bytes, bigEndian: true).ToString();
        }

        // Fallback: treat as UTF-8 string
        return Encoding.UTF8.GetString(payload);
    }

    private static int FormatToSampleRate(string format) => format.ToLowerInvariant() switch
    {
        "slin16" or "slin/16000" => 16000,
        "slin" or "slin/8000" or "slin8" => 8000,
        "slin32" or "slin/32000" => 32000,
        "slin48" or "slin/48000" => 48000,
        "ulaw" or "alaw" or "g711" => 8000,
        "g722" => 16000,
        "opus" => 48000,
        _ => 8000
    };

    public async ValueTask DisposeAsync()
    {
        // Atomic: three parties can dispose one session — the handler's `await using`, the server's
        // stop walking its sessions, and a consumer holding it as an IAudioStream.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();
        _audioInChannel.Writer.TryComplete();

        if (_pipeFillTask is not null)
            await _pipeFillTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_readPumpTask is not null)
            await _readPumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Publishes the ending only if the pump did not; then completes the sequence.
        _state.TryEnd(error: false);
        _state.Dispose();
        _cts.Dispose();

        await _stream.DisposeAsync();
    }
}
