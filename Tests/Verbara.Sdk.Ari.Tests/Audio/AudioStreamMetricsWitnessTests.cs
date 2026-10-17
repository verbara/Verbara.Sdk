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
            MeterAssert.CountedOnce(recorder, StreamsOpened, AudioSocket, "one session was identified");
            MeterAssert.CountedOnce(recorder, StreamsClosed, AudioSocket, "that one session ended once, on its hangup frame");
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
            MeterAssert.CountedOnce(recorder, StreamsOpened, AudioSocket, "one session was identified");
            MeterAssert.CountedOnce(recorder, StreamsClosed, AudioSocket, "two disposals of one session close one stream");
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

    [Fact]
    public async Task AudioSocketStreams_ShouldCountOneOpenedAndOneClosedWithoutAnnouncing_WhenTheStopBeginsBeforeRegistration()
    {
        // Arrange — the server's stop begins inside the window between the session opening (identified) and
        // its registration, which the server reads as a refusal
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed, ConnectionsRefused);
        await using var call = await AudioSocketCall.StartAsync();
        var stopBegun = StartStopInTheRegistrationWindow(call.AudioSocketServer.StopAsync, hook => call.AudioSocketServer.BeforeRegistration = hook);

        // Act
        await call.SendAsync(Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture.IdentificationFrame());
        var stop = await stopBegun.WaitAsync(ServedAudioCall.SignalTimeout);
        await stop.WaitAsync(ServedAudioCall.SignalTimeout);
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            call.WasAnnounced.Should().BeFalse("the stop began before the server announced the stream");
            MeterAssert.CountedOnce(recorder, StreamsOpened, AudioSocket, "the session opened when it identified itself");
            MeterAssert.CountedOnce(recorder, StreamsClosed, AudioSocket, "the opened session was closed by the stopping server");
            recorder.Of(ConnectionsRefused).Should().BeEmpty("the server was stopping, not full");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AudioSocketProtocolFrames_ShouldCountTheFrameButNoStream_WhenItArrivesBeforeTheIdentificationFrame(bool hangup)
    {
        // Arrange
        var counted = hangup ? HangupFrames : ErrorFrames;
        var other = hangup ? ErrorFrames : HangupFrames;
        using var recorder = new AudioMeasurementRecorder(HangupFrames, ErrorFrames, StreamsOpened, StreamsClosed);
        await using var call = await AudioSocketCall.StartAsync();

        // Act — the frame arrives first, and ends the connection before any identification
        await call.SendAsync(hangup ? AudioSocketCall.HangupFrame() : AudioSocketCall.ErrorFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            call.WasAnnounced.Should().BeFalse("the connection never identified itself");
            MeterAssert.CountedOnce(recorder, counted, AudioSocket, "the frame was received from Asterisk, identified or not");
            recorder.Of(other).Should().BeEmpty("only one protocol frame arrived");
            recorder.Of(StreamsOpened).Should().BeEmpty("a connection that never identified never opened");
            recorder.Of(StreamsClosed).Should().BeEmpty("a stream that never opened is never closed");
        }
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
        MeterAssert.Total(recorder, FramesReceived, 2, AudioSocket,
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
        MeterAssert.Total(recorder, BytesReceived, 2 * CapturedPayloadLength, AudioSocket,
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
            MeterAssert.Total(recorder, FramesReceived, 1, AudioSocket, "audio received from Asterisk before the Uuid frame was still received");
            MeterAssert.Total(recorder, BytesReceived, CapturedPayloadLength, AudioSocket, "and its payload bytes");
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
        MeterAssert.Total(recorder, FramesSent, 2, AudioSocket, "the consumer wrote two frames and both reached the wire");
    }

    [Fact]
    public async Task AudioSocketBytesSent_ShouldCountTheAudioBytesTheConsumerWrites_WithoutTheFrameHeaders()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesSent);

        // Act
        await RunAudioSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        MeterAssert.Total(recorder, BytesSent, 2 * CapturedPayloadLength, AudioSocket,
            "two writes of 320 audio bytes; the three-byte AudioSocket header is not audio");
    }

    [Fact]
    public async Task AudioSocketFramesSent_ShouldCountNeitherFrameNorBytes_WhenTheConsumerWritesAnEmptyFrameBetweenTwoAudioFrames()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesSent, BytesSent);
        await using var call = await AudioSocketCall.StartAsync();
        var stream = await call.IdentifyAsync();

        // Act
        await stream.WriteFrameAsync(CapturedPayload());
        await stream.WriteFrameAsync(ReadOnlyMemory<byte>.Empty);
        await stream.WriteFrameAsync(CapturedPayload());
        var received = await call.ReceiveAsync(2 * (3 + CapturedPayloadLength) + 3);
        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            received.Length.Should().Be(2 * (3 + CapturedPayloadLength) + 3,
                "all three writes reached the far end, the empty one as a header-only frame");
            MeterAssert.Total(recorder, FramesSent, 2, AudioSocket, "the empty write carried no audio and is not a frame sent");
            MeterAssert.Total(recorder, BytesSent, 2 * CapturedPayloadLength, AudioSocket, "only the two audio payloads are bytes sent");
        }
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
            MeterAssert.CountedOnce(recorder, HangupFrames, AudioSocket, "one hangup frame arrived");
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
            MeterAssert.CountedOnce(recorder, ErrorFrames, AudioSocket, "one error frame arrived");
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
        MeterAssert.Latency(recorder, expectedValues: 2, AudioSocket);
    }

    [Fact]
    public async Task AudioSocketFrameLatency_ShouldRecordNoValueForTheEmptyFrame_WhenTheConsumerReadsPastIt()
    {
        // Arrange — an audio frame, an empty one, an audio frame
        using var recorder = new AudioMeasurementRecorder(FrameLatency);
        await using var call = await AudioSocketCall.StartAsync();
        var stream = await call.IdentifyAsync();
        await call.SendAsync(AudioSocketCall.CapturedAudioFrame());
        await call.SendAsync(AudioSocketCall.EmptyAudioFrame());
        await call.SendAsync(AudioSocketCall.CapturedAudioFrame());

        // Act — the consumer reads all three
        var lengths = new List<int>();
        for (var i = 0; i < 3; i++)
            lengths.Add((await stream.ReadFrameAsync().AsTask().WaitAsync(ServedAudioCall.SignalTimeout)).Length);

        await call.SendAsync(AudioSocketCall.HangupFrame());
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            lengths.Should().Equal([CapturedPayloadLength, 0, CapturedPayloadLength], "the empty frame is still queued and read");
            MeterAssert.Latency(recorder, expectedValues: 2, AudioSocket);
        }
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
        MeterAssert.CountedOnce(recorder, TransportFailures, AudioSocket, "one live session lost its transport");
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
            MeterAssert.CountedOnce(recorder, StreamsOpened, WebSocket, "one session was upgraded and started");
            MeterAssert.CountedOnce(recorder, StreamsClosed, WebSocket, "that one session ended once, on the close frame");
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
            MeterAssert.CountedOnce(recorder, StreamsOpened, WebSocket, "one session was started");
            MeterAssert.CountedOnce(recorder, StreamsClosed, WebSocket, "two disposals of one session close one stream");
        }
    }

    [Fact]
    public async Task WebSocketStreams_ShouldCountOneOpenedAndOneClosedWithoutAnnouncing_WhenTheStopBeginsBeforeRegistration()
    {
        // Arrange — the server's stop begins inside the window between the session starting (past its
        // upgrade) and its registration, which the server reads as a refusal
        using var recorder = new AudioMeasurementRecorder(StreamsOpened, StreamsClosed, ConnectionsRefused);
        await using var call = await WebSocketCall.StartAsync();
        var stopBegun = StartStopInTheRegistrationWindow(call.WebSocketServer.StopAsync, hook => call.WebSocketServer.BeforeRegistration = hook);

        // Act
        await call.SendUpgradeAsync();
        var stop = await stopBegun.WaitAsync(ServedAudioCall.SignalTimeout);
        await stop.WaitAsync(ServedAudioCall.SignalTimeout);
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            call.WasAnnounced.Should().BeFalse("the stop began before the server announced the stream");
            MeterAssert.CountedOnce(recorder, StreamsOpened, WebSocket, "the session started right after the upgrade");
            MeterAssert.CountedOnce(recorder, StreamsClosed, WebSocket, "the started session was closed by the stopping server");
            recorder.Of(ConnectionsRefused).Should().BeEmpty("the server was stopping, not full");
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
        MeterAssert.Total(recorder, FramesReceived, 2, WebSocket, "two binary messages carried audio; the empty one is not counted");
    }

    [Fact]
    public async Task WebSocketBytesReceived_ShouldCountTheAudioPayloadBytes_WhenAnEmptyMessageArrivesBetweenTwoAudioMessages()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesReceived);

        // Act
        await RunWebSocketCallWithAnEmptyMessageBetweenTwoAudioMessagesAsync();

        // Assert
        MeterAssert.Total(recorder, BytesReceived, 2 * CapturedPayloadLength, WebSocket, "the payload bytes of the two audio messages");
    }

    [Fact]
    public async Task WebSocketFramesSent_ShouldCountEachFrameTheConsumerWrites()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesSent);

        // Act
        await RunWebSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        MeterAssert.Total(recorder, FramesSent, 2, WebSocket, "the consumer wrote two frames and both reached the client");
    }

    [Fact]
    public async Task WebSocketBytesSent_ShouldCountTheAudioBytesTheConsumerWrites()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(BytesSent);

        // Act
        await RunWebSocketCallWhereTheConsumerWritesTwoFramesAsync();

        // Assert
        MeterAssert.Total(recorder, BytesSent, 2 * CapturedPayloadLength, WebSocket, "two writes of 320 audio bytes");
    }

    [Fact]
    public async Task WebSocketFramesSent_ShouldCountNeitherFrameNorBytes_WhenTheConsumerWritesAnEmptyFrameBetweenTwoAudioFrames()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(FramesSent, BytesSent);
        await using var call = await WebSocketCall.StartAsync();
        var stream = await call.UpgradeAsync();

        // Act
        await stream.WriteFrameAsync(CapturedPayload());
        await stream.WriteFrameAsync(ReadOnlyMemory<byte>.Empty);
        await stream.WriteFrameAsync(CapturedPayload());
        var lengths = new List<int>();
        for (var i = 0; i < 3; i++)
            lengths.Add((await call.ReceiveBinaryAsync()).Length);
        await call.SendCloseFrameAsync();
        await call.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            lengths.Should().Equal([CapturedPayloadLength, 0, CapturedPayloadLength], "all three writes reached the client, the empty one as an empty message");
            MeterAssert.Total(recorder, FramesSent, 2, WebSocket, "the empty write carried no audio and is not a frame sent");
            MeterAssert.Total(recorder, BytesSent, 2 * CapturedPayloadLength, WebSocket, "only the two audio payloads are bytes sent");
        }
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
        MeterAssert.Latency(recorder, expectedValues: 2, WebSocket);
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
        MeterAssert.CountedOnce(recorder, TransportFailures, WebSocket, "a reset under a live session is the transport failing");
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
        MeterAssert.CountedOnce(recorder, TransportFailures, WebSocket, "a WebSocket that ends without its close frame ended abnormally");
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

    /// <summary>
    /// A <b>zero</b> assertion, meaningful only alongside the RST and FIN witnesses above, which show the same
    /// instrument counting: the owner's own teardown of a live session is not a transport failure.
    /// </summary>
    [Fact]
    public async Task WebSocketTransportFailures_ShouldCountNothing_WhenTheConsumerDisposesALiveSession()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await WebSocketCall.StartAsync();
        var stream = await call.UpgradeAsync();

        // Act
        await stream.DisposeAsync().AsTask().WaitAsync(ServedAudioCall.SignalTimeout);
        await call.WaitUntilReleasedAsync();

        // Assert
        recorder.Of(TransportFailures).Should().BeEmpty("the consumer ended the session; its transport did not fail");
    }

    /// <summary>
    /// A <b>zero</b> assertion, meaningful only alongside the RST and FIN witnesses above: a server stop that
    /// disposes a live session is not a transport failure.
    /// </summary>
    [Fact]
    public async Task WebSocketTransportFailures_ShouldCountNothing_WhenTheServerStopsWithALiveSession()
    {
        // Arrange
        using var recorder = new AudioMeasurementRecorder(TransportFailures);
        await using var call = await WebSocketCall.StartAsync();
        await call.UpgradeAsync();

        // Act
        await call.StopServerAsync().WaitAsync(ServedAudioCall.SignalTimeout);
        await call.WaitUntilReleasedAsync();

        // Assert
        recorder.Of(TransportFailures).Should().BeEmpty("the stopping server ended the session; its transport did not fail");
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
        MeterAssert.CountedOnce(recorder, ConnectionsRefused, transport == Transport.AudioSocket ? AudioSocket : WebSocket,
            "one connection was refused at the limit");
    }

    // ================================================================ the class doc's dotnet-counters sentence

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task DotnetCountersMonitorVerbaraSdkAriAudio_ShouldSeeARealSessionsMeasurements_WhenSubscribedByTheMeterNameAlone(Transport transport)
    {
        // Arrange — what `dotnet-counters monitor ... Verbara.Sdk.Ari.Audio` asks for: every instrument of the
        // meter with that name, none named one by one. The listener enables an instrument by its meter's name
        // only, so an instrument published under any other meter name is never seen.
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Instrument, object? Transport)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, "Verbara.Sdk.Ari.Audio", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            seen.Enqueue((instrument.Name, TagValue(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            seen.Enqueue((instrument.Name, TagValue(tags))));
        listener.Start();
        var expectedTransport = transport == Transport.AudioSocket ? AudioSocket : WebSocket;

        // Act — one real call, one audio frame received, ended by the far end
        if (transport == Transport.AudioSocket)
        {
            await using var call = await AudioSocketCall.StartAsync();
            await call.IdentifyAsync();
            await call.SendAsync(AudioSocketCall.CapturedAudioFrame());
            await call.SendAsync(AudioSocketCall.HangupFrame());
            await call.WaitUntilReleasedAsync();
        }
        else
        {
            await using var call = await WebSocketCall.StartAsync();
            await call.UpgradeAsync();
            await call.SendBinaryAsync(CapturedPayload());
            await call.SendCloseFrameAsync();
            await call.WaitUntilReleasedAsync();
        }

        // Assert — the session's measurements reached a subscriber that named only the meter
        var fromThisTransport = seen.Where(m => Equals(m.Transport, expectedTransport)).Select(m => m.Instrument).ToHashSet();
        fromThisTransport.Should().Contain(
            [StreamsOpened, FramesReceived, BytesReceived, StreamsClosed],
            "a subscriber to the meter Verbara.Sdk.Ari.Audio by name sees what a real {0} session records", expectedTransport);
    }

    private static object? TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, TransportTag, StringComparison.Ordinal))
                return tag.Value;
        }

        return null;
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

    /// <summary>
    /// Installs a one-shot registration hook that begins the server's stop on the connection's handler, inside
    /// the window between the session opening and its registration. Returns the stop as it was begun, without
    /// blocking the handler on it.
    /// </summary>
    private static Task<Task> StartStopInTheRegistrationWindow(
        Func<CancellationToken, ValueTask> stopAsync, Action<Action> setHook)
    {
        var stopBegun = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        setHook(() =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
                stopBegun.TrySetResult(stopAsync(CancellationToken.None).AsTask());
        });
        return stopBegun.Task;
    }

    // ================================================================ assertions

    /// <summary>
    /// The assertions every witness ends on: what the meter recorded for one instrument, and its transport tag.
    /// </summary>
    private static class MeterAssert
    {
        /// <summary>
        /// Exactly one measurement of <paramref name="instrument"/>, of value 1, carrying exactly the
        /// <c>audio.transport</c> tag with <paramref name="transport"/>.
        /// </summary>
        public static void CountedOnce(AudioMeasurementRecorder recorder, string instrument, string transport, string because)
        {
            var measurements = recorder.Of(instrument);
            measurements.Should().ContainSingle(
                "{0} — expected one measurement of {1}, but the meter recorded {2}", because, instrument, recorder.Describe());
            if (measurements.Count == 1)
            {
                measurements[0].Value.Should().Be(1, because);
                Tagged(measurements[0], transport);
            }
        }

        /// <summary>
        /// The values of <paramref name="instrument"/> sum to <paramref name="expected"/>, and every one of its
        /// measurements carries the <c>audio.transport</c> tag with <paramref name="transport"/>.
        /// </summary>
        public static void Total(AudioMeasurementRecorder recorder, string instrument, double expected, string transport, string because)
        {
            using var scope = new AssertionScope();
            var measurements = recorder.Of(instrument);
            recorder.Total(instrument).Should().Be(
                expected, "{0} — the meter recorded {1}", because, recorder.Describe());
            foreach (var measurement in measurements)
                Tagged(measurement, transport);
        }

        public static void Latency(AudioMeasurementRecorder recorder, int expectedValues, string transport)
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
                Tagged(value, transport);
            }
        }

        public static void Tagged(AudioMeasurement measurement, string transport)
        {
            measurement.Tags.Select(tag => tag.Key).Should().Equal(
                [TransportTag],
                "{0} carries exactly one tag, the transport", measurement.Instrument);
            measurement.Tag(TransportTag).Should().Be(transport, "{0} came through {1}", measurement.Instrument, transport);
        }
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
