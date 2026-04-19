// Architectural note: this session implements IChanWebSocketSession (sub-interface of IAudioStream)
// instead of folding chan_websocket-specific methods into IAudioStream, because the AudioSocket
// TCP protocol doesn't share the text-frame control channel. Consumers cast the IAudioStream
// returned by IAudioServer.GetStream when they know they're talking to chan_websocket.
using System.Buffers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Channels;

namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// A single WebSocket audio connection. Receives binary frames via ManagedWebSocket
/// (audio payload) and text frames parsed as <see cref="ChanWebSocketControlMessage"/>.
/// </summary>
internal sealed class WebSocketAudioSession : IChanWebSocketSession
{
    private readonly WebSocket _webSocket;
    private readonly BehaviorSubject<AudioStreamState> _state = new(AudioStreamState.Connected);
    private readonly Subject<ChanWebSocketControlMessage> _controlSubject = new();
    private readonly Channel<ReadOnlyMemory<byte>> _audioInChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _readPumpTask;
    private volatile bool _disposed;

    public string ChannelId { get; }
    public string Format { get; }
    public int SampleRate { get; }
    public bool IsConnected => !_disposed && _webSocket.State == WebSocketState.Open;
    public IObservable<AudioStreamState> StateChanges => _state;
    public IObservable<ChanWebSocketControlMessage> ControlMessages => _controlSubject;

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

                if (bufferWriter.WrittenCount == 0)
                    continue;

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Binary:
                        _audioInChannel.Writer.TryWrite(bufferWriter.WrittenMemory.ToArray());
                        break;

                    case WebSocketMessageType.Text:
                        TryDispatchControlMessage(bufferWriter.WrittenSpan);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Pump cancelled — normal shutdown path.
        }
        catch (WebSocketException)
        {
            // Peer closed or transport error — state transitions to Disconnected in finally.
        }
        finally
        {
            _audioInChannel.Writer.TryComplete();
            _state.OnNext(AudioStreamState.Disconnected);
        }
    }

    private void TryDispatchControlMessage(ReadOnlySpan<byte> utf8Json)
    {
        ChanWebSocketControlMessage? message;
        try
        {
            message = ChanWebSocketControlMessageSerializer.Deserialize(utf8Json);
        }
        catch (JsonException)
        {
            // Malformed or unknown-discriminator JSON: drop silently (logged at server level in future).
            return;
        }

        if (message is not null)
            _controlSubject.OnNext(message);
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
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(audioData, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
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
        _controlSubject.OnCompleted();
        _controlSubject.Dispose();
        _webSocket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
