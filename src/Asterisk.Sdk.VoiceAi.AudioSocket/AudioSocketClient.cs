using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Asterisk.Sdk.VoiceAi.AudioSocket.Internal;

namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>
/// TCP client that simulates Asterisk's AudioSocket behavior.
/// Sends a UUID frame on connect, then streams PCM audio frames.
/// Use for testing without a real Asterisk instance.
/// </summary>
public sealed class AudioSocketClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Guid _channelId;
    private TcpClient? _client;
    private PipeWriter? _writer;
    private PipeReader? _reader;

    /// <summary>Creates a new client that will connect to the given server.</summary>
    public AudioSocketClient(string host, int port, Guid channelId)
    {
        _host = host;
        _port = port;
        _channelId = channelId;
    }

    /// <summary>Connect and send UUID frame.</summary>
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        var stream = _client.GetStream();
        _writer = PipeWriter.Create(stream);
        _reader = PipeReader.Create(stream);

        // Send UUID frame in big-endian network byte order
        Span<byte> uuidBytes = stackalloc byte[16];
        _channelId.TryWriteBytes(uuidBytes, bigEndian: true, out _);
        AudioSocketFrameCodec.WriteFrame(_writer, AudioSocketFrameType.Uuid, uuidBytes);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Send a PCM audio frame to the server.</summary>
    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken ct = default)
    {
        EnsureConnected();
        AudioSocketFrameCodec.WriteFrame(_writer!, AudioSocketFrameType.Audio, pcmData.Span);
        await _writer!.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Send a hangup frame to the server.</summary>
    public async ValueTask SendHangupAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        AudioSocketFrameCodec.WriteFrame(_writer!, AudioSocketFrameType.Hangup, []);
        await _writer!.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Read audio frames echoed back from the server. Completes on hangup or cancellation.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAudioAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureConnected();
        while (!ct.IsCancellationRequested)
        {
            var result = await _reader!.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            bool stop = false;

            while (AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame))
            {
                if (frame.Type == AudioSocketFrameType.Audio)
                    yield return frame.Payload;
                else if (frame.Type is AudioSocketFrameType.Hangup or AudioSocketFrameType.Error)
                    stop = true;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (stop || result.IsCompleted) yield break;
        }
    }

    private void EnsureConnected()
    {
        if (_client is null || !_client.Connected)
            throw new InvalidOperationException("Client is not connected. Call ConnectAsync first.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.CompleteAsync().ConfigureAwait(false);
        if (_reader is not null)
            await _reader.CompleteAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
    }
}
