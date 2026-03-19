using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket.Internal;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.AudioSocket;

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
    private volatile bool _disposed;

    /// <summary>UUID received from Asterisk (from the first UUID frame).</summary>
    public Guid ChannelId { get; }

    /// <summary>Remote endpoint address.</summary>
    public string RemoteEndpoint { get; }

    /// <summary>Audio format of incoming audio.</summary>
    public AudioFormat InputFormat { get; }

    /// <summary>Whether the session is still connected.</summary>
    public bool IsConnected => !_disposed && !_cts.IsCancellationRequested;

    /// <summary>Raised when the channel hangs up (from either side).</summary>
    public event Action? OnHangup;

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
                SingleReader = false
            });

        _reader = reader;
        _writer = PipeWriter.Create(client.GetStream());
    }

    /// <summary>Start the background read loop (called by the server after session creation).</summary>
    internal void StartReadLoop() =>
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));

    /// <summary>Read incoming audio frames from Asterisk. Completes when the session ends.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Write PCM audio back to Asterisk (e.g., TTS output).</summary>
    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioSocketFrameCodec.WriteFrame(_writer, AudioSocketFrameType.Audio, pcmData.Span);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Send a silence indication frame.</summary>
    public async ValueTask WriteSilenceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> payload = stackalloc byte[2];
        payload[0] = 0;
        payload[1] = 0;
        AudioSocketFrameCodec.WriteFrame(_writer, AudioSocketFrameType.Silence, payload);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Signal hangup to Asterisk.</summary>
    public async ValueTask HangupAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioSocketFrameCodec.WriteFrame(_writer, AudioSocketFrameType.Hangup, []);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
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
                            var copy = frame.Payload.ToArray();
                            await _audioChannel.Writer.WriteAsync(copy, ct).ConfigureAwait(false);
                            break;

                        case AudioSocketFrameType.Hangup:
                        case AudioSocketFrameType.Error:
                            _audioChannel.Writer.TryComplete();
                            OnHangup?.Invoke();
                            await DisposeAsync().ConfigureAwait(false);
                            return;

                        case AudioSocketFrameType.Silence:
                        case AudioSocketFrameType.Uuid:
                        default:
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
            _audioChannel.Writer.TryComplete();
            OnHangup?.Invoke();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        _client.Dispose();
    }
}
