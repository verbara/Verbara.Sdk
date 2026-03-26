using System.Net.WebSockets;
using System.Reactive.Linq;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class WebSocketAudioSessionTests
{
    private static WebSocketAudioSession CreateSession(FakeWebSocket ws, string channelId = "ch-1", string format = "slin16")
        => new(ws, channelId, format);

    // -- Constructor / Properties ------------------------------------------------

    [Fact]
    public void Constructor_ShouldSetProperties_WhenCreated()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws, "chan-42", "slin16");

        sut.ChannelId.Should().Be("chan-42");
        sut.Format.Should().Be("slin16");
        sut.SampleRate.Should().Be(16000);
        sut.IsConnected.Should().BeTrue();
    }

    // -- FormatToSampleRate branches --------------------------------------------

    [Theory]
    [InlineData("ulaw", 8000)]
    [InlineData("alaw", 8000)]
    [InlineData("g711", 8000)]
    [InlineData("slin", 8000)]
    [InlineData("slin/8000", 8000)]
    [InlineData("slin8", 8000)]
    [InlineData("slin16", 16000)]
    [InlineData("slin/16000", 16000)]
    [InlineData("g722", 16000)]
    [InlineData("slin32", 32000)]
    [InlineData("slin/32000", 32000)]
    [InlineData("slin48", 48000)]
    [InlineData("slin/48000", 48000)]
    [InlineData("opus", 48000)]
    [InlineData("unknown-codec", 8000)]
    public void Constructor_ShouldMapSampleRate_WhenFormatProvided(string format, int expectedRate)
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws, format: format);

        sut.SampleRate.Should().Be(expectedRate);
    }

    // -- ReadFrameAsync ---------------------------------------------------------

    [Fact]
    public async Task ReadFrameAsync_ShouldReturnData_WhenBinaryFrameReceived()
    {
        using var ws = new FakeWebSocket();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        ws.EnqueueReceive(payload);
        ws.EnqueueClose();

        var sut = CreateSession(ws);
        sut.Start();

        var frame = await sut.ReadFrameAsync();

        frame.ToArray().Should().Equal(payload);
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldReturnEmpty_WhenChannelCompleted()
    {
        using var ws = new FakeWebSocket();
        ws.EnqueueClose();

        var sut = CreateSession(ws);
        sut.Start();

        // Give the read pump time to process the close frame
        await Task.Delay(50);

        var frame = await sut.ReadFrameAsync();

        frame.IsEmpty.Should().BeTrue();
        await sut.DisposeAsync();
    }

    // -- ReadPump state transitions ---------------------------------------------

    [Fact]
    public async Task ReadPump_ShouldTransitionToDisconnected_WhenCloseFrameReceived()
    {
        using var ws = new FakeWebSocket();
        ws.EnqueueClose();

        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();
        using var sub = sut.StateChanges.Subscribe(s => states.Add(s));

        sut.Start();
        await Task.Delay(100);

        states.Should().Contain(AudioStreamState.Disconnected);
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task ReadPump_ShouldTransitionToDisconnected_WhenCancelled()
    {
        using var ws = new FakeWebSocket();
        // No data enqueued — pump will block until cancelled

        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();
        using var sub = sut.StateChanges.Subscribe(s => states.Add(s));

        sut.Start();
        await sut.DisposeAsync();

        states.Should().Contain(AudioStreamState.Disconnected);
    }

    // -- WriteFrameAsync --------------------------------------------------------

    [Fact]
    public async Task WriteFrameAsync_ShouldSendBinaryData()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws);
        var data = new byte[] { 10, 20, 30 };

        await sut.WriteFrameAsync(data);

        ws.SentData.Should().ContainSingle()
            .Which.Should().Equal(data);
        await sut.DisposeAsync();
    }

    // -- DisposeAsync -----------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_ShouldTransitionToDisconnected()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();
        using var sub = sut.StateChanges.Subscribe(s => states.Add(s));

        await sut.DisposeAsync();

        states.Should().Contain(AudioStreamState.Disconnected);
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws);

        await sut.DisposeAsync();

        // Second dispose should not throw
        var exception = await Record.ExceptionAsync(async () => await sut.DisposeAsync());
        exception.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_ShouldCloseWebSocket_WhenOpen()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws);

        await sut.DisposeAsync();

        ws.State.Should().Be(WebSocketState.Closed);
    }

    [Fact]
    public void IsConnected_ShouldReturnFalse_WhenWebSocketClosed()
    {
        using var ws = new FakeWebSocket();
        ws.Close();
        var sut = CreateSession(ws);

        sut.IsConnected.Should().BeFalse();
    }

    // -- FakeWebSocket ----------------------------------------------------------

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _receiveQueue = new();
        private readonly List<byte[]> _sentData = [];
        private WebSocketState _state = WebSocketState.Open;

        public IReadOnlyList<byte[]> SentData => _sentData;

        public void EnqueueReceive(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary, bool endOfMessage = true)
            => _receiveQueue.Enqueue((type, data, endOfMessage));

        public void EnqueueClose()
            => _receiveQueue.Enqueue((WebSocketMessageType.Close, [], true));

        public void Close() => _state = WebSocketState.Closed;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_receiveQueue.Count == 0)
            {
                // Block until cancelled
                return new ValueTask<ValueWebSocketReceiveResult>(
                    Task.Delay(Timeout.Infinite, ct).ContinueWith<ValueWebSocketReceiveResult>(
                        _ => throw new OperationCanceledException(ct),
                        ct));
            }

            var (type, data, eom) = _receiveQueue.Dequeue();
            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var count = Math.Min(data.Length, buffer.Length);
            data.AsMemory(0, count).CopyTo(buffer);
            return new(new ValueWebSocketReceiveResult(count, type, eom));
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            _sentData.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => throw new NotSupportedException("Use Memory overload");

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
            => throw new NotSupportedException("Use Memory overload");

        public override void Abort() => _state = WebSocketState.Aborted;

#pragma warning disable IDISP010 // WebSocket.Dispose is abstract — no base to call
        public override void Dispose() => _state = WebSocketState.Closed;
#pragma warning restore IDISP010
    }
}
