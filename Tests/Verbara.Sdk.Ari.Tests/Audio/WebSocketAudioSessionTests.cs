using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.TestInfrastructure.Wire;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class WebSocketAudioSessionTests
{
    private static WebSocketAudioSession CreateSession(FakeWebSocket ws, string channelId = "ch-1", string format = "slin16")
        => new(ws, channelId, format, new CapturingLogger<WebSocketAudioSession>());

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
    public async Task ReadPump_ShouldTransitionToDisconnectedOnce_WhenCloseFrameReceived()
    {
        using var ws = new FakeWebSocket();
        ws.EnqueueClose();

        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();

        // Wait for the transition this test is named after instead of for 100 ms. That sleep was
        // load-bearing, not cosmetic: forced to zero the assertion fails, which is another way of
        // saying a loaded runner can outrun it. StateChanges was already subscribed here, so the
        // causal seam cost nothing to reach — and Fed.StartAsync
        // below waits on the read pump the same way.
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = sut.StateChanges.Subscribe(s =>
        {
            states.Add(s);
            if (s == AudioStreamState.Disconnected)
                disconnected.TrySetResult();
        });

        sut.Start();

        // Deliberately generous and never reached on the happy path — the wait returns the moment
        // the read pump publishes the state. Removing the close frame makes it throw here in 5 s
        // instead of asserting on an empty list, which is how this wait was checked.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.DisposeAsync();

        // Read after the disposal, which every owner performs: it must not publish the ending again
        states.Should().Equal(
            [AudioStreamState.Connected, AudioStreamState.Disconnected],
            "the close frame's ending is published once, last");
    }

    [Fact]
    public async Task ReadPump_ShouldTransitionToDisconnectedOnce_WhenCancelled()
    {
        using var ws = new FakeWebSocket();
        // No data enqueued — pump will block until cancelled

        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();
        using var sub = sut.StateChanges.Subscribe(s => states.Add(s));

        sut.Start();
        await sut.DisposeAsync();

        states.Should().Equal(
            [AudioStreamState.Connected, AudioStreamState.Disconnected],
            "a cancelled pump and the disposal that cancelled it publish one ending between them");
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
    public async Task DisposeAsync_ShouldTransitionToDisconnectedOnce()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws);
        var states = new List<AudioStreamState>();
        using var sub = sut.StateChanges.Subscribe(s => states.Add(s));

        await sut.DisposeAsync();

        states.Should().Equal(
            [AudioStreamState.Connected, AudioStreamState.Disconnected],
            "the disposal of a session whose pump never ran publishes the ending once");
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
    public async Task DisposeAsync_ShouldFinish_WhenPeerNeverAnswersCloseFrame()
    {
        // A real server-side socket whose peer never reads, and no Start(): nothing is receiving, so
        // cancelling the read pump cannot abort the socket, and CloseAsync is left waiting for a
        // close frame the peer never sends. Disposal has to give up on that handshake.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await accepting;
        using var serverSocket = WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true });
        var sut = new WebSocketAudioSession(serverSocket, "ch-silent-peer", "slin16", new CapturingLogger<WebSocketAudioSession>());
        var stateCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = sut.StateChanges.Subscribe(static _ => { }, () => stateCompleted.TrySetResult());

        // Bounded, so a close handshake that waits forever fails here instead of hanging the run.
        await sut.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        stateCompleted.Task.IsCompleted.Should().BeTrue("disposal completes the state stream once the close handshake is abandoned");
    }

    [Fact]
    public void IsConnected_ShouldReturnFalse_WhenWebSocketClosed()
    {
        using var ws = new FakeWebSocket();
        ws.Close();
        var sut = CreateSession(ws);

        sut.IsConnected.Should().BeFalse();
    }

    // -- Control messages, read as Asterisk sends them ---------------------------
    //
    // Every frame below that Asterisk would send is a captured frame from ChanWebSocketWireCapture; the few synthetic
    // frames are strings in the test, labelled, in the captured grammar they copy. None is built by this SDK's encoder.

    private static readonly byte[] AudioMarker = MarkerFrame();

    private static byte[] MarkerFrame()
    {
        var frame = new byte[640];
        frame[0] = 0x7E;
        return frame;
    }

    public static TheoryData<int> EveryCapturedControlMessage()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ChanWebSocketWireCapture.ControlMessages.Count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCapturedControlMessage))]
    public async Task ControlMessages_ShouldPublishTheMappedRecordWithTheCapturedValues_WhenAsteriskSendsEachCapturedMessage(int index)
    {
        // Arrange
        var captured = ChanWebSocketWireCapture.ControlMessages[index];

        // Act — the captured frame, then an audio frame
        await using var fed = await Fed.StartAsync("ch-1", "slin16", Text(captured.Payload()));

        // Assert
        using (new AssertionScope($"{captured.Event} {captured.Format} {captured.AsteriskVersion} ({captured.Source})"))
        {
            fed.MarkerDelivered.Should().BeTrue("the audio frame after the message is delivered");
            fed.Published.Should().ContainSingle("each captured message is published once");
            var message = fed.Published.Single();
            switch (captured.Event)
            {
                case "DTMF_END":
                    var dtmf = message.Should().BeOfType<ChanWebSocketDtmf>().Subject;
                    dtmf.Digit.Should().Be(captured.Value);
                    dtmf.ChannelId.Should().Be(captured.ChannelId);
#pragma warning disable VSDK0004 // the obsolete read is the point: DTMF_END carries no duration
                    dtmf.DurationMs.Should().Be(0);
#pragma warning restore VSDK0004
                    break;
                case "MEDIA_XOFF":
                    message.Should().BeOfType<ChanWebSocketMediaXoff>().Which.ChannelId.Should().Be(captured.ChannelId);
                    break;
                case "MEDIA_XON":
                    message.Should().BeOfType<ChanWebSocketMediaXon>().Which.ChannelId.Should().Be(captured.ChannelId);
                    break;
                case "MEDIA_MARK_PROCESSED":
                    var mark = message.Should().BeOfType<ChanWebSocketMediaMarkProcessed>().Subject;
                    mark.Mark.Should().Be(captured.Value);
                    mark.ChannelId.Should().Be(captured.ChannelId);
                    break;
                case "ERROR":
                    var error = message.Should().BeOfType<ChanWebSocketError>().Subject;
                    error.ChannelId.Should().Be(captured.ChannelId);
                    error.ErrorText.Should().Be(captured.Value);
                    break;
                default:
                    // STATUS, QUEUE_DRAINED, MEDIA_BUFFERING_COMPLETED: replies to commands this SDK has no method for
                    var generic = message.Should().BeOfType<ChanWebSocketUnknownMessage>().Subject;
                    generic.Event.Should().Be(captured.Event);
                    generic.RawText.Should().Be(captured.Text);
                    break;
            }
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task ControlMessages_ShouldCarryTheErrorTextVerbatimWithItsTrailingNewline_WhenAsteriskRefusesACommand(ChanWebSocketCaptureFormat format)
    {
        // Arrange — the captured ERROR whose error_text ends in a literal newline
        var captured = ChanWebSocketWireCapture.ControlMessages.First(
            m => m.Event == "ERROR" && m.Format == format && m.Value!.EndsWith('\n'));

        // Act
        await using var fed = await Fed.StartAsync("ch-1", "slin16", Text(captured.Payload()));

        // Assert
        fed.Published.Should().ContainSingle().Which.Should().BeOfType<ChanWebSocketError>()
            .Which.ErrorText.Should().Be(captured.Value).And.EndWith("\n", "the trailing newline is part of what Asterisk sent ({0})", captured.Source);
    }

    [Theory]
    // Synthetic, in the captured plain-text grammar (EVENT key:value …): an event no capture names.
    [InlineData("FUTURE_EVENT channel_id:synthetic-1 queue_length:0", "FUTURE_EVENT")]
    // Synthetic, in the captured JSON grammar (an object named by `event`): an event no capture names.
    [InlineData("""{"event":"FUTURE_EVENT","channel_id":"synthetic-1"}""", "FUTURE_EVENT")]
    // Synthetic, neither shape: a JSON object with no `event`, so it names no event.
    [InlineData("""{"channel_id":"synthetic-1"}""", "")]
    public async Task ControlMessages_ShouldPublishTheGenericRecordWithTheRawText_WhenTheModelDoesNotNameTheMessage(string synthetic, string expectedEvent)
    {
        // Act
        await using var fed = await Fed.StartAsync("ch-1", "slin16", Text(Encoding.UTF8.GetBytes(synthetic)));

        // Assert
        using (new AssertionScope())
        {
            fed.MarkerDelivered.Should().BeTrue("the audio frame after the message is delivered");
            var generic = fed.Published.Should().ContainSingle("a message the model does not name is not dropped")
                .Which.Should().BeOfType<ChanWebSocketUnknownMessage>().Subject;
            generic.Event.Should().Be(expectedEvent);
            generic.RawText.Should().Be(synthetic);
        }
    }

    [Fact]
    public async Task ControlMessages_ShouldPublishNothing_WhenTheConnectionEndsWithItsCloseFrame()
    {
        // Arrange — the captured MEDIA_START, then the close frame: a hangup is a close frame, never a text message
        var mediaStart = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText);
        using var ws = new FakeWebSocket();
        ws.EnqueueReceive(mediaStart.Payload(), WebSocketMessageType.Text);
        ws.EnqueueClose();
        var sut = CreateSession(ws);
        var published = 0;
        using var controls = sut.ControlMessages.Subscribe(_ => Interlocked.Increment(ref published));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var states = sut.StateChanges.Subscribe(s =>
        {
            if (s == AudioStreamState.Disconnected)
                ended.TrySetResult();
        });

        // Act
        sut.Start();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Volatile.Read(ref published).Should().Be(0, "the ending is published on StateChanges, never as a control message");
        await sut.DisposeAsync();
    }

    // -- MEDIA_START is stream state ---------------------------------------------

    public static TheoryData<int> EveryCapturedMediaStart()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ChanWebSocketWireCapture.MediaStart.Count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCapturedMediaStart))]
    public async Task MediaStart_ShouldCarryEveryCapturedField_WhenTheFirstFrameIsMediaStart(int index)
    {
        // Arrange
        var captured = ChanWebSocketWireCapture.MediaStart[index];
        var expected = CapturedFields.Read(captured);

        // Act — on a session whose configured format is of another rate
        await using var fed = await Fed.StartAsync("seg", "ulaw", Text(captured.Payload()));

        // Assert
        using (new AssertionScope(captured.Source))
        {
            fed.Published.Should().BeEmpty("MEDIA_START is stream state, never a control message");
            var mediaStart = fed.MediaStart;
            mediaStart.Should().NotBeNull();
            mediaStart!.Format.Should().Be(expected.Format);
            mediaStart.Rate.Should().Be(16000, "derived from slin16");
            mediaStart.Channels.Should().Be(1);
            mediaStart.ChannelId.Should().Be(captured.ChannelId);
            mediaStart.ConnectionId.Should().Be(expected.ConnectionId);
            mediaStart.Channel.Should().Be(expected.Channel);
            mediaStart.OptimalFrameSize.Should().Be(expected.OptimalFrameSize);
            mediaStart.Ptime.Should().Be(expected.Ptime);
            mediaStart.Passthrough.Should().Be(expected.Passthrough);
            mediaStart.Unbuffered.Should().Be(expected.Unbuffered);
            if (captured.Format == ChanWebSocketCaptureFormat.Json)
            {
                mediaStart.ChannelVariables.Should().NotBeNull().And.Equal(expected.ChannelVariables!);
                mediaStart.ChannelVariables!["MEDIA_WEBSOCKET_CONNECTION_ID"].Should().Be(expected.ConnectionId);
                mediaStart.ChannelVariables["MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE"].Should().Be("640");
                if (captured.Producer == ChanWebSocketCaptureProducer.Dial)
                    mediaStart.ChannelVariables.Should().ContainKey("DIALEDPEERNUMBER");
                if (captured.Source.Contains("dialjsoninh", StringComparison.Ordinal))
                    mediaStart.ChannelVariables["__H1CALLER"].Should().Be("raw-dji;2", "the caller leg's inherited variable is carried");
            }
            else
            {
                mediaStart.ChannelVariables.Should().BeNull("plain text carries no channel variables");
            }

            fed.Session.Format.Should().Be(mediaStart.Format, "the stream's format follows its first MEDIA_START");
            fed.Session.SampleRate.Should().Be(mediaStart.Rate, "one derivation gives both");
        }
    }

    [Theory]
    // The captured frame, extended in the test with one synthetic token no capture names
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    // The captured frame, extended in the test with one synthetic member no capture names
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task MediaStart_ShouldStillReadEveryCapturedField_WhenTheFrameCarriesATokenTheModelDoesNotName(ChanWebSocketCaptureFormat format)
    {
        // Arrange
        var captured = CapturedMediaStart("24.0.0-rc2", format);
        var extended = format == ChanWebSocketCaptureFormat.Json
            ? captured.Text.Insert(1, "\"future_member\":{\"nested\":[1,2]},")
            : captured.Text + " future_token:synthetic";
        extended.Should().NotBe(captured.Text);
        var expected = CapturedFields.Read(captured);

        // Act
        await using var fed = await Fed.StartAsync("seg", "slin16", Text(Encoding.UTF8.GetBytes(extended)));

        // Assert
        using (new AssertionScope())
        {
            var mediaStart = fed.MediaStart;
            mediaStart.Should().NotBeNull("the unknown token does not make the read fail");
            mediaStart!.ChannelId.Should().Be(captured.ChannelId);
            mediaStart.ConnectionId.Should().Be(expected.ConnectionId);
            mediaStart.Channel.Should().Be(expected.Channel);
            mediaStart.Format.Should().Be(expected.Format);
            mediaStart.OptimalFrameSize.Should().Be(expected.OptimalFrameSize);
            mediaStart.Ptime.Should().Be(expected.Ptime);
            mediaStart.Passthrough.Should().Be(expected.Passthrough);
            mediaStart.Unbuffered.Should().Be(expected.Unbuffered);
            fed.Published.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MediaStart_ShouldBeSetOnceByALateMediaStart_AndLeaveTheKeyAtThePathSegment()
    {
        // Arrange — an audio frame first, then the captured MEDIA_START, on a session configured for ulaw
        var captured = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText);

        // Act
        await using var fed = await Fed.StartAsync("seg-late", "ulaw", Binary(new byte[320]), Text(captured.Payload()));

        // Assert
        using (new AssertionScope())
        {
            fed.Audio.Should().ContainSingle("the binary first frame is delivered").Which.Length.Should().Be(320);
            fed.MarkerDelivered.Should().BeTrue();
            fed.MediaStart.Should().NotBeNull().And.Match<ChanWebSocketMediaStart>(m => m.ChannelId == captured.ChannelId);
            fed.Session.ChannelId.Should().Be("seg-late", "a MEDIA_START after the first frame re-keys nothing");
            fed.Session.Format.Should().Be("slin16");
            fed.Session.SampleRate.Should().Be(16000);
            fed.MediaStart!.Rate.Should().Be(fed.Session.SampleRate);
            fed.Published.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MediaStart_ShouldKeepTheFirstValues_WhenASecondMediaStartArrives()
    {
        // Arrange — two captured MEDIA_STARTs naming two calls. Every capture is slin16, so the second is made "of another
        // format" in the test: synthetic, the captured JSON MEDIA_START with "format":"slin16" replaced by "format":"ulaw"
        var first = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText);
        var second = ChanWebSocketWireCapture.MediaStart.First(f =>
            f.AsteriskVersion == "22.9.0" && f.Producer == ChanWebSocketCaptureProducer.ExternalMedia && f.Format == ChanWebSocketCaptureFormat.Json);
        var secondOfAnotherFormat = second.Text.Replace("\"format\":\"slin16\"", "\"format\":\"ulaw\"", StringComparison.Ordinal);
        secondOfAnotherFormat.Should().NotBe(second.Text, "the synthetic frame differs from the capture in its format only");

        // Act
        await using var fed = await Fed.StartAsync("seg", "ulaw", Text(first.Payload()), Text(Encoding.UTF8.GetBytes(secondOfAnotherFormat)));

        // Assert
        using (new AssertionScope())
        {
            fed.MediaStart!.ChannelId.Should().Be(first.ChannelId, "a second MEDIA_START changes nothing");
            fed.MediaStart.Channel.Should().Be(CapturedFields.Read(first).Channel);
            fed.MediaStart.Format.Should().Be("slin16");
            fed.Session.Format.Should().Be("slin16", "a second MEDIA_START of another format leaves the first one's format");
            fed.Session.SampleRate.Should().Be(16000, "and the first one's rate");
            fed.MediaStart.Rate.Should().Be(fed.Session.SampleRate);
            fed.Published.Should().BeEmpty();
        }
    }

    [Fact]
    public void MediaStart_ShouldBeNull_WhenNoMediaStartHasBeenRead()
    {
        using var ws = new FakeWebSocket();
        var sut = CreateSession(ws, format: "ulaw");

        ((IChanWebSocketSession)sut).MediaStart.Should().BeNull();
        sut.Format.Should().Be("ulaw");
        sut.SampleRate.Should().Be(8000);
    }

    [Fact]
    public async Task MediaStart_ShouldKeepEveryChannelVariableAsSent_WhenAValueIsNotAString()
    {
        // Arrange — synthetic, the captured JSON MEDIA_START with its channel_variables replaced in the test: every
        // captured value is a string, so a number, an object, a null and two keys that differ by case only are added here
        var captured = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.Json);
        var replaced = ReplaceChannelVariables(captured.Text, """{"N":1,"O":{"x":[1]},"Z":null,"k":"a","K":"b"}""");

        // Act
        await using var fed = await Fed.StartAsync("seg", "slin16", Text(Encoding.UTF8.GetBytes(replaced)));

        // Assert
        using (new AssertionScope())
        {
            // BeAssignableTo is the non-null check here: the member is already declared IReadOnlyDictionary<string, string>
            var variables = fed.MediaStart?.ChannelVariables.Should().BeAssignableTo<IReadOnlyDictionary<string, string>>().Which;
            variables.Should().HaveCount(5, "exactly the members sent");
            variables["N"].Should().Be("1", "a number is kept as its raw JSON text");
            variables["O"].Should().Be("""{"x":[1]}""", "an object is kept as its raw JSON text");
            variables["Z"].Should().Be("null", "a JSON null is kept as its raw JSON text");
            variables["k"].Should().Be("a");
            variables["K"].Should().Be("b", "keys are compared ordinally, so two keys that differ by case are two entries");
            variables.Should().NotBeOfType<Dictionary<string, string>>("the instance is immutable, not the parser's buffer");
            var asMutable = variables.Should().BeAssignableTo<IDictionary<string, string>>().Which;
            asMutable.Invoking(d => d.Add("added", "x")).Should().Throw<NotSupportedException>("the instance is immutable");
            fed.Published.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MediaStart_ShouldReadAnEmptyChannelVariablesObjectAsEmpty_WhenTheFrameSendsNone()
    {
        // Arrange — synthetic, the captured JSON MEDIA_START with "channel_variables":{} in the test
        var captured = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.Json);
        var replaced = ReplaceChannelVariables(captured.Text, "{}");

        // Act
        await using var fed = await Fed.StartAsync("seg", "slin16", Text(Encoding.UTF8.GetBytes(replaced)));

        // Assert
        fed.MediaStart?.ChannelVariables.Should().NotBeNull("{} is an empty object, not an absent one")
            .And.BeEmpty();
    }

    [Theory]
    // The captured MEDIA_START with a leading newline added in the test (synthetic: Asterisk sends none), as the first frame
    [InlineData(ChanWebSocketCaptureFormat.Json, false)]
    [InlineData(ChanWebSocketCaptureFormat.PlainText, false)]
    // The same, after a binary first frame: a late MEDIA_START
    [InlineData(ChanWebSocketCaptureFormat.Json, true)]
    [InlineData(ChanWebSocketCaptureFormat.PlainText, true)]
    public async Task MediaStart_ShouldNeverBePublishedAsAControlMessage_WhenTheFrameStartsWithANewline(ChanWebSocketCaptureFormat format, bool late)
    {
        // Arrange
        var captured = CapturedMediaStart("24.0.0-rc2", format);
        var withNewline = Encoding.UTF8.GetBytes("\n" + captured.Text);
        var frames = late ? new[] { Binary(new byte[320]), Text(withNewline) } : new[] { Text(withNewline) };

        // Act
        await using var fed = await Fed.StartAsync("seg", "ulaw", frames);

        // Assert
        using (new AssertionScope())
        {
            fed.Published.Should().BeEmpty("MEDIA_START is stream state, never a control message, however the frame is padded");
            fed.MediaStart.Should().NotBeNull().And.Match<ChanWebSocketMediaStart>(m => m.ChannelId == captured.ChannelId);
            fed.Session.Format.Should().Be("slin16");
        }
    }

    [Theory]
    [MemberData(nameof(EveryCapturedMediaStart))]
    public void ReadMessage_ShouldMapNoRecord_WhenTheFrameIsMediaStart(int index)
    {
        // Arrange — the captured MEDIA_START as sent, and with leading whitespace added in the test (synthetic)
        var captured = ChanWebSocketWireCapture.MediaStart[index];

        // Act / Assert
        using (new AssertionScope(captured.Source))
        {
            foreach (var text in new[] { captured.Text, "\n" + captured.Text, " \t\r\n" + captured.Text })
            {
                var frame = ChanWebSocketMediaStartParser.Read(Encoding.UTF8.GetBytes(text));
                frame.Head.IsMediaStart.Should().BeTrue("the one read names the frame MEDIA_START and its call");
                frame.Head.ChannelId.Should().Be(captured.ChannelId);
                ChanWebSocketInboundReader.ReadMessage(frame).Should().BeNull("MEDIA_START is never a control message");
            }
        }
    }

    // -- Activity events ---------------------------------------------------------

    [Theory]
    [InlineData("22.10.1", ChanWebSocketCaptureFormat.PlainText)]
    [InlineData("22.10.1", ChanWebSocketCaptureFormat.Json)]
    [InlineData("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText)]
    [InlineData("24.0.0-rc2", ChanWebSocketCaptureFormat.Json)]
    public async Task MediaStart_ShouldBeTracedOnceAsMediaStarted_WhenItIsTheFirstFrame(string version, ChanWebSocketCaptureFormat format)
    {
        // Arrange — an activity started before the session starts
        var captured = CapturedMediaStart(version, format);
        using var tracing = Tracing.Start();

        // Act
        await using var fed = await Fed.StartAsync("seg", "ulaw", Text(captured.Payload()));

        // Assert
        using (new AssertionScope(captured.Source))
        {
            var started = tracing.Activity.Events.Where(e => e.Name == VerbaraSemanticConventions.Events.MediaStarted).ToList();
            started.Should().ContainSingle("the MEDIA_START that fills MediaStart is traced once");
            var tags = started.Count == 1 ? started[0].Tags.ToList() : [];
            tags.Should().Contain(t => t.Key == VerbaraSemanticConventions.Channel.Id && (string?)t.Value == captured.ChannelId);
            tags.Should().Contain(t => t.Key == VerbaraSemanticConventions.Media.Codec && (string?)t.Value == "slin16");
            tags.Should().Contain(t => t.Key == VerbaraSemanticConventions.Media.SampleRate && t.Value is int && (int)t.Value == 16000);
            tags.Should().NotContain(t => t.Key == "media.channels", "a tag that is 1 on every frame is a constant");
            fed.Published.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MediaStart_ShouldBeTracedWithTheFramesChannelId_WhenItArrivesAfterAudio()
    {
        // Arrange
        var captured = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText);
        using var tracing = Tracing.Start();

        // Act
        await using var fed = await Fed.StartAsync("seg-late", "ulaw", Binary(new byte[320]), Text(captured.Payload()));

        // Assert
        var started = tracing.Activity.Events.Where(e => e.Name == VerbaraSemanticConventions.Events.MediaStarted).ToList();
        started.Should().ContainSingle().Which.Tags.Should().Contain(
            t => t.Key == VerbaraSemanticConventions.Channel.Id && (string?)t.Value == captured.ChannelId,
            "the event names the call the frame names, not the path segment");
    }

    [Fact]
    public async Task MediaStart_ShouldAddNoSecondEvent_WhenASecondMediaStartArrives()
    {
        // Arrange
        var first = CapturedMediaStart("24.0.0-rc2", ChanWebSocketCaptureFormat.PlainText);
        var second = ChanWebSocketWireCapture.MediaStart.First(f =>
            f.AsteriskVersion == "22.9.0" && f.Producer == ChanWebSocketCaptureProducer.ExternalMedia && f.Format == ChanWebSocketCaptureFormat.Json);
        using var tracing = Tracing.Start();

        // Act
        await using var fed = await Fed.StartAsync("seg", "ulaw", Text(first.Payload()), Text(second.Payload()));

        // Assert
        tracing.Activity.Events.Count(e => e.Name == VerbaraSemanticConventions.Events.MediaStarted).Should().Be(1);
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task DtmfControlMessage_ShouldEmitDtmfReceivedEventWithNoDuration_WhenActivityCurrent(ChanWebSocketCaptureFormat format)
    {
        // Arrange — the captured DTMF_END
        var captured = ChanWebSocketWireCapture.ControlMessages.First(m => m.Event == "DTMF_END" && m.Format == format);
        using var tracing = Tracing.Start();

        // Act
        await using var fed = await Fed.StartAsync("PJSIP/alice-00042", "slin16", Text(captured.Payload()));

        // Assert
        using (new AssertionScope(captured.Source))
        {
            var events = tracing.Activity.Events.Where(e => e.Name == VerbaraSemanticConventions.Events.DtmfReceived).ToList();
            events.Should().ContainSingle();
            var tags = events.Count == 1 ? events[0].Tags.ToList() : [];
            tags.Should().Contain(t => t.Key == VerbaraSemanticConventions.Channel.Id && (string?)t.Value == "PJSIP/alice-00042");
            tags.Should().Contain(t => t.Key == "asterisk.dtmf.digit" && (string?)t.Value == captured.Value);
            tags.Should().NotContain(t => t.Key == "asterisk.dtmf.duration_ms", "DTMF_END carries no duration");
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task MediaMarkProcessedControlMessage_ShouldEmitMarkProcessedEvent_WhenActivityCurrent(ChanWebSocketCaptureFormat format)
    {
        // Arrange — the captured MEDIA_MARK_PROCESSED with a correlation id
        var captured = ChanWebSocketWireCapture.ControlMessages.First(
            m => m.Event == "MEDIA_MARK_PROCESSED" && m.Format == format && m.Value!.Length > 0);
        using var tracing = Tracing.Start();

        // Act
        await using var fed = await Fed.StartAsync("PJSIP/mark-77", "slin16", Text(captured.Payload()));

        // Assert
        var evt = tracing.Activity.Events.Where(e => e.Name == VerbaraSemanticConventions.Events.MediaMarkProcessed).ToList();
        evt.Should().ContainSingle(captured.Source).Which.Tags.Should()
            .Contain(t => t.Key == VerbaraSemanticConventions.Channel.Id && (string?)t.Value == "PJSIP/mark-77")
            .And.Contain(t => t.Key == "asterisk.media.mark" && (string?)t.Value == captured.Value);
    }

    [Fact]
    public async Task ControlMessage_ShouldNotThrow_WhenNoActivityCurrent()
    {
        // Arrange — no Activity on the context; the event emission must be a no-op
        Activity.Current.Should().BeNull();
        var captured = ChanWebSocketWireCapture.ControlMessages.First(
            m => m.Event == "DTMF_END" && m.Format == ChanWebSocketCaptureFormat.PlainText);

        // Act
        await using var fed = await Fed.StartAsync("ch-1", "slin16", Text(captured.Payload()));

        // Assert
        fed.Published.Should().ContainSingle("the message is delivered").Which.Should().BeOfType<ChanWebSocketDtmf>();
    }

    // -- Outbound commands -------------------------------------------------------
#pragma warning disable CA2012 // `_ = session.Send…Async(…)` in an Action: the facts assert the throw comes before any ValueTask exists
    // Every command is compared with the captured spelling (ChanWebSocketCommandSpelling). A session is "plain" or "JSON"
    // by the captured MEDIA_START it read first; one that has read none has no format and writes no command.

    private const string FormatNotYetKnown = "control format is not yet known";

    /// <summary>A started session fed nothing: MediaStart is null, as a server with MediaStartTimeout = Zero announces it.</summary>
    private static (FakeWebSocket Ws, WebSocketAudioSession Session) StartedFedNothing()
    {
        var ws = new FakeWebSocket();
        var session = CreateSession(ws);
        session.Start();
        return (ws, session);
    }

    /// <summary>A session pre-fed the captured MEDIA_START of <paramref name="format"/> and one 640-byte frame, read by the consumer.</summary>
    private static async Task<(FakeWebSocket Ws, WebSocketAudioSession Session)> NegotiatedAsync(ChanWebSocketCaptureFormat format)
    {
        var ws = new FakeWebSocket();
        ws.EnqueueReceive(CapturedMediaStart("24.0.0-rc2", format).Payload(), WebSocketMessageType.Text);
        ws.EnqueueReceive(new byte[640]);
        var session = CreateSession(ws);
        session.Start();
        var frame = await session.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        frame.Length.Should().Be(640, "the pump handles frames in order, so the MEDIA_START before it has been read");
        return (ws, session);
    }

    /// <summary>
    /// Runs <paramref name="act"/> and returns what it threw, without awaiting anything: a throw captured in a returned
    /// ValueTask reads as no exception, so a non-null result proves the throw came before any ValueTask existed. Any other exception type escapes and fails the test.
    /// </summary>
    private static TException? Synchronously<TException>(Action act)
        where TException : Exception
    {
        try
        {
            act();
            return null;
        }
        catch (TException ex)
        {
            return ex;
        }
    }

    private static async Task DisposeAsync((FakeWebSocket Ws, WebSocketAudioSession Session) s)
    {
        await s.Session.DisposeAsync();
        s.Ws.Dispose();
    }

    [Fact]
    public async Task FakeWebSocket_ShouldCompleteAPendingReceive_WhenAFrameIsEnqueuedAfterIt()
    {
        // Arrange — the seam the facts below stand on: a receive already parked on an empty queue
        using var ws = new FakeWebSocket();
        var buffer = new byte[16];
        var pending = ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask();
        pending.IsCompleted.Should().BeFalse("nothing is queued yet");

        // Act
        ws.EnqueueReceive([1, 2, 3]);

        // Assert
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result.Count.Should().Be(3);
        buffer[..3].Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText, ChanWebSocketControlMessageFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json, ChanWebSocketControlMessageFormat.Json)]
    public async Task MediaStart_ShouldSayTheControlFormatItArrivedIn_WhenTheCapturedMediaStartIsRead(
        ChanWebSocketCaptureFormat format, ChanWebSocketControlMessageFormat expected)
    {
        var s = await NegotiatedAsync(format);
        try
        {
            ((IChanWebSocketSession)s.Session).MediaStart!.ControlMessageFormat.Should().Be(expected);
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText, 117)]
    [InlineData(ChanWebSocketCaptureFormat.Json, 84)]
    public async Task SendMarkAsync_ShouldWriteOne128ByteFrameInTheCapturedSpelling_WhenTheMarkFillsTheFrame(
        ChanWebSocketCaptureFormat format, int markBytes)
    {
        // Arrange
        var s = await NegotiatedAsync(format);
        var mark = new string('a', markBytes);
        var expected = format == ChanWebSocketCaptureFormat.PlainText
            ? ChanWebSocketCommandSpelling.MarkPlain(mark)
            : ChanWebSocketCommandSpelling.MarkJson(mark);
        try
        {
            // Act
            await s.Session.SendMarkAsync(mark);

            // Assert
            using (new AssertionScope())
            {
                s.Ws.Sent.Should().ContainSingle();
                s.Ws.Sent[0].Type.Should().Be(WebSocketMessageType.Text);
                s.Ws.Sent[0].Data.Should().Equal(expected);
                s.Ws.Sent[0].Data.Should().HaveCount(ChanWebSocketControlMessageSerializer.MaxCommandBytes);
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    public static TheoryData<ChanWebSocketCaptureFormat, string, string> MarksOverTheFrame() => new()
    {
        // (a)/(b): one ASCII byte over the budget, 129-byte frames
        { ChanWebSocketCaptureFormat.PlainText, new string('a', 118), "118 ASCII bytes" },
        { ChanWebSocketCaptureFormat.Json, new string('a', 85), "85 ASCII bytes" },
        // (c): 59 characters but 118 UTF-8 bytes; (d): 130 bytes raw, 302 escaped
        { ChanWebSocketCaptureFormat.PlainText, new string('\u00E9', 59), "59 x U+00E9" },
        { ChanWebSocketCaptureFormat.Json, new string('\u00E9', 43), "43 x U+00E9" },
        // The length the captures measured dropped unread, no reply, on every version in both formats
        { ChanWebSocketCaptureFormat.PlainText, new string('a', 200), "200 ASCII bytes" },
        { ChanWebSocketCaptureFormat.Json, new string('a', 200), "200 ASCII bytes" },
    };

    [Theory]
    [MemberData(nameof(MarksOverTheFrame))]
    public async Task SendMarkAsync_ShouldThrowSynchronouslyAndWriteNothing_WhenTheFrameWouldExceedTheLimit(
        ChanWebSocketCaptureFormat format, string mark, string because)
    {
        // Arrange
        var s = await NegotiatedAsync(format);
        try
        {
            var negotiated = ((IChanWebSocketSession)s.Session).MediaStart!.ControlMessageFormat;
            var encoded = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(new ChanWebSocketMarkMedia(mark), negotiated).Length;

            // Act — never awaited: the throw comes before any ValueTask exists
            var thrown = Synchronously<ArgumentOutOfRangeException>(() => _ = s.Session.SendMarkAsync(mark));

            // Assert
            using (new AssertionScope(because))
            {
                s.Ws.Sent.Should().BeEmpty("nothing is written for a frame Asterisk drops unread");
                var refused = thrown.Should().BeOfType<ArgumentOutOfRangeException>().Subject;
                refused.ParamName.Should().Be("mark");
                refused.ActualValue.Should().Be(encoded);
                refused.Message.Should().Contain(encoded.ToString(CultureInfo.InvariantCulture)).And.Contain(negotiated.ToString())
                    .And.Contain("128").And.Contain("117").And.Contain("84");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task SendMarkAsync_ShouldCarryThe129ByteEncodingAsItsActualValue_WhenTheMarkIsOneByteOver(ChanWebSocketCaptureFormat format)
    {
        var s = await NegotiatedAsync(format);
        try
        {
            var mark = new string('a', format == ChanWebSocketCaptureFormat.PlainText ? 118 : 85);
            var thrown = Synchronously<ArgumentOutOfRangeException>(() => _ = s.Session.SendMarkAsync(mark));

            using (new AssertionScope())
            {
                s.Ws.Sent.Should().BeEmpty();
                thrown.Should().BeOfType<ArgumentOutOfRangeException>().Which.ActualValue.Should().Be(129);
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Fact]
    public async Task SendMarkAsync_ShouldWriteThe11ByteCommand_WhenTheMarkIsEmptyOnAPlainTextConnection()
    {
        var s = await NegotiatedAsync(ChanWebSocketCaptureFormat.PlainText);
        try
        {
            await s.Session.SendMarkAsync("");

            s.Ws.Sent.Should().ContainSingle().Which.Data.Should().Equal(ChanWebSocketCommandSpelling.MarkPlain("")).And.HaveCount(11);
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Theory]
    [InlineData(ChanWebSocketMediaDirection.In, "in")]
    [InlineData(ChanWebSocketMediaDirection.Out, "out")]
    [InlineData(ChanWebSocketMediaDirection.Both, "both")]
    public async Task SendSetMediaDirectionAsync_ShouldWriteTheCapturedJsonCommand_WhenTheConnectionIsJson(
        ChanWebSocketMediaDirection direction, string wireValue)
    {
        var s = await NegotiatedAsync(ChanWebSocketCaptureFormat.Json);
        try
        {
            await s.Session.SendSetMediaDirectionAsync(direction);

            using (new AssertionScope())
            {
                var sent = s.Ws.Sent.Should().ContainSingle().Subject;
                sent.Type.Should().Be(WebSocketMessageType.Text);
                sent.Data.Should().Equal(ChanWebSocketCommandSpelling.SetMediaDirectionJson(wireValue));
                if (direction == ChanWebSocketMediaDirection.Both)
                    sent.Data.Should().HaveCount(52, "the longest of the three, under the limit");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Fact]
    public async Task SendSetMediaDirectionAsync_ShouldThrowSynchronouslyNamingTheRemedy_WhenTheConnectionIsPlainText()
    {
        var s = await NegotiatedAsync(ChanWebSocketCaptureFormat.PlainText);
        try
        {
            var thrown = Synchronously<InvalidOperationException>(() => _ = s.Session.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.Both));

            using (new AssertionScope())
            {
                s.Ws.Sent.Should().BeEmpty();
                thrown.Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain("control_message_format = json").And.Contain("f(json) to a dial string you write");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Fact]
    public async Task SendSetMediaDirectionAsync_ShouldThrowSynchronouslyAndWriteNothing_WhenNoMediaStartHasBeenRead()
    {
        var s = StartedFedNothing();
        try
        {
            var thrown = Synchronously<InvalidOperationException>(() => _ = s.Session.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.Both));

            using (new AssertionScope())
            {
                s.Ws.Sent.Should().BeEmpty();
                thrown.Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain(FormatNotYetKnown).And.Contain("MediaStartTimeout");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    public async Task SendMarkAsync_ShouldBeRefusedUntilAMediaStartIsRead_ThenWriteInItsFormat(ChanWebSocketCaptureFormat format)
    {
        // Arrange — (h): a started session fed nothing
        var s = StartedFedNothing();
        try
        {
            // Act — never awaited: the refusal is synchronous
            var refused = Synchronously<InvalidOperationException>(() => _ = s.Session.SendMarkAsync("m"));

            // Assert
            using (new AssertionScope("before MEDIA_START"))
            {
                s.Ws.Sent.Should().BeEmpty();
                ((IChanWebSocketSession)s.Session).MediaStart.Should().BeNull();
                refused.Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain(FormatNotYetKnown).And.Contain("MediaStartTimeout");
            }
            var sentBeforeMediaStart = s.Ws.Sent.Length;

            // Act — (i)/(j): the captured MEDIA_START and an audio frame arrive on the session already parked in its receive
            s.Ws.EnqueueReceive(CapturedMediaStart("24.0.0-rc2", format).Payload(), WebSocketMessageType.Text);
            s.Ws.EnqueueReceive(new byte[640]);
            var frame = await s.Session.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await s.Session.SendMarkAsync("m");

            // Assert
            using (new AssertionScope("after MEDIA_START"))
            {
                frame.Length.Should().Be(640);
                ((IChanWebSocketSession)s.Session).MediaStart.Should().NotBeNull();
                var expected = format == ChanWebSocketCaptureFormat.Json
                    ? ChanWebSocketCommandSpelling.MarkJson("m")
                    : ChanWebSocketCommandSpelling.MarkPlain("m");
                s.Ws.Sent.Skip(sentBeforeMediaStart).Should().ContainSingle().Which.Data.Should().Equal(expected)
                    .And.HaveCount(format == ChanWebSocketCaptureFormat.Json ? 45 : 12);
                s.Ws.Sent.Should().HaveCount(1, "the mark is the only entry");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

    [Fact]
    public async Task Commands_ShouldBeRefused_WhenThePeerSentAudioAndNoMediaStart()
    {
        // Arrange — (k): one audio frame and no MEDIA_START, read by the consumer, which then writes one frame
        using var ws = new FakeWebSocket();
        ws.EnqueueReceive(new byte[640]);
        var sut = CreateSession(ws);
        sut.Start();
        (await sut.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Length.Should().Be(640);
        var audio = new byte[] { 1, 2, 3 };
        await sut.WriteFrameAsync(audio);

        // Act — never awaited
        var mark = Synchronously<InvalidOperationException>(() => _ = sut.SendMarkAsync("m"));
        var direction = Synchronously<InvalidOperationException>(() => _ = sut.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.Both));

        // Assert
        using (new AssertionScope())
        {
            ((IChanWebSocketSession)sut).MediaStart.Should().BeNull();
            ws.Sent.Should().ContainSingle("only the audio frame the consumer wrote");
            ws.Sent[0].Type.Should().Be(WebSocketMessageType.Binary);
            ws.Sent[0].Data.Should().Equal(audio);
            mark.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain(FormatNotYetKnown);
            direction.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain(FormatNotYetKnown);
        }

        await sut.DisposeAsync();
    }

    public static TheoryData<string, string> EveryStateForTheDeprecatedSends()
    {
        var data = new TheoryData<string, string>();
        foreach (var method in new[] { "Xon", "Xoff" })
        {
            foreach (var state in new[] { "none", "plain", "json" })
                data.Add(method, state);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStateForTheDeprecatedSends))]
    public async Task SendXonAndSendXoff_ShouldThrowNotSupportedExceptionSynchronouslyAndWriteNothing_InEveryState(string method, string state)
    {
        // Arrange — (r)/(s): no MEDIA_START, a plain-text one, a JSON one
        var s = state switch
        {
            "none" => StartedFedNothing(),
            "plain" => await NegotiatedAsync(ChanWebSocketCaptureFormat.PlainText),
            _ => await NegotiatedAsync(ChanWebSocketCaptureFormat.Json),
        };
        try
        {
            var mediaStart = ((IChanWebSocketSession)s.Session).MediaStart;
            var sentBefore = s.Ws.Sent.Length;

            // Act — never awaited: the throw is the method's first statement
#pragma warning disable VSDK0004 // the obsolete call is the point
            var thrown = method == "Xon"
                ? Synchronously<NotSupportedException>(() => _ = s.Session.SendXonAsync())
                : Synchronously<NotSupportedException>(() => _ = s.Session.SendXoffAsync());
#pragma warning restore VSDK0004

            // Assert
            using (new AssertionScope($"{method} {state}"))
            {
                s.Ws.Sent.Should().HaveCount(sentBefore).And.BeEmpty();
                ((IChanWebSocketSession)s.Session).MediaStart.Should().BeSameAs(mediaStart);
                thrown.Should().BeOfType<NotSupportedException>()
                    .Which.Message.Should().Contain("no XON/XOFF command").And.Contain("ChanWebSocketMediaXoff");
            }
        }
        finally
        {
            await DisposeAsync(s);
        }
    }

#pragma warning restore CA2012

    [Theory]
    // Synthetic, in the captured JSON grammar: a lone surrogate escape the reader accepts and GetString() cannot decode.
    [InlineData("""{"event":"DTMF_END","digit":"\uD800","channel_id":"synthetic-1"}""")]
    // Synthetic, the same in a member name.
    [InlineData("""{"event":"DTMF_END","\uDC00":"1","channel_id":"synthetic-1"}""")]
    // Synthetic, an invalid escape.
    [InlineData("""{"event":"DTMF_END","digit":"\x","channel_id":"synthetic-1"}""")]
    public async Task ControlMessages_ShouldPublishTheGenericRecordAndKeepReading_WhenAJsonValueIsUnreadable(string synthetic)
    {
        // Arrange — the unreadable frame, then an audio marker; no close frame, so the session can only end on its own
        using var ws = new FakeWebSocket();
        ws.EnqueueReceive(Encoding.UTF8.GetBytes(synthetic), WebSocketMessageType.Text);
        ws.EnqueueReceive(AudioMarker);
        var sut = CreateSession(ws);
        var published = new ConcurrentQueue<ChanWebSocketControlMessage>();
        var states = new ConcurrentQueue<AudioStreamState>();
        using var messages = sut.ControlMessages.Subscribe(published.Enqueue);
        using var stateChanges = sut.StateChanges.Subscribe(states.Enqueue);
        sut.Start();

        // Act — the marker is read only if the pump read past the unreadable frame
        var marker = await sut.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        using (new AssertionScope(synthetic))
        {
            marker.Length.Should().Be(AudioMarker.Length, "the next frame is read");
            var generic = published.Should().ContainSingle().Which.Should().BeOfType<ChanWebSocketUnknownMessage>().Subject;
            generic.RawText.Should().Be(synthetic);
            sut.IsConnected.Should().BeTrue("the session did not end");
            states.Should().Equal([AudioStreamState.Connected], "no ending was published");
        }

        await sut.DisposeAsync();
    }

    // -- helpers -----------------------------------------------------------------

    private static ChanWebSocketCapturedFrame CapturedMediaStart(string version, ChanWebSocketCaptureFormat format) =>
        ChanWebSocketWireCapture.MediaStart.First(f =>
            f.AsteriskVersion == version && f.Producer == ChanWebSocketCaptureProducer.ExternalMedia && f.Format == format && f.Option == "none");

    private static (WebSocketMessageType Type, byte[] Data) Text(byte[] data) => (WebSocketMessageType.Text, data);

    /// <summary>A captured JSON frame with its <c>channel_variables</c> object replaced, found with JsonDocument, never by the SDK.</summary>
    private static string ReplaceChannelVariables(string capturedJson, string replacement)
    {
        using var json = JsonDocument.Parse(capturedJson);
        var original = json.RootElement.GetProperty("channel_variables").GetRawText();
        var at = capturedJson.IndexOf(original, StringComparison.Ordinal);
        at.Should().BePositive("the captured frame carries channel_variables");
        return string.Concat(capturedJson.AsSpan(0, at), replacement, capturedJson.AsSpan(at + original.Length));
    }

    private static (WebSocketMessageType Type, byte[] Data) Binary(byte[] data) => (WebSocketMessageType.Binary, data);

    /// <summary>
    /// A session fed the given frames, then an audio marker frame and a close frame, read up to the marker: the pump
    /// handles frames in order, so every frame before the marker has been handled once the marker is read.
    /// </summary>
    private sealed class Fed : IAsyncDisposable
    {
        private readonly FakeWebSocket _ws;
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<ChanWebSocketControlMessage> _published;

        private Fed(FakeWebSocket ws, WebSocketAudioSession session, IDisposable subscription, ConcurrentQueue<ChanWebSocketControlMessage> published)
        {
            _ws = ws;
            Session = session;
            _subscription = subscription;
            _published = published;
        }

        public WebSocketAudioSession Session { get; }

        /// <summary>The session's <c>MediaStart</c>, read through the interface a consumer holds.</summary>
        public ChanWebSocketMediaStart? MediaStart => ((IChanWebSocketSession)Session).MediaStart;

        public IReadOnlyList<ChanWebSocketControlMessage> Published => _published.ToArray();

        /// <summary>The audio frames read before the marker.</summary>
        public List<ReadOnlyMemory<byte>> Audio { get; } = [];

        /// <summary>True when the marker, fed after every frame, was read as the last audio frame.</summary>
        public bool MarkerDelivered { get; private set; }

        public static async Task<Fed> StartAsync(string channelId, string format, params (WebSocketMessageType Type, byte[] Data)[] frames)
        {
            var ws = new FakeWebSocket();
            foreach (var (type, data) in frames)
                ws.EnqueueReceive(data, type);
            ws.EnqueueReceive(AudioMarker);
            ws.EnqueueClose();

            var session = CreateSession(ws, channelId, format);
            var published = new ConcurrentQueue<ChanWebSocketControlMessage>();
            var subscription = session.ControlMessages.Subscribe(published.Enqueue);
            var fed = new Fed(ws, session, subscription, published);
            session.Start();

            // One read per binary frame fed, then the marker.
            var reads = frames.Count(f => f.Type == WebSocketMessageType.Binary) + 1;
            for (var i = 0; i < reads; i++)
                fed.Audio.Add(await session.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            // The marker is the last frame read; the frames before it are the ones fed.
            fed.MarkerDelivered = fed.Audio[^1].Length == AudioMarker.Length && fed.Audio[^1].Span[0] == 0x7E;
            fed.Audio.RemoveAt(fed.Audio.Count - 1);
            return fed;
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Session.DisposeAsync();
            _ws.Dispose();
        }
    }

    /// <summary>An activity of a test-only source, current on this execution context from its start.</summary>
    private sealed class Tracing : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        private Tracing(ActivitySource source, ActivityListener listener, Activity activity)
        {
            _source = source;
            _listener = listener;
            Activity = activity;
        }

        public Activity Activity { get; }

        public static Tracing Start()
        {
            var name = "test.source." + Guid.NewGuid().ToString("N");
            var source = new ActivitySource(name);
            var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);
            var activity = source.StartActivity("chanws-session");
            activity.Should().NotBeNull();
            return new Tracing(source, listener, activity!);
        }

        public void Dispose()
        {
            Activity.Dispose();
            _listener.Dispose();
            _source.Dispose();
        }
    }

    /// <summary>The fields of a captured MEDIA_START, read with JsonDocument or by splitting tokens, never by the SDK.</summary>
    private sealed record CapturedFields(
        string Format, string? ConnectionId, string? Channel, int? OptimalFrameSize, int? Ptime,
        bool? Passthrough, bool? Unbuffered, Dictionary<string, string>? ChannelVariables)
    {
        public static CapturedFields Read(ChanWebSocketCapturedFrame frame)
        {
            if (frame.Format == ChanWebSocketCaptureFormat.Json)
            {
                using var json = JsonDocument.Parse(frame.Text);
                var root = json.RootElement;
                bool? Flag(string name) => root.TryGetProperty(name, out var v) ? v.GetBoolean() : null;
                return new CapturedFields(
                    root.GetProperty("format").GetString()!,
                    root.GetProperty("connection_id").GetString(),
                    root.GetProperty("channel").GetString(),
                    root.GetProperty("optimal_frame_size").GetInt32(),
                    root.GetProperty("ptime").GetInt32(),
                    Flag("passthrough"),
                    Flag("unbuffered"),
                    root.GetProperty("channel_variables").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal));
            }

            var fields = frame.Text.Split(' ').Skip(1)
                .Select(t => t.Split(':', 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            bool? Bool(string name) => fields.TryGetValue(name, out var v) ? bool.Parse(v) : null;
            return new CapturedFields(
                fields["format"], fields["connection_id"], fields["channel"],
                int.Parse(fields["optimal_frame_size"], CultureInfo.InvariantCulture),
                int.Parse(fields["ptime"], CultureInfo.InvariantCulture),
                Bool("passthrough"), Bool("unbuffered"), null);
        }
    }

    // -- FakeWebSocket ----------------------------------------------------------

    private sealed class FakeWebSocket : WebSocket
    {
        // Unbounded and signalled: a receive pending on an empty queue completes when the test enqueues, cancellation
        // still ends it, and the test and pump threads never share an unsynchronised queue.
        private readonly Channel<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _receive =
            Channel.CreateUnbounded<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)>();
        private readonly List<(WebSocketMessageType Type, byte[] Data)> _sent = [];
        private volatile WebSocketState _state = WebSocketState.Open;

        /// <summary>Every message sent, in order, with its type: a snapshot.</summary>
        public (WebSocketMessageType Type, byte[] Data)[] Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public IReadOnlyList<byte[]> SentData => Sent.Select(m => m.Data).ToArray();

        public void EnqueueReceive(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary, bool endOfMessage = true)
            => _receive.Writer.TryWrite((type, data, endOfMessage));

        public void EnqueueClose()
            => _receive.Writer.TryWrite((WebSocketMessageType.Close, [], true));

        public void Close() => _state = WebSocketState.Closed;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var (type, data, eom) = await _receive.Reader.ReadAsync(ct);
            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var count = Math.Min(data.Length, buffer.Length);
            data.AsMemory(0, count).CopyTo(buffer);
            return new ValueWebSocketReceiveResult(count, type, eom);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add((type, buffer.ToArray()));
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

        // A byte[] binds to this overload; recorded like the Memory one, so no send goes unseen.
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
            => SendAsync(buffer.AsMemory(), type, endOfMessage, ct).AsTask();

        public override void Abort() => _state = WebSocketState.Aborted;

#pragma warning disable IDISP010 // WebSocket.Dispose is abstract — no base to call
        public override void Dispose() => _state = WebSocketState.Closed;
#pragma warning restore IDISP010
    }
}
