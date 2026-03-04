using System.Buffers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Threading.Channels;

namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// A single WebSocket audio connection. Receives binary frames via ManagedWebSocket.
/// </summary>
internal sealed class WebSocketAudioSession : IAudioStream
{
    private readonly WebSocket _webSocket;
    private readonly BehaviorSubject<AudioStreamState> _state = new(AudioStreamState.Connected);
    private readonly Channel<ReadOnlyMemory<byte>> _audioInChannel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readPumpTask;
    private volatile bool _disposed;

    public string ChannelId { get; }
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => !_disposed && _webSocket.State == WebSocketState.Open;
    public IObservable<AudioStreamState> StateChanges => _state;

    internal WebSocketAudioSession(WebSocket webSocket, string channelId, string format)
    {
        _webSocket = webSocket;
        ChannelId = channelId;
        Format = format;
        SampleRate = FormatToSampleRate(format);

        _audioInChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    internal void Start()
    {
        _readPumpTask = Task.Run(() => ReadPumpAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ReadPumpAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
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

                if (result.MessageType == WebSocketMessageType.Binary && bufferWriter.WrittenCount > 0)
                {
                    _audioInChannel.Writer.TryWrite(bufferWriter.WrittenMemory.ToArray());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _audioInChannel.Writer.TryComplete();
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
        await _webSocket.SendAsync(audioData, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
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

        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* Best effort */ }
        }

        if (_readPumpTask is not null)
            await _readPumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _state.OnNext(AudioStreamState.Disconnected);
        _state.OnCompleted();
        _state.Dispose();
        _webSocket.Dispose();
        _cts.Dispose();
    }
}
