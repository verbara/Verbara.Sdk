using System.Buffers;
using System.IO.Pipelines;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class AudioSocketSessionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] Transport failed under a live session, which ends as Disconnected: channel_id={ChannelId}")]
    public static partial void TransportFailed(ILogger logger, Exception exception, string channelId);
}

/// <summary>
/// A single AudioSocket connection. Read pump parses frames via AudioSocketProtocol,
/// audio frames are queued for consumer via Channel&lt;T&gt;.
/// </summary>
internal sealed class AudioSocketSession : IAudioStream
{
    private readonly Stream _stream;
    private readonly Pipe _inputPipe;
    private readonly BehaviorSubject<AudioStreamState> _state = new(AudioStreamState.Connecting);
    private readonly Channel<ReadOnlyMemory<byte>> _audioInChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Exception? _transportFailure;
    private Task? _readPumpTask;
    private Task? _pipeFillTask;
    private volatile bool _disposed;

    public string ChannelId { get; private set; } = string.Empty;
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => !_disposed && _state.Value == AudioStreamState.Connected;
    public IObservable<AudioStreamState> StateChanges => _state;

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
                            ChannelId = ParseUuid(payload);
                            _state.OnNext(AudioStreamState.Connected);
                            break;

                        case AudioFrameType.Audio:
                            _audioInChannel.Writer.TryWrite(payload.ToArray());
                            break;

                        case AudioFrameType.Hangup:
                            _state.OnNext(AudioStreamState.Disconnected);
                            _audioInChannel.Writer.TryComplete();
                            return;

                        case AudioFrameType.Error:
                            _state.OnNext(AudioStreamState.Error);
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
        // with the failure recorded for the report below. The one other source of an IOException
        // here is a consumer's `StateChanges` observer, which leaves the loop as it would with an
        // exception of any other type.
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
            if (endsLive && failure is not null && !_disposed)
            {
                AudioSocketSessionLog.TransportFailed(_logger, failure, ChannelId);
                AudioStreamMetrics.TransportFailures.Add(1);
            }

            _audioInChannel.Writer.TryComplete();
            if (endsLive)
                _state.OnNext(AudioStreamState.Disconnected);
        }
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
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        _audioInChannel.Writer.TryComplete();

        if (_pipeFillTask is not null)
            await _pipeFillTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_readPumpTask is not null)
            await _readPumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _state.OnNext(AudioStreamState.Disconnected);
        _state.OnCompleted();
        _state.Dispose();
        _cts.Dispose();

        await _stream.DisposeAsync();
    }
}
