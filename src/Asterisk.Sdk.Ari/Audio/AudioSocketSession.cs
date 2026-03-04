using System.Buffers;
using System.IO.Pipelines;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Channels;

namespace Asterisk.Sdk.Ari.Audio;

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
    private Task? _readPumpTask;
    private Task? _pipeFillTask;
    private volatile bool _disposed;

    public string ChannelId { get; private set; } = string.Empty;
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => !_disposed && _state.Value == AudioStreamState.Connected;
    public IObservable<AudioStreamState> StateChanges => _state;

    internal AudioSocketSession(Stream stream, string format)
    {
        _stream = stream;
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
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
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

                        case AudioFrameType.Silence:
                            // Silence frame — enqueue empty to signal silence
                            _audioInChannel.Writer.TryWrite(ReadOnlyMemory<byte>.Empty);
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
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            await reader.CompleteAsync();
            _audioInChannel.Writer.TryComplete();
            if (_state.Value == AudioStreamState.Connected)
                _state.OnNext(AudioStreamState.Disconnected);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (await _audioInChannel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_audioInChannel.Reader.TryRead(out var frame))
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
            return new Guid(bytes).ToString();
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
