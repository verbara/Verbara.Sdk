using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A WebSocket stream whose consumer falls behind: each frame its full receive buffer discards is counted on
/// <c>audio.frames.dropped</c>. Every test drives the real session over a real loopback socket, whose far end is a
/// <see cref="RawWebSocketPeer"/>.
/// </summary>
/// <remarks>
/// <para>
/// No wait is a delay. The read pump handles frames in the order they arrive, so once the captured control message
/// sent after the audio reaches a <c>ControlMessages</c> subscriber, every audio frame before it was counted and written
/// to the buffer.
/// </para>
/// <para>
/// The instruments are process-wide, so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class WebSocketFrameDropTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // The published names: the contract an exporter sees. Literals on purpose, so a rename is a red here.
    private const string MeterName = "Verbara.Sdk.Ari.Audio";
    private const string TransportTag = "audio.transport";
    private const string WebSocketTransport = "websocket";
    private const string FramesDropped = "audio.frames.dropped";
    private const string FramesReceived = "audio.frames.received";

    /// <summary>The session's receive buffer: the frames it keeps for a consumer that reads nothing.</summary>
    private const int BufferCapacity = 1000;

    private const int AudioFrameBytes = 640;

    private static readonly ChanWebSocketCapturedMessage CapturedDtmf = ChanWebSocketWireCapture.ControlMessages.First(
        m => m.Event == "DTMF_END" && m.Format == ChanWebSocketCaptureFormat.PlainText);

    [Fact]
    public async Task ReadFrameAsync_ShouldCountFiveDroppedTaggedWebsocketAndReadTheNewest_WhenFiveFramesArriveOnAFullBuffer()
    {
        // Arrange — a live stream whose consumer reads nothing
        using var recorder = new AudioMeasurementRecorder(FramesDropped, FramesReceived);
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await DropConnection.OpenAsync(bound.Token);
        const int sent = BufferCapacity + 5;

        // Act — the buffer fills, then 5 more frames arrive
        for (var index = 0; index < sent; index++)
            await connection.Peer.SendBinaryAsync(NumberedFrame(index));
        await connection.SendMarkerAndWaitAsync(bound.Token);
        var read = new List<int>(BufferCapacity);
        for (var i = 0; i < BufferCapacity; i++)
        {
            var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);
            read.Add(BinaryPrimitives.ReadInt32LittleEndian(frame.Span));
        }

        // Assert
        using (new AssertionScope(Summary(recorder)))
        {
            recorder.Total(FramesDropped).Should().Be(5, "each frame the full buffer discards is counted once");
            recorder.Of(FramesDropped).Should().OnlyContain(
                m => Equals(m.Tag(TransportTag), WebSocketTransport), "a drop carries the transport it came through");
            recorder.Total(FramesReceived).Should().Be(sent, "every frame that arrived is counted as received, dropped or not");
            read.Should().Equal(Enumerable.Range(sent - BufferCapacity, BufferCapacity),
                "the buffer discards its oldest frame, so the consumer reads the 1000 newest, in order");
        }
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldNotMoveTheDropCount_WhenTheConsumerReadsEachFrame()
    {
        // Arrange — a live stream whose consumer reads each frame as it arrives
        using var recorder = new AudioMeasurementRecorder(FramesDropped, FramesReceived);
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var connection = await DropConnection.OpenAsync(bound.Token);
        const int sent = 50;

        // Act
        var read = new List<int>(sent);
        for (var index = 0; index < sent; index++)
        {
            await connection.Peer.SendBinaryAsync(NumberedFrame(index));
            var frame = await connection.Session.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token);
            read.Add(BinaryPrimitives.ReadInt32LittleEndian(frame.Span));
        }

        await connection.SendMarkerAndWaitAsync(bound.Token);

        // Assert
        using (new AssertionScope(Summary(recorder)))
        {
            IsPublished(FramesDropped).Should().BeTrue("the drop count exists on the meter, so a zero is a measurement, not an absence");
            recorder.Of(FramesDropped).Should().BeEmpty("a buffer that never fills discards nothing");
            recorder.Total(FramesReceived).Should().Be(sent);
            read.Should().Equal(Enumerable.Range(0, sent), "the consumer read every frame");
        }
    }

    /// <summary>The totals recorded, for a failure message: one line, not one entry per frame.</summary>
    private static string Summary(AudioMeasurementRecorder recorder) =>
        $"{FramesDropped}={recorder.Total(FramesDropped)} in {recorder.Of(FramesDropped).Count} measurements, "
        + $"{FramesReceived}={recorder.Total(FramesReceived)}";

    /// <summary>An audio frame whose first four bytes carry its index, so the frames the consumer reads can be named.</summary>
    private static byte[] NumberedFrame(int index)
    {
        var payload = new byte[AudioFrameBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        return payload;
    }

    /// <summary>Whether the meter publishes an instrument named <paramref name="instrumentName"/>.</summary>
    private static bool IsPublished(string instrumentName)
    {
        var found = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
            {
                Volatile.Write(ref found, true);
            }
        };
        listener.Start();
        return Volatile.Read(ref found);
    }

    /// <summary>
    /// A started session over a real loopback socket, upgraded by the server's own upgrade code, whose far end is a
    /// <see cref="RawWebSocketPeer"/> that answers the session's close.
    /// </summary>
    private sealed class DropConnection : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _accepted;
        private readonly TaskCompletionSource _marker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _markerSubscription;

        private DropConnection(TcpListener listener, TcpClient accepted, RawWebSocketPeer peer, WebSocketAudioSession session)
        {
            _listener = listener;
            _accepted = accepted;
            Peer = peer;
            Session = session;
            _markerSubscription = session.ControlMessages.Subscribe(message =>
            {
                if (message is ChanWebSocketDtmf)
                    _marker.TrySetResult();
            });
        }

        public RawWebSocketPeer Peer { get; }

        public WebSocketAudioSession Session { get; }

        /// <summary>Sends the captured DTMF and waits until the pump has handled it, and so every frame sent before it.</summary>
        public async Task SendMarkerAndWaitAsync(CancellationToken token)
        {
            await Peer.SendTextAsync(CapturedDtmf.Text);
            await _marker.Task.WaitAsync(token);
        }

        public static async Task<DropConnection> OpenAsync(CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync(token);
            var peer = await RawWebSocketPeer.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, token);
            var accepted = await accept;
            var stream = accepted.GetStream();
            var id = $"drop-{Guid.NewGuid():N}";
            var upgrade = peer.UpgradeAsync(id, token);
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, token);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, token);
            await upgrade;
            var session = new WebSocketAudioSession(
                WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true }),
                id,
                "slin16",
                new CapturingLogger<WebSocketAudioSession>());
            var connection = new DropConnection(listener, accepted, peer, session);
            peer.AnswerCloseWhenReceived();
            session.Start();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            _markerSubscription.Dispose();
            await Session.DisposeAsync();
            _accepted.Dispose();
            Peer.Dispose();
            _listener.Dispose();
        }
    }
}
