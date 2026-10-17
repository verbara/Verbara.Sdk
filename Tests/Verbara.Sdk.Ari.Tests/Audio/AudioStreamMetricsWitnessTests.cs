using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// Each instrument of the <c>Verbara.Sdk.Ari.Audio</c> meter, witnessed against a real session: a call
/// through the public server, ended the way the far end ends it, and the exact measurements that call
/// emitted, each carrying the transport it came through.
/// </summary>
/// <remarks>
/// <para>
/// The instruments are process-wide, so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone, and
/// every recorder listens to the instruments it names, matched by meter and instrument name. No wait is a
/// delay: each assertion follows the release of the server's handler, its last step for the connection.
/// </para>
/// <para>
/// AudioSocket calls are fed the bytes captured from a real Asterisk where a capture exists (the
/// identification and audio frames). Hangup, error and empty audio frames have no capture and are written
/// by hand in <see cref="AudioSocketCall"/>, never by the SDK's encoder.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioStreamMetricsWitnessTests
{
    // The published names: the contract an exporter sees. Literals on purpose, so a rename is a red here.
    private const string TransportTag = "audio.transport";
    private const string AudioSocket = "audiosocket";
    private const string WebSocket = "websocket";

    private const string StreamsOpened = "audio.streams.opened";
    private const string StreamsClosed = "audio.streams.closed";
    private const string FramesReceived = "audio.frames.received";
    private const string BytesReceived = "audio.bytes.received";
    private const string FramesSent = "audio.frames.sent";
    private const string BytesSent = "audio.bytes.sent";
    private const string HangupFrames = "audio.hangup.frames";
    private const string ErrorFrames = "audio.error.frames";
    private const string FrameLatency = "audio.frame.latency";
    private const string TransportFailures = "audio.transport.failures";
    private const string ConnectionsRefused = "audio.connections.refused";

    /// <summary>The captured audio frame's payload: 320 bytes, 20 ms of 8 kHz signed-linear PCM.</summary>
    private const int CapturedPayloadLength = 320;

    /// <summary>The transport under test, for the witnesses both servers share.</summary>
    public enum Transport
    {
        /// <summary><see cref="AudioSocketServer"/>.</summary>
        AudioSocket,

        /// <summary><see cref="WebSocketAudioServer"/>.</summary>
        WebSocket,
    }

    // ================================================================ AudioSocket — lifecycle

    [Fact]
    public async Task AudioSocketStreams_ShouldCountOneOpenedAndOneClosedTaggedAudiosocket_WhenAnIdentifiedSessionEndsOnAHangupFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed);
        await using var call = await AudioSocketCall.StartAsync();
        await call.IdentifyAsync();

        // Act
        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, StreamsOpened, AudioSocket, "one session was identified");
            AssertCountedOnce(recorder, StreamsClosed, AudioSocket, "that one session ended once, on its hangup frame");
        }
    }

    [Fact]
    public async Task AudioSocketStreams_ShouldCountOneClosed_WhenTheServerAndTheConsumerDisposeTheSessionConcurrently()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed);
        await using var call = await AudioSocketCall.StartAsync();
        var stream = await call.IdentifyAsync();

        // Act — both disposals released by one gate: the consumer's, and the server's stop walking its sessions
        await DisposeConcurrentlyAsync(stream, call);
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, StreamsOpened, AudioSocket, "one session was identified");
            AssertCountedOnce(recorder, StreamsClosed, AudioSocket, "two disposals of one session close one stream");
        }
    }

    /// <summary>
    /// A <b>zero</b> assertion: green on the unwired tree, where nothing is recorded at all. It is meaningful
    /// only alongside the two lifecycle witnesses above, which show the same instruments counting a session.
    /// </summary>
    [Fact]
    public async Task AudioSocketStreams_ShouldCountNothing_WhenTheConnectionEndsBeforeItIdentifies()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed);
        await using var call = await AudioSocketCall.StartAsync(idleTimeout: TimeSpan.FromMinutes(5));

        // Act — the connection closes without ever sending its identification frame
        call.ClosePeerWithFin();
        await call.WaitUntilReleasedAsync();

        // Assert
        call.WasAnnounced.Should().BeFalse("the connection never identified itself, so it never became a stream");
        recorder.All.Should().BeEmpty(
            "a connection that never identified is no stream: it opens none and closes none, but recorded {0}",
            recorder.Describe());
    }

    // ================================================================ AudioSocket — data

    [Fact]
    public async Task AudioSocketFramesReceived_ShouldCountOnlyTheNonEmptyAudioFrames_WhenAnEmptyAudioFrameArrivesBetweenThem()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesReceived);

        // Act
        await RunAudioSocketCallWithAnEmptyFrameBetweenTwoAudioFramesAsync();

        // Assert
        AssertTotal(recorder, FramesReceived, 2, AudioSocket,
            "two audio frames carried audio; the empty one between them carried none and is not counted");
    }

    [Fact]
    public async Task AudioSocketBytesReceived_ShouldCountTheAudioPayloadBytes_WhenAnEmptyAudioFrameArrivesBetweenTwoAudioFrames()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesReceived);

        // Act
        await RunAudioSocketCallWithAnEmptyFrameBetweenTwoAudioFramesAsync();

        // Assert
        AssertTotal(recorder, BytesReceived, 2 * CapturedPayloadLength, AudioSocket,
            "the payload bytes of the two audio frames, headers excluded");
    }

    [Fact]
    public async Task AudioSocketFramesReceived_ShouldCountAudio_WhenItArrivesBeforeTheIdentificationFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesReceived, BytesReceived);
        await using var call = await AudioSocketCall.StartAsync();

        // Act — one captured audio frame, then the identification, then the hangup
        await call.SendAsync(AudioSocketCall.CapturedAudioFrame());
        await call.IdentifyAsync();
        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertTotal(recorder, FramesReceived, 1, AudioSocket, "audio received from Asterisk before the Uuid frame was still received");
            AssertTotal(recorder, BytesReceived, CapturedPayloadLength, AudioSocket, "and its payload bytes");
        }
    }

    [Fact]
    public async Task AudioSocketFramesSent_ShouldCountEachFrameTheConsumerWrites()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesSent);

        // Act
        await RunAudioSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        AssertTotal(recorder, FramesSent, 2, AudioSocket, "the consumer wrote two frames and both reached the wire");
    }

    [Fact]
    public async Task AudioSocketBytesSent_ShouldCountTheAudioBytesTheConsumerWrites_WithoutTheFrameHeaders()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesSent);

        // Act
        await RunAudioSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        AssertTotal(recorder, BytesSent, 2 * CapturedPayloadLength, AudioSocket,
            "two writes of 320 audio bytes; the three-byte AudioSocket header is not audio");
    }

    // ================================================================ AudioSocket — protocol frames

    [Fact]
    public async Task AudioSocketHangupFrames_ShouldCountOneTaggedAudiosocket_WhenTheFarEndSendsAHangupFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(HangupFrames, ErrorFrames);
        await using var call = await AudioSocketCall.StartAsync();
        await call.IdentifyAsync();

        // Act
        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, HangupFrames, AudioSocket, "one hangup frame arrived");
            recorder.Of(ErrorFrames).Should().BeEmpty("no error frame arrived");
        }
    }

    [Fact]
    public async Task AudioSocketErrorFrames_ShouldCountOneTaggedAudiosocket_WhenTheFarEndSendsAnErrorFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(HangupFrames, ErrorFrames);
        await using var call = await AudioSocketCall.StartAsync();
        await call.IdentifyAsync();

        // Act
        await call.SendAsync(AudioSocketCall.ErrorFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, ErrorFrames, AudioSocket, "one error frame arrived");
            recorder.Of(HangupFrames).Should().BeEmpty("no hangup frame arrived");
        }
    }

    // ================================================================ AudioSocket — latency

    [Fact]
    public async Task AudioSocketFrameLatency_ShouldRecordOneNonNegativeValuePerFrameTheConsumerReads()
    {
        // Arrange — three frames arrive; the consumer reads two of them
        using var recorder = new AudioMeasurementRecorder(FrameLatency);
        await using var call = await AudioSocketCall.StartAsync();
        var stream = await call.IdentifyAsync();
        for (var i = 0; i < 3; i++)
            await call.SendAsync(AudioSocketCall.CapturedAudioFrame());

        // Act
        for (var i = 0; i < 2; i++)
        {
            var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(ServedAudioCall.SignalTimeout);
            frame.Length.Should().Be(CapturedPayloadLength, "each read returns one captured frame's audio");
        }

        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        AssertLatency(recorder, expectedValues: 2, AudioSocket);
    }

    // ================================================================ AudioSocket — internal instruments

    [Fact]
    public async Task AudioSocketTransportFailures_ShouldCarryTheAudiosocketTag_WhenTheTransportResetsAnIdentifiedSession()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await AudioSocketCall.StartAsync();
        await call.IdentifyAsync();

        // Act
        call.ResetPeer();
        await call.WaitUntilReleasedAsync();

        // Assert
        AssertCountedOnce(recorder, TransportFailures, AudioSocket, "one live session lost its transport");
    }

    // ================================================================ WebSocket — lifecycle

    [Fact]
    public async Task WebSocketStreams_ShouldCountOneOpenedAndOneClosedTaggedWebsocket_WhenTheFarEndSendsACloseFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed);
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();

        // Act
        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, StreamsOpened, WebSocket, "one session was upgraded and started");
            AssertCountedOnce(recorder, StreamsClosed, WebSocket, "that one session ended once, on the close frame");
        }
    }

    [Fact]
    public async Task WebSocketStreams_ShouldCountOneClosed_WhenTheServerAndTheConsumerDisposeTheSessionConcurrently()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed);
        await using var call = await WebSocketCall.StartAsync();
        var stream = await call.UpgradeAsync();

        // Act
        await DisposeConcurrentlyAsync(stream, call);
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            AssertCountedOnce(recorder, StreamsOpened, WebSocket, "one session was started");
            AssertCountedOnce(recorder, StreamsClosed, WebSocket, "two disposals of one session close one stream");
        }
    }

    // ================================================================ WebSocket — data

    [Fact]
    public async Task WebSocketFramesReceived_ShouldCountOnlyTheNonEmptyBinaryMessages_WhenAnEmptyOneArrivesBetweenThem()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesReceived);

        // Act
        await RunWebSocketCallWithAnEmptyMessageBetweenTwoAudioMessagesAsync();

        // Assert
        AssertTotal(recorder, FramesReceived, 2, WebSocket, "two binary messages carried audio; the empty one is not counted");
    }

    [Fact]
    public async Task WebSocketBytesReceived_ShouldCountTheAudioPayloadBytes_WhenAnEmptyMessageArrivesBetweenTwoAudioMessages()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesReceived);

        // Act
        await RunWebSocketCallWithAnEmptyMessageBetweenTwoAudioMessagesAsync();

        // Assert
        AssertTotal(recorder, BytesReceived, 2 * CapturedPayloadLength, WebSocket, "the payload bytes of the two audio messages");
    }

    [Fact]
    public async Task WebSocketFramesSent_ShouldCountEachFrameTheConsumerWrites()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesSent);

        // Act
        await RunWebSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        AssertTotal(recorder, FramesSent, 2, WebSocket, "the consumer wrote two frames and both reached the client");
    }

    [Fact]
    public async Task WebSocketBytesSent_ShouldCountTheAudioBytesTheConsumerWrites()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesSent);

        // Act
        await RunWebSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        AssertTotal(recorder, BytesSent, 2 * CapturedPayloadLength, WebSocket, "two writes of 320 audio bytes");
    }

    // ================================================================ WebSocket — latency

    [Fact]
    public async Task WebSocketFrameLatency_ShouldRecordOneNonNegativeValuePerFrameTheConsumerReads()
    {
        // Arrange — three messages arrive; the consumer reads two of them
        using var recorder = new AudioMeasurementRecorder(FrameLatency);
        await using var call = await WebSocketCall.StartAsync();
        var stream = await call.UpgradeAsync();
        for (var i = 0; i < 3; i++)
            await call.SendBinaryAsync(CapturedPayload());

        // Act
        for (var i = 0; i < 2; i++)
        {
            var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(ServedAudioCall.SignalTimeout);
            frame.Length.Should().Be(CapturedPayloadLength, "each read returns one message's audio");
        }

        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();

        // Assert
        AssertLatency(recorder, expectedValues: 2, WebSocket);
    }

    // ================================================================ WebSocket — transport failure

    /// <summary>
    /// Measured on this tree (a first-chance probe over ten runs of each ending): an RST makes the server's
    /// receive throw <c>WebSocketException(ConnectionClosedPrematurely)</c> over
    /// <c>IOException(SocketException(ConnectionReset))</c>, the same reset AudioSocket counts.
    /// </summary>
    [Fact]
    public async Task WebSocketTransportFailures_ShouldCountOneTaggedWebsocket_WhenTheTransportResetsALiveSession()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();

        // Act
        call.ResetPeer();
        await call.WaitUntilReleasedAsync();

        // Assert
        AssertCountedOnce(recorder, TransportFailures, WebSocket, "a reset under a live session is the transport failing");
    }

    /// <summary>
    /// Measured on this tree: a FIN with no close frame makes the server's receive throw
    /// <c>WebSocketException(ConnectionClosedPrematurely)</c> with no inner exception. On a WebSocket the
    /// orderly ending is the close frame, so a connection that ends without one ended abnormally.
    /// </summary>
    [Fact]
    public async Task WebSocketTransportFailures_ShouldCountOneTaggedWebsocket_WhenTheConnectionEndsWithoutACloseFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();

        // Act
        call.ClosePeerWithFin();
        await call.WaitUntilReleasedAsync();

        // Assert
        AssertCountedOnce(recorder, TransportFailures, WebSocket, "a WebSocket that ends without its close frame ended abnormally");
    }

    /// <summary>
    /// A <b>zero</b> assertion: green on the unwired tree, where the WebSocket session counts no failure at
    /// all. It is meaningful only alongside the two abnormal-ending witnesses above. Measured on this tree:
    /// a close frame raises no exception at the server.
    /// </summary>
    [Fact]
    public async Task WebSocketTransportFailures_ShouldCountNothing_WhenTheFarEndSendsACloseFrame()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();

        // Act
        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();

        // Assert
        recorder.Of(TransportFailures).Should().BeEmpty("a close frame is the orderly ending, not a transport failure");
    }

    // ================================================================ both — refusal

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task ConnectionsRefused_ShouldCarryTheTransportTag_WhenTheServerRefusesAConnectionAtTheLimit(Transport transport)
    {
        // Arrange — a running server with one place; the first connection takes it at the accept
        using var recorder = new AudioMeasurementRecorder(ConnectionsRefused);
        await using var server = await RefusingServer.StartAsync(transport);
        using var holder = await ConnectAsync(server.Port);

        // Act — the second connection is refused and closed
        using var refused = await ConnectAsync(server.Port);
        await ReadToEndAsync(refused);
        await server.WhenRefusalReported.WaitAsync(ServedAudioCall.SignalTimeout);

        // Assert
        AssertCountedOnce(recorder, ConnectionsRefused, transport == Transport.AudioSocket ? AudioSocket : WebSocket,
            "one connection was refused at the limit");
    }

    // ================================================================ scenarios

    private static async Task RunAudioSocketCallWithAnEmptyFrameBetweenTwoAudioFramesAsync()
    {
        await using var call = await AudioSocketCall.StartAsync();
        await call.IdentifyAsync();
        await call.SendAsync(AudioSocketCall.CapturedAudioFrame());
        await call.SendAsync(AudioSocketCall.EmptyAudioFrame());
        await call.SendAsync(AudioSocketCall.CapturedAudioFrame());
        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();
    }

    private static async Task RunAudioSocketCallWhereTheConsumerWritesTwoFramesAsync()
    {
        await using var call = await AudioSocketCall.StartAsync();
        var stream = await call.IdentifyAsync();
        await stream.WriteFrameAsync(CapturedPayload());
        await stream.WriteFrameAsync(CapturedPayload());

        // Both frames reached the far end, header and payload, before the call ends.
        var received = await call.ReceiveAsync(2 * (3 + CapturedPayloadLength));
        received.Length.Should().Be(2 * (3 + CapturedPayloadLength), "both written frames reached the far end");

        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();
    }

    private static async Task RunWebSocketCallWithAnEmptyMessageBetweenTwoAudioMessagesAsync()
    {
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();
        await call.SendBinaryAsync(CapturedPayload());
        await call.SendBinaryAsync([]);
        await call.SendBinaryAsync(CapturedPayload());
        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();
    }

    private static async Task RunWebSocketCallWhereTheConsumerWritesTwoFramesAsync()
    {
        await using var call = await WebSocketCall.StartAsync();
        var stream = await call.UpgradeAsync();
        await stream.WriteFrameAsync(CapturedPayload());
        await stream.WriteFrameAsync(CapturedPayload());

        (await call.ReceiveBinaryAsync()).Length.Should().Be(CapturedPayloadLength, "the first frame reached the client");
        (await call.ReceiveBinaryAsync()).Length.Should().Be(CapturedPayloadLength, "the second frame reached the client");

        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();
    }

    /// <summary>
    /// Disposes <paramref name="stream"/> from the consumer's side and stops the server, which disposes the
    /// same session, both released by one gate so they run at the same moment.
    /// </summary>
    private static async Task DisposeConcurrentlyAsync(IAudioStream stream, ServedAudioCall call)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var byConsumer = Task.Run(async () =>
        {
            await gate.Task;
            await stream.DisposeAsync();
        });
        var byServer = Task.Run(async () =>
        {
            await gate.Task;
            await call.StopServerAsync();
        });

        gate.SetResult();
        await Task.WhenAll(byConsumer, byServer).WaitAsync(ServedAudioCall.SignalTimeout);
    }

    /// <summary>The captured audio frame's payload, its header removed.</summary>
    private static byte[] CapturedPayload() => AudioSocketCall.CapturedAudioFrame()[3..];

    // ================================================================ assertions

    /// <summary>
    /// Exactly one measurement of <paramref name="instrument"/>, of value 1, carrying exactly the
    /// <c>audio.transport</c> tag with <paramref name="transport"/>.
    /// </summary>
    private static void AssertCountedOnce(AudioMeasurementRecorder recorder, string instrument, string transport, string because)
    {
        var measurements = recorder.Of(instrument);
        measurements.Should().ContainSingle(
            "{0} — expected one measurement of {1}, but the meter recorded {2}", because, instrument, recorder.Describe());
        if (measurements.Count == 1)
        {
            measurements[0].Value.Should().Be(1, because);
            AssertTagged(measurements[0], transport);
        }
    }

    /// <summary>
    /// The values of <paramref name="instrument"/> sum to <paramref name="expected"/>, and every one of its
    /// measurements carries the <c>audio.transport</c> tag with <paramref name="transport"/>.
    /// </summary>
    private static void AssertTotal(AudioMeasurementRecorder recorder, string instrument, double expected, string transport, string because)
    {
        using var scope = new AssertionScope();
        var measurements = recorder.Of(instrument);
        recorder.Total(instrument).Should().Be(
            expected, "{0} — the meter recorded {1}", because, recorder.Describe());
        foreach (var measurement in measurements)
            AssertTagged(measurement, transport);
    }

    private static void AssertLatency(AudioMeasurementRecorder recorder, int expectedValues, string transport)
    {
        using var scope = new AssertionScope();
        var values = recorder.Of(FrameLatency);
        values.Should().HaveCount(
            expectedValues,
            "one latency value is recorded per frame the consumer read, not per frame received; the meter recorded {0}",
            recorder.Describe());
        foreach (var value in values)
        {
            value.Value.Should().BeGreaterThanOrEqualTo(0, "a frame is read after it was received");
            AssertTagged(value, transport);
        }
    }

    private static void AssertTagged(AudioMeasurement measurement, string transport)
    {
        measurement.Tags.Select(tag => tag.Key).Should().Equal(
            [TransportTag],
            "{0} carries exactly one tag, the transport", measurement.Instrument);
        measurement.Tag(TransportTag).Should().Be(transport, "{0} came through {1}", measurement.Instrument, transport);
    }

    // ================================================================ refusal helpers

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ServedAudioCall.SignalTimeout);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Reads until the server closes the connection: the end of the stream, or a reset.</summary>
    private static async Task ReadToEndAsync(TcpClient client)
    {
        var buffer = new byte[64];
        try
        {
            var stream = client.GetStream();
            while (await stream.ReadAsync(buffer).AsTask().WaitAsync(ServedAudioCall.SignalTimeout) > 0)
            {
                // Nothing is expected; a refused connection is closed without a byte.
            }
        }
        catch (IOException)
        {
            // A reset is the server closing it too.
            return;
        }
    }

    /// <summary>A running server of either transport with one place, on a loopback port the test dials.</summary>
    private sealed class RefusingServer : IAsyncDisposable
    {
        private readonly AudioSocketServer? _audioSocket;
        private readonly WebSocketAudioServer? _webSocket;

        private RefusingServer(int port, AudioSocketServer? audioSocket, WebSocketAudioServer? webSocket, Task refusal)
        {
            Port = port;
            _audioSocket = audioSocket;
            _webSocket = webSocket;
            WhenRefusalReported = refusal;
        }

        public int Port { get; }

        /// <summary>Completes on the server's refusal Warning, which it writes after the count.</summary>
        public Task WhenRefusalReported { get; }

        public static async Task<RefusingServer> StartAsync(Transport transport)
        {
            // The bind helper builds a fresh server per attempt, so the logger kept is the last one built:
            // the started server's.
            if (transport == Transport.AudioSocket)
            {
                CapturingLogger<AudioSocketServer>? logger = null;
                var (server, port) = await LoopbackServerBind.StartAsync(
                    p => new AudioSocketServer(Options(p, 0), logger = new CapturingLogger<AudioSocketServer>()),
                    s => s.StartAsync());
                return new RefusingServer(port, server, null, logger!.WhenLogged(e => e.EventName == "StreamLimitReached"));
            }

            CapturingLogger<WebSocketAudioServer>? wsLogger = null;
            var (ws, wsPort) = await LoopbackServerBind.StartAsync(
                p => new WebSocketAudioServer(Options(0, p), wsLogger = new CapturingLogger<WebSocketAudioServer>()),
                s => s.StartAsync());
            return new RefusingServer(wsPort, null, ws, wsLogger!.WhenLogged(e => e.EventName == "StreamLimitReached"));
        }

        public ValueTask DisposeAsync() => _audioSocket is not null ? _audioSocket.DisposeAsync() : _webSocket!.DisposeAsync();

        private static AudioServerOptions Options(int audioSocketPort, int webSocketPort) => new()
        {
            ListenAddress = "127.0.0.1",
            AudioSocketPort = audioSocketPort,
            WebSocketPort = webSocketPort,
            MaxConcurrentStreams = 1,
            IdleTimeout = TimeSpan.FromSeconds(30),
        };
    }
}
