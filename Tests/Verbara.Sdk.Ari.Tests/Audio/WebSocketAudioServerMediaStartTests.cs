using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.TestInfrastructure.Wire;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A WebSocket audio stream is keyed by the call its first frame names. chan_websocket upgrades every call of one
/// <c>websocket_client</c> connection on the same fixed path, and its first frame is <c>MEDIA_START</c>, whose
/// <c>channel_id</c> is the only place the call is named. Every frame Asterisk would send here is one of the
/// captured frames in <see cref="ChanWebSocketWireCapture"/>, never a string this SDK builds.
/// </summary>
/// <remarks>
/// <para>
/// The tests follow their subject, not their site. Those about the re-key run on the default
/// <see cref="AudioServerOptions.MediaStartTimeout"/> and send the captured <c>MEDIA_START</c> before they wait for
/// the announcement, because the announcement comes after it. Those about a first frame that does not name a call
/// send that frame, and the wait ends on it. Those about the bound run on a <see cref="FakeTimeProvider"/>, read the
/// wait's timer as the server arms it, and move the clock only where the bound is the subject. A low real bound is
/// never used as a shortcut: under a fake clock it never fires, and on a real one it races what the test measures.
/// </para>
/// </remarks>
public sealed class WebSocketAudioServerMediaStartTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DefaultMediaStartTimeout = new AudioServerOptions().MediaStartTimeout;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The fixed path segment every call of one <c>websocket_client</c> entry upgrades on.</summary>
    private const string SharedSegment = "fixedpath";

    // Two calls captured off one Asterisk, one per producer, in each format.
    private static ChanWebSocketCapturedFrame DialPlain => Capture("24.0.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText);

    private static ChanWebSocketCapturedFrame ExternalMediaPlain => Capture("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText);

    private static ChanWebSocketCapturedFrame DialJson => Capture("24.0.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json);

    private static ChanWebSocketCapturedFrame ExternalMediaJson => Capture("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json);

    // ================================================================ (a), (b): the re-key

    [Fact]
    public Task GetStream_ShouldFindEachCallByItsChannelId_WhenTwoPlainTextMediaStartsOnOnePathNameTwoCalls() =>
        EachCallIsFoundByItsChannelIdAsync(DialPlain, ExternalMediaPlain);

    [Fact]
    public Task GetStream_ShouldFindEachCallByItsChannelId_WhenTwoJsonMediaStartsOnOnePathNameTwoCalls() =>
        EachCallIsFoundByItsChannelIdAsync(DialJson, ExternalMediaJson);

    private static async Task EachCallIsFoundByItsChannelIdAsync(ChanWebSocketCapturedFrame first, ChanWebSocketCapturedFrame second)
    {
        // Arrange — two calls through one websocket_client entry: both upgrade on its fixed path
        await using var harness = await Harness.StartAsync();

        // Act — each sends its captured MEDIA_START as its first frame, before its announcement is awaited
        using var one = await harness.ConnectNamedAsync(SharedSegment, first);
        using var two = await harness.ConnectNamedAsync(SharedSegment, second);

        // Assert
        using (new AssertionScope())
        {
            harness.Server.GetStream(first.ChannelId).Should().BeSameAs(
                one.Stream, "the first call is found by the channel_id its MEDIA_START carries ({0})", first.Source);
            harness.Server.GetStream(second.ChannelId).Should().BeSameAs(
                two.Stream, "the second call is found by its own channel_id ({0})", second.Source);
            one.Stream.ChannelId.Should().Be(first.ChannelId, "the stream reports the call it named as its key");
            two.Stream.ChannelId.Should().Be(second.ChannelId, "the stream reports the call it named as its key");
            harness.Server.GetStream(SharedSegment).Should().BeNull(
                "the path names no call: every call of the entry upgrades on it");
            harness.Server.ActiveStreamCount.Should().Be(2);
        }
    }

    // ================================================================ (i): two connections name one call

    [Fact]
    public async Task GetStream_ShouldReturnTheFirst_WhenTwoConnectionsNameTheSameCall()
    {
        // Arrange — Asterisk does not reject a duplicate channel_id, so two live connections can name one call
        await using var harness = await Harness.StartAsync();
        var frame = ExternalMediaPlain;

        // Act — on two different paths, so the path cannot be what ties them
        using var first = await harness.ConnectNamedAsync("path-one", frame);
        using var second = await harness.ConnectNamedAsync("path-two", frame);

        // Assert
        using (new AssertionScope())
        {
            harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(
                first.Stream, "the earliest live connection that named the call holds its key");
            harness.Server.ActiveStreamCount.Should().Be(2, "both live connections are counted");
            harness.Server.ActiveStreams.Should().HaveCount(2).And.Contain(first.Stream).And.Contain(second.Stream);
            second.Stream.ChannelId.Should().Be(frame.ChannelId, "it named the same call");
        }

        // Act — the holder ends: the other one that named the call takes the key over
        await first.CloseAsync();
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(second.Stream);
    }

    // ================================================================ (j): the wait holds a place, unlisted

    [Fact]
    public async Task HandleConnection_ShouldHoldItsPlaceButStayUnlisted_WhileItWaitsToBeNamed()
    {
        // Arrange — the default bound on a clock that never moves, so the wait is held open for as long as the
        // test reads it, and nothing elapses
        var time = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(time: time);
        var frame = DialPlain;
        var announced = harness.Announcements.NextAsync();

        using var client = await ConnectAsync(harness.Port, SharedSegment);
        var waiting = TimerDueAsync(time, DefaultMediaStartTimeout);
        await Task.WhenAny(waiting, announced).WaitAsync(WaitLimit);

        // Assert — admitted and waiting: it holds its place, and nothing lists, counts or finds it
        using (new AssertionScope())
        {
            announced.IsCompleted.Should().BeFalse("a connection that has not sent its first frame is not announced yet");
            harness.Server.ActiveStreamCount.Should().Be(0, "a connection waiting to be named is not counted");
            harness.Server.ActiveStreams.Should().BeEmpty("a connection waiting to be named is not listed");
            harness.Server.GetStream(SharedSegment).Should().BeNull("a connection waiting to be named is not found");
            harness.Server.GetStream(frame.ChannelId).Should().BeNull();
            harness.Options.Admission.Held.Should().Be(1, "the place taken at the accept is held through the wait");
        }

        // Act — the first frame names the call
        await SendTextAsync(client, frame);
        var (stream, _) = await announced.WaitAsync(WaitLimit);

        // Assert — from its announcement it is listed, counted and found
        using (new AssertionScope())
        {
            stream.ChannelId.Should().Be(frame.ChannelId);
            harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(stream);
            harness.Server.ActiveStreamCount.Should().Be(1);
        }
    }

    // ================================================================ (n): the gate is the first frame

    [Theory]
    [InlineData(null, ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(null, ChanWebSocketCaptureFormat.Json)]
    [InlineData("chat", ChanWebSocketCaptureFormat.PlainText)]
    [InlineData("chat", ChanWebSocketCaptureFormat.Json)]
    public async Task HandleConnection_ShouldKeyByMediaStart_WhateverSubprotocolTheClientOffers(
        string? offered, ChanWebSocketCaptureFormat format)
    {
        // Arrange — the offer is operator configuration (protocols = …), so it decides nothing: only the first
        // frame does
        await using var harness = await Harness.StartAsync();
        var frame = Capture("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, format);

        // Act
        using var client = await harness.ConnectNamedAsync(SharedSegment, frame, offered);

        // Assert
        using (new AssertionScope())
        {
            client.Stream.ChannelId.Should().Be(
                frame.ChannelId, "a connection whose first frame is MEDIA_START is named by it, offer or no offer");
            harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(client.Stream);
            harness.Server.GetStream(SharedSegment).Should().BeNull("the path segment is not the named stream's key");
        }
    }

    // ================================================================ (o): a second MEDIA_START is ignored

    [Fact]
    public async Task HandleConnection_ShouldIgnoreAndLogASecondMediaStart_WhenTheFirstFrameNamedTheCall()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var naming = ExternalMediaPlain;
        var second = Capture("22.9.0", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json);
        // Every capture is slin16, so the second is made "of another format" in the test: synthetic, the captured JSON
        // MEDIA_START with "format":"slin16" replaced by "format":"ulaw"
        var secondOfAnotherFormat = second.Text.Replace("\"format\":\"slin16\"", "\"format\":\"ulaw\"", StringComparison.Ordinal);
        secondOfAnotherFormat.Should().NotBe(second.Text, "the synthetic frame differs from the capture in its format only");
        using var client = await harness.ConnectNamedAsync(SharedSegment, naming);
        var published = 0;
        using var controls = ((IChanWebSocketSession)client.Stream).ControlMessages.Subscribe(
            _ => Interlocked.Increment(ref published));

        // Act — a second MEDIA_START naming another call in another format, then an audio frame, read so the text frame
        // before it has been handled when the assertions run
        await client.Client.SendAsync(
            Encoding.UTF8.GetBytes(secondOfAnotherFormat), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await client.Client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var audio = await client.Stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            audio.Length.Should().Be(640);
            client.Stream.ChannelId.Should().Be(naming.ChannelId, "only the first frame names the stream");
            harness.Server.GetStream(naming.ChannelId).Should().BeSameAs(client.Stream);
            harness.Server.GetStream(second.ChannelId).Should().BeNull("a later MEDIA_START re-keys nothing");
            Volatile.Read(ref published).Should().Be(0, "MEDIA_START is stream state, never a control message");
            var mediaStart = ((IChanWebSocketSession)client.Stream).MediaStart;
            mediaStart.Should().NotBeNull();
            mediaStart?.ChannelId.Should().Be(naming.ChannelId, "a second MEDIA_START leaves MediaStart at the first one's values");
            mediaStart?.Format.Should().Be("slin16");
            client.Stream.Format.Should().Be("slin16", "a second MEDIA_START of another format leaves the first one's format");
            client.Stream.SampleRate.Should().Be(16000, "and the first one's rate");
            mediaStart?.Rate.Should().Be(client.Stream.SampleRate);
            TheIgnoredMediaStartIsLogged(logger, naming, second);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldLogTheCallTheFirstMediaStartNamed_WhenTheSecondArrivesRightBehindIt()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var naming = ExternalMediaPlain;
        var second = Capture("22.9.0", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json);
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, SharedSegment);

        // Act — the naming MEDIA_START and, right behind it, a second one naming another call. The pump can read the
        // second before the server re-keys the session, so the entry cannot take the call from the session's key.
        // Then an audio frame, read so the text frame before it has been handled when the assertions run
        await SendTextAsync(client, naming);
        await SendTextAsync(client, second);
        await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var (stream, _) = await announced.WaitAsync(WaitLimit);
        var audio = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            audio.Length.Should().Be(640);
            stream.ChannelId.Should().Be(naming.ChannelId, "only the first frame names the stream");
            TheIgnoredMediaStartIsLogged(logger, naming, second);
        }
    }

    private static void TheIgnoredMediaStartIsLogged(
        RecordingLogger logger, ChanWebSocketCapturedFrame naming, ChanWebSocketCapturedFrame second)
    {
        var entries = logger.Entries.Where(e => e.Level == LogLevel.Debug && e.EventName == "SecondMediaStartIgnored").ToList();
        entries.Should().ContainSingle("the ignored MEDIA_START is recorded once at Debug; logged [{0}]", logger.Describe());
        var message = entries.Count == 1 ? entries[0].Message : string.Empty;
        message.Should().Contain($"path_segment={SharedSegment} ", "the entry names the path the connection upgraded on");
        message.Should().Contain(
            $"first_channel_id={naming.ChannelId} ", "the entry names the call the first MEDIA_START named, never the path in its place");
        message.Should().EndWith($"ignored_channel_id={second.ChannelId}");
    }

    // ================================================================ (g), (h): a first frame that does not name

    [Fact]
    public async Task HandleConnection_ShouldAnnounceUnderThePathSegment_WhenTheFirstFrameIsBinary()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger, defaultFormat: "ulaw");
        var frame = DialPlain;
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, "seg-binary");

        // Act — an audio frame first, then a MEDIA_START
        var first = new byte[640];
        first[0] = 0x5A;
        await client.SendAsync(first, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var (stream, _) = await announced.WaitAsync(WaitLimit);
        var published = 0;
        using var controls = ((IChanWebSocketSession)stream).ControlMessages.Subscribe(_ => Interlocked.Increment(ref published));
        await SendTextAsync(client, frame);
        await client.SendAsync(new byte[320], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        var firstRead = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);
        var secondRead = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            firstRead.Length.Should().Be(640, "the binary first frame is the stream's first audio frame");
            firstRead.Span[0].Should().Be(0x5A);
            secondRead.Length.Should().Be(320);
            stream.ChannelId.Should().Be("seg-binary", "a binary first frame leaves the connection unnamed");
            harness.Server.GetStream("seg-binary").Should().BeSameAs(stream);
            harness.Server.GetStream(frame.ChannelId).Should().BeNull("a MEDIA_START after the first frame re-keys nothing");
            Volatile.Read(ref published).Should().Be(0, "MEDIA_START is never a control message");
            var mediaStart = ((IChanWebSocketSession)stream).MediaStart;
            mediaStart.Should().NotBeNull("a late MEDIA_START fills MediaStart");
            mediaStart?.ChannelId.Should().Be(frame.ChannelId, "MediaStart carries the later MEDIA_START's values");
            stream.Format.Should().Be("slin16", "a late MEDIA_START corrects the format once");
            stream.SampleRate.Should().Be(16000);
            mediaStart?.Rate.Should().Be(stream.SampleRate);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Debug && e.EventName == "ConnectionUnnamedByBinaryFirstFrame" && e.Message.Contains("seg-binary"),
                "why the connection is unnamed is logged at Debug; logged [{0}]", logger.Describe());
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldAnnounceUnderThePathSegment_WhenTheFirstFrameIsTextThatIsNotMediaStart()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, "seg-text");

        // Act — a text frame that is not MEDIA_START, then audio
        await client.SendAsync(Encoding.UTF8.GetBytes("MEDIA_XOFF"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var (stream, _) = await announced.WaitAsync(WaitLimit);
        await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var audio = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            audio.Length.Should().Be(640, "the audio that follows is delivered");
            stream.ChannelId.Should().Be("seg-text");
            ((IChanWebSocketSession)stream).MediaStart.Should().BeNull("no MEDIA_START has arrived");
            harness.Server.GetStream("seg-text").Should().BeSameAs(stream);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Debug && e.EventName == "ConnectionUnnamedByTextFirstFrame"
                    && e.Message.Contains("seg-text") && e.Message.Contains("MEDIA_XOFF"),
                "the Debug entry names the frame's event; logged [{0}]", logger.Describe());
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "nothing about it is a warning");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldLogAMediaStartWithoutChannelIdAsSuch_WhenItIsTheFirstFrame()
    {
        // Arrange — Asterisk never sends it: the captured MEDIA_START with its channel_id token taken out
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var captured = ExternalMediaPlain;
        var withoutChannelId = string.Join(' ', captured.Text.Split(' ').Where(t => !t.StartsWith("channel_id:", StringComparison.Ordinal)));
        withoutChannelId.Should().StartWith("MEDIA_START ").And.NotContain(captured.ChannelId);
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, "seg-no-id");

        // Act
        await client.SendAsync(Encoding.UTF8.GetBytes(withoutChannelId), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var (stream, _) = await announced.WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            stream.ChannelId.Should().Be("seg-no-id", "a MEDIA_START that names no call leaves the connection unnamed");
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Debug && e.EventName == "ConnectionUnnamedByMediaStartWithoutChannelId"
                    && e.Message.Contains("seg-no-id") && e.Message.Contains("MEDIA_START without a channel_id"),
                "the Debug entry says the MEDIA_START carries no channel_id; logged [{0}]", logger.Describe());
            logger.Entries.Should().NotContain(
                e => e.Message.Contains("not MEDIA_START"), "the frame is a MEDIA_START; logged [{0}]", logger.Describe());
        }
    }

    // ================================================================ the stream's format follows its first MEDIA_START

    public static TheoryData<string, ChanWebSocketCaptureFormat> EveryVersionInEachFormat()
    {
        var data = new TheoryData<string, ChanWebSocketCaptureFormat>();
        foreach (var version in ChanWebSocketWireCapture.Versions)
        {
            data.Add(version, ChanWebSocketCaptureFormat.PlainText);
            data.Add(version, ChanWebSocketCaptureFormat.Json);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryVersionInEachFormat))]
    public async Task Format_ShouldFollowTheNamingMediaStart_WhenTheDefaultFormatIsOfAnotherRate(string version, ChanWebSocketCaptureFormat format)
    {
        // Arrange — a server configured for ulaw (8000 Hz); the captured frame names slin16
        await using var harness = await Harness.StartAsync(defaultFormat: "ulaw");
        var frame = Capture(version, ChanWebSocketCaptureProducer.ExternalMedia, format);

        // Act
        using var client = await harness.ConnectNamedAsync(SharedSegment, frame);

        // Assert
        using (new AssertionScope(frame.Source))
        {
            var mediaStart = ((IChanWebSocketSession)client.Stream).MediaStart;
            mediaStart.Should().NotBeNull();
            client.Stream.Format.Should().Be("slin16", "the wire's format wins over the configured default");
            client.Stream.SampleRate.Should().Be(16000);
            mediaStart?.Format.Should().Be(client.Stream.Format);
            mediaStart?.Rate.Should().Be(client.Stream.SampleRate);
            client.Stream.ChannelId.Should().Be(frame.ChannelId);
        }
    }

    [Fact]
    public async Task Format_ShouldKeepTheDefault_WhenAnUnnamedConnectionSendsNoMediaStart()
    {
        // Arrange
        await using var harness = await Harness.StartAsync(defaultFormat: "ulaw");

        // Act — an audio frame first, and no MEDIA_START
        using var client = await harness.ConnectUnnamedAsync("seg-default");

        // Assert
        using (new AssertionScope())
        {
            client.Stream.Format.Should().Be("ulaw");
            client.Stream.SampleRate.Should().Be(8000);
            ((IChanWebSocketSession)client.Stream).MediaStart.Should().BeNull();
        }
    }

    // ================================================================ (k), (l), (m), (r): the tier rule

    [Fact]
    public async Task GetStream_ShouldReturnTheNamedConnection_WhenItNamesTheSegmentAnUnnamedHolderPresents()
    {
        // Arrange — an unnamed holder whose path segment equals a call's channel_id
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var frame = ExternalMediaPlain;
        using var unnamed = await harness.ConnectUnnamedAsync(frame.ChannelId);

        // Act — a connection on another path names that call
        using var named = await harness.ConnectNamedAsync(SharedSegment, frame);

        // Assert
        using (new AssertionScope())
        {
            harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(
                named.Stream, "a connection that names the call outranks one that only presents it as its path segment");
            harness.Server.ActiveStreamCount.Should().Be(2);
            harness.Server.ActiveStreams.Should().Contain(unnamed.Stream);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && e.EventName == "PathSegmentCollision" && e.Message.Contains(frame.ChannelId),
                "the collision is logged once; logged [{0}]", logger.Describe());
        }

        // Act — the named one ends: the unnamed one holds the key again
        await named.CloseAsync();
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(unnamed.Stream);
    }

    [Fact]
    public async Task GetStream_ShouldKeepTheNamedConnection_WhenAnUnnamedOneArrivesOnTheSegmentItHolds()
    {
        // Arrange — a call named first, on another path
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(logger: logger);
        var frame = DialPlain;
        using var named = await harness.ConnectNamedAsync(SharedSegment, frame);

        // Act — an unnamed connection on the path segment equal to that call's id
        using var unnamed = await harness.ConnectUnnamedAsync(frame.ChannelId);

        // Assert
        using (new AssertionScope())
        {
            harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(named.Stream);
            harness.Server.ActiveStreamCount.Should().Be(2);
            harness.Server.ActiveStreams.Should().Contain(unnamed.Stream);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && e.EventName == "PathSegmentCollision" && e.Message.Contains(frame.ChannelId),
                "the collision is logged once; logged [{0}]", logger.Describe());
        }

        await named.CloseAsync();
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(unnamed.Stream, "once nothing names the call, the segment holds it");
    }

    [Fact]
    public async Task GetStream_ShouldPassTheKeyToTheNextNamedConnectionBeforeTheUnnamedOne_WhenTheFirstNamedEnds()
    {
        // Arrange — an unnamed holder displaced from X, then two connections that both name X
        await using var harness = await Harness.StartAsync();
        var frame = DialJson;
        using var unnamed = await harness.ConnectUnnamedAsync(frame.ChannelId);
        using var firstNamed = await harness.ConnectNamedAsync(SharedSegment, frame);
        using var secondNamed = await harness.ConnectNamedAsync(SharedSegment, frame);
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(firstNamed.Stream);

        // Act and assert — the named tier first, in arrival order
        await firstNamed.CloseAsync();
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(
            secondNamed.Stream, "a connection that names X outranks the unnamed one, whatever their arrival order");

        await secondNamed.CloseAsync();
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(
            unnamed.Stream, "the unnamed one holds X only once no live connection names it");
    }

    [Fact]
    public async Task GetStream_ShouldReturnTheDisplacedHolder_WhenTheNamedConnectionThatDisplacedItEnds()
    {
        // Arrange — U1 holds X unnamed, U2 waits on X unnamed
        await using var harness = await Harness.StartAsync();
        var frame = ExternalMediaJson;
        using var u1 = await harness.ConnectUnnamedAsync(frame.ChannelId);
        using var u2 = await harness.ConnectUnnamedAsync(frame.ChannelId);

        // Act — N names X and takes it
        using var named = await harness.ConnectNamedAsync(SharedSegment, frame);
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(named.Stream, "the named connection takes X from U1");

        await named.CloseAsync();

        // Assert
        harness.Server.GetStream(frame.ChannelId).Should().BeSameAs(
            u1.Stream, "the displaced holder returns to the waiting list ahead of the other connections of its tier");
    }

    // ================================================================ (f): the 101 echo

    [Fact]
    public async Task Upgrade_ShouldSelectMedia_WhenTheClientOffersIt()
    {
        await using var harness = await Harness.StartAsync();

        using var client = await ConnectAsync(harness.Port, SharedSegment, "media");

        client.SubProtocol.Should().Be("media", "Asterisk's websocket_client offers media, and the 101 selects it");
    }

    [Theory]
    [InlineData("media", true)]
    [InlineData(null, false)]
    [InlineData("chat", false)]
    public async Task Upgrade_ShouldCarrySecWebSocketProtocolOnlyForAnOfferedMedia_WhenTheResponseBytesAreRead(string? offered, bool expected)
    {
        await using var harness = await Harness.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
        var network = tcp.GetStream();

        await network.WriteAsync(Encoding.ASCII.GetBytes(UpgradeRequest(SharedSegment, offered)));
        var response = await ReadResponseHeadAsync(network);

        using (new AssertionScope())
        {
            response.Should().StartWith("HTTP/1.1 101 Switching Protocols");
            if (expected)
                response.Should().Contain("\r\nSec-WebSocket-Protocol: media\r\n");
            else
                response.Should().NotContain("Sec-WebSocket-Protocol", "the 101 never selects a subprotocol that was not offered");
        }
    }

    [Fact]
    public async Task SendUpgradeResponseAsync_ShouldWriteTheSelectedSubprotocol_WhenOneIsGiven()
    {
        using var stream = new MemoryStream();

        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, "dGhlIHNhbXBsZSBub25jZQ==", "media", CancellationToken.None);

        Encoding.ASCII.GetString(stream.ToArray()).Should().Be(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\nSec-WebSocket-Protocol: media\r\n\r\n");
    }

    // ================================================================ (d), (e), (c): the bound

    [Fact]
    public async Task HandleConnection_ShouldAnnounceUnderThePathSegment_WhenNoFrameArrivesWithinMediaStartTimeout()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(time: time, logger: logger);
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, "seg-silent");
        var bound = TimerDueAsync(time, DefaultMediaStartTimeout);
        await Task.WhenAny(bound, announced).WaitAsync(WaitLimit);
        announced.IsCompleted.Should().BeFalse("a connection is announced only when its wait ends");

        // Act — the bound elapses
        time.Advance(DefaultMediaStartTimeout);
        var (stream, _) = await announced.WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            stream.ChannelId.Should().Be("seg-silent");
            harness.Server.GetStream("seg-silent").Should().BeSameAs(stream);
            harness.Server.ActiveStreamCount.Should().Be(1);
            harness.Server.ActiveStreams.Should().ContainSingle().Which.Should().BeSameAs(stream);
            foreach (var frame in ChanWebSocketWireCapture.MediaStart)
                harness.Server.GetStream(frame.ChannelId).Should().BeNull("an unnamed connection is found under no call's id");
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Debug && e.EventName == "ConnectionUnnamedByMediaStartTimeout" && e.Message.Contains("seg-silent"),
                "logged [{0}]", logger.Describe());
        }
    }

    [Fact]
    public async Task StopAsync_ShouldNotAnnounceAndShouldReturnThePlace_WhenAConnectionWaitsToBeNamed()
    {
        // Arrange — a connection in its wait; nothing elapses
        var time = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(time: time);
        var announced = harness.Announcements.NextAsync();
        using var client = await ConnectAsync(harness.Port, "seg-stop");
        var bound = TimerDueAsync(time, DefaultMediaStartTimeout);
        await Task.WhenAny(bound, announced).WaitAsync(WaitLimit);

        // Act
        await harness.Server.StopAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            harness.Announcements.Count.Should().Be(0, "the wait ends with the stop, and the connection is not announced");
            harness.Server.ActiveStreamCount.Should().Be(0);
            harness.Options.Admission.Held.Should().Be(0, "its place is returned");
        }
    }

    [Fact]
    public async Task AcceptLoop_ShouldAdmitExactlyTheLimit_WhenTenConnectionsWaitToBeNamed()
    {
        // Arrange — kept green: the admission the re-key must not lose. A limit of 2; no client sends a frame and
        // nothing elapses, so every admitted connection is still in its wait when the next one arrives
        var time = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(time: time, maxStreams: 2);

        // Act
        var admitted = new List<ClientWebSocket>();
        var refused = 0;
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var client = new ClientWebSocket();
                try
                {
                    await client.ConnectAsync(Endpoint(harness.Port, SharedSegment), CancellationToken.None).WaitAsync(WaitLimit);
                    admitted.Add(client);
                }
                catch (WebSocketException)
                {
                    refused++;
                    client.Dispose();
                }
            }

            // Assert
            using (new AssertionScope())
            {
                admitted.Should().HaveCount(2, "the limit is taken at the accept and held through the wait");
                refused.Should().Be(8);
                harness.Options.Admission.Held.Should().Be(2);
                harness.Server.ActiveStreamCount.Should().Be(0, "every admitted connection is still waiting to be named");
                harness.Announcements.Count.Should().Be(0, "a connection waiting to be named is not announced");
            }
        }
        finally
        {
            DisposeAll(admitted);
        }
    }

    private static void DisposeAll(List<ClientWebSocket> clients)
    {
        foreach (var client in clients)
            client.Dispose();
    }

    // ================================================================ (p): a bound that cannot be honoured

    [Theory]
    [InlineData("negative")]
    [InlineData("infinite")]
    public async Task StartAsync_ShouldRefuseAndBindNothing_WhenMediaStartTimeoutCannotBeHonoured(string which)
    {
        var port = FreePort();
        var server = new WebSocketAudioServer(
            new AudioServerOptions
            {
                ListenAddress = "127.0.0.1",
                WebSocketPort = port,
                MediaStartTimeout = which == "negative" ? TimeSpan.FromSeconds(-1) : Timeout.InfiniteTimeSpan,
            },
            NullLogger<WebSocketAudioServer>.Instance);
        await using var _ = server;

        var start = async () => await server.StartAsync();

        (await start.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .Which.ParamName.Should().Be(nameof(AudioServerOptions.MediaStartTimeout));
        server.IsRunning.Should().BeFalse();
        using var probe = new TcpClient();
        var connect = async () => await probe.ConnectAsync(IPAddress.Loopback, port);
        await connect.Should().ThrowAsync<SocketException>("a refused start binds nothing");
    }

    [Fact]
    public async Task StartAsync_ShouldStart_WhenMediaStartTimeoutIsZero()
    {
        await using var harness = await Harness.StartAsync(mediaStartTimeout: TimeSpan.Zero);

        harness.Server.IsRunning.Should().BeTrue("zero is no wait, not a bound that cannot be honoured");
    }

    // ================================================================ (q): each reason is logged

    [Fact]
    public async Task HandleConnection_ShouldLogOneDebugEntryNamingItsReason_ForEachConnectionAnnouncedUnnamed()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(time: time, logger: logger);

        // Act — a binary first frame
        var announced = harness.Announcements.NextAsync();
        using var binary = await ConnectAsync(harness.Port, "q-binary");
        await binary.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        await announced.WaitAsync(WaitLimit);

        // a text first frame that is not MEDIA_START
        announced = harness.Announcements.NextAsync();
        using var text = await ConnectAsync(harness.Port, "q-text");
        await text.SendAsync(Encoding.UTF8.GetBytes("MEDIA_XOFF"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await announced.WaitAsync(WaitLimit);

        // nothing, until the bound elapses. A connection whose first frame arrived before its wait began armed no
        // timer, so the first two may have armed one each or none: their waits are over, so every timer they armed
        // is already published, and draining them leaves the next one as this connection's
        DrainTimers(time);
        announced = harness.Announcements.NextAsync();
        var bound = TimerDueAsync(time, DefaultMediaStartTimeout);
        using var silent = await ConnectAsync(harness.Port, "q-bound");
        await Task.WhenAny(bound, announced).WaitAsync(WaitLimit);
        time.Advance(DefaultMediaStartTimeout);
        await announced.WaitAsync(WaitLimit);

        // Assert
        var entries = logger.Entries;
        using (new AssertionScope())
        {
            entries.Should().ContainSingle(e => e.Message.Contains("q-binary") && e.EventName == "ConnectionUnnamedByBinaryFirstFrame" && e.Level == LogLevel.Debug,
                "logged [{0}]", logger.Describe());
            entries.Should().ContainSingle(e => e.Message.Contains("q-text") && e.EventName == "ConnectionUnnamedByTextFirstFrame" && e.Message.Contains("MEDIA_XOFF") && e.Level == LogLevel.Debug,
                "logged [{0}]", logger.Describe());
            entries.Should().ContainSingle(e => e.Message.Contains("q-bound") && e.EventName == "ConnectionUnnamedByMediaStartTimeout" && e.Level == LogLevel.Debug,
                "logged [{0}]", logger.Describe());
            entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "an unnamed connection is no warning");
        }
    }

    // ================================================================ (s): the peer ends the connection during the wait

    [Fact]
    public async Task HandleConnection_ShouldNeitherAnnounceNorHoldThePlace_WhenThePeerClosesDuringTheWait()
    {
        // Arrange — a limit of one, a clock that never moves
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(time: time, logger: logger, maxStreams: 1);
        var waiting = TimerDueAsync(time, DefaultMediaStartTimeout);

        // Act — the peer upgrades and closes before any frame
        using (var closing = await ConnectAsync(harness.Port, "s-closed"))
        {
            await Task.WhenAny(waiting, harness.Announcements.FirstAnnounced).WaitAsync(WaitLimit);
            harness.Announcements.Count.Should().Be(0, "the connection is in its wait, so it is not announced yet");
            await closing.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        await TheNextConnectionTakesThePlaceAsync(harness, logger, "s-closed");
    }

    [Fact]
    public async Task HandleConnection_ShouldNeitherAnnounceNorHoldThePlace_WhenThePeerResetsDuringTheWait()
    {
        // Arrange — a limit of one, a clock that never moves. A ClientWebSocket cannot force a reset, so the peer
        // writes its upgrade by hand and closes with a zero linger
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        await using var harness = await Harness.StartAsync(time: time, logger: logger, maxStreams: 1);
        var waiting = TimerDueAsync(time, DefaultMediaStartTimeout);

        // Act
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
            var network = tcp.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes(UpgradeRequest("s-reset", "media")));
            (await ReadResponseHeadAsync(network)).Should().StartWith("HTTP/1.1 101");
            await Task.WhenAny(waiting, harness.Announcements.FirstAnnounced).WaitAsync(WaitLimit);
            harness.Announcements.Count.Should().Be(0, "the connection is in its wait, so it is not announced yet");
            tcp.LingerState = new LingerOption(true, 0);
            tcp.Close();
        }

        await TheNextConnectionTakesThePlaceAsync(harness, logger, "s-reset");
    }

    private static async Task TheNextConnectionTakesThePlaceAsync(Harness harness, RecordingLogger logger, string endedSegment)
    {
        // A second connection is admitted only once the first one's place is back (limit 1), and with nothing
        // advanced, so the place came back from the ending, not from the bound
        var frame = DialPlain;
        using var giveUp = new CancellationTokenSource(WaitLimit);
        ClientWebSocket second;
        while (true)
        {
            var attempt = new ClientWebSocket();
            try
            {
                await attempt.ConnectAsync(Endpoint(harness.Port, SharedSegment), giveUp.Token);
                second = attempt;
                break;
            }
            catch (WebSocketException)
            {
                attempt.Dispose();
            }
        }

        using (second)
        {
            await SendTextAsync(second, frame);

            // Read announcements until this connection's, which names the call; a server that announced the ended
            // connection too has put that one first, and the second read then ends the loop.
            for (var read = 0; read < 2; read++)
            {
                var (announced, _) = await harness.Announcements.NextAsync().WaitAsync(WaitLimit);
                if (announced.ChannelId == frame.ChannelId)
                    break;
            }

            using (new AssertionScope())
            {
                harness.Announcements.Count.Should().Be(1, "the connection that ended during its wait is never announced");
                harness.Announcements.All.Should().ContainSingle().Which.ChannelId.Should().Be(frame.ChannelId);
                harness.Server.GetStream(endedSegment).Should().BeNull();
                harness.Server.ActiveStreamCount.Should().Be(1);
                harness.Server.ActiveStreams.Should().NotContain(s => s.ChannelId == endedSegment);
                logger.Entries.Should().NotContain(
                    e => e.EventName != null && e.EventName.StartsWith("ConnectionUnnamed", StringComparison.Ordinal),
                    "a connection that ended is not reported as announced unnamed; logged [{0}]", logger.Describe());
            }
        }
    }

    // ================================================================ Zero, and the default

    [Fact]
    public async Task HandleConnection_ShouldAnnounceAtOnceUnderThePathSegmentAndArmNoMediaStartTimer_WhenMediaStartTimeoutIsZero()
    {
        // Arrange
        var time = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(time: time, mediaStartTimeout: TimeSpan.Zero);
        var announced = harness.Announcements.NextAsync();

        // Act
        using var client = await ConnectAsync(harness.Port, "seg-zero");
        var (stream, _) = await announced.WaitAsync(WaitLimit);
        var timers = DrainTimers(time);

        // Assert — the upgrade's IdleTimeout timer, armed for every connection, and nothing else
        using (new AssertionScope())
        {
            timers.Should().ContainSingle("only the upgrade's IdleTimeout timer is armed; timers due at [{0}]",
                string.Join(", ", timers.Select(t => t.DueTime)))
                .Which.DueTime.Should().Be(IdleTimeout);
            stream.ChannelId.Should().Be("seg-zero");
        }

        // Act — a MEDIA_START afterwards, then audio, read so the text frame has been handled
        var frame = ExternalMediaPlain;
        await SendTextAsync(client, frame);
        await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        (await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit)).Length.Should().Be(640);

        // Assert — zero never re-keys, and the MEDIA_START is state
        using (new AssertionScope())
        {
            harness.Server.GetStream(frame.ChannelId).Should().BeNull();
            stream.ChannelId.Should().Be("seg-zero");
            harness.Server.GetStream("seg-zero").Should().BeSameAs(stream);
            var mediaStart = ((IChanWebSocketSession)stream).MediaStart;
            mediaStart.Should().NotBeNull("a MEDIA_START after a zero-bound announcement fills MediaStart");
            mediaStart?.ChannelId.Should().Be(frame.ChannelId);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldArmASecondTimerAtMediaStartTimeout_WhenItIsPositive()
    {
        // The negative control of the test above: the same read finds the wait's timer when there is a wait
        var time = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(time: time);
        var announced = harness.Announcements.NextAsync();

        using var client = await ConnectAsync(harness.Port, "seg-positive");
        var timers = new List<FakeTimeProvider.FakeTimer>();
        while (!timers.Any(t => t.DueTime == DefaultMediaStartTimeout))
            timers.Add(await time.TimersCreated.ReadAsync().AsTask().WaitAsync(WaitLimit));

        timers.Select(t => t.DueTime).Should().Equal(IdleTimeout, DefaultMediaStartTimeout);
        announced.IsCompleted.Should().BeFalse();

        await SendTextAsync(client, DialPlain);
        (await announced.WaitAsync(WaitLimit)).Stream.ChannelId.Should().Be(DialPlain.ChannelId);
    }

    [Fact]
    public void MediaStartTimeout_ShouldDefaultToTwoSeconds()
    {
        new AudioServerOptions().MediaStartTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    // ================================================================ helpers

    private static ChanWebSocketCapturedFrame Capture(
        string version, ChanWebSocketCaptureProducer producer, ChanWebSocketCaptureFormat format, string option = "none") =>
        ChanWebSocketWireCapture.MediaStart.First(
            f => f.AsteriskVersion == version && f.Producer == producer && f.Format == format && f.Option == option);

    private static Uri Endpoint(int port, string segment) => new($"ws://127.0.0.1:{port}/media/{segment}");

    private static async Task<ClientWebSocket> ConnectAsync(int port, string segment, string? offered = null)
    {
        var client = new ClientWebSocket();
        if (offered is not null)
            client.Options.AddSubProtocol(offered);
        await client.ConnectAsync(Endpoint(port, segment), CancellationToken.None).WaitAsync(WaitLimit);
        return client;
    }

    private static Task SendTextAsync(ClientWebSocket client, ChanWebSocketCapturedFrame frame) =>
        client.SendAsync(frame.Payload(), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static string UpgradeRequest(string segment, string? offered) =>
        $"GET /media/{segment} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
        (offered is null ? "" : $"Sec-WebSocket-Protocol: {offered}\r\n") + "\r\n";

    private static async Task<string> ReadResponseHeadAsync(NetworkStream network)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await network.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(WaitLimit);
            if (read == 0)
                return Encoding.ASCII.GetString(buffer, 0, total);
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>The first timer created on <paramref name="time"/> due at <paramref name="due"/>, after skipping <paramref name="skip"/> of them.</summary>
    private static async Task TimerDueAsync(FakeTimeProvider time, TimeSpan due, int skip = 0)
    {
        var seen = 0;
        while (true)
        {
            var timer = await time.TimersCreated.ReadAsync().AsTask().WaitAsync(WaitLimit);
            if (timer.DueTime == due && seen++ >= skip)
                return;
        }
    }

    private static List<FakeTimeProvider.FakeTimer> DrainTimers(FakeTimeProvider time)
    {
        var timers = new List<FakeTimeProvider.FakeTimer>();
        while (time.TimersCreated.TryRead(out var timer))
            timers.Add(timer);
        return timers;
    }

    private static Task WhenDisposed(IAudioStream stream)
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.StateChanges.Subscribe(static _ => { }, () => disposed.TrySetResult());
        return disposed.Task;
    }

    /// <summary>A started server, its options and its announcements.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(WebSocketAudioServer server, int port, AudioServerOptions options)
        {
            Server = server;
            Port = port;
            Options = options;
            Announcements = new Announced(server);
        }

        public WebSocketAudioServer Server { get; }

        public int Port { get; }

        public AudioServerOptions Options { get; }

        public Announced Announcements { get; }

        public static async Task<Harness> StartAsync(
            TimeProvider? time = null,
            ILogger<WebSocketAudioServer>? logger = null,
            int maxStreams = 1000,
            TimeSpan? mediaStartTimeout = null,
            string? defaultFormat = null)
        {
            AudioServerOptions? options = null;
            var (server, port) = await LoopbackServerBind.StartAsync(
                p =>
                {
                    options = new AudioServerOptions
                    {
                        ListenAddress = "127.0.0.1",
                        WebSocketPort = p,
                        MaxConcurrentStreams = maxStreams,
                        IdleTimeout = IdleTimeout,
                    };
                    if (mediaStartTimeout is { } value)
                        options.MediaStartTimeout = value;
                    if (defaultFormat is not null)
                        options.DefaultFormat = defaultFormat;
                    return new WebSocketAudioServer(options, logger ?? NullLogger<WebSocketAudioServer>.Instance, time ?? TimeProvider.System);
                },
                s => s.StartAsync());
            return new Harness(server, port, options!);
        }

        /// <summary>Connects, sends <paramref name="frame"/> as the first frame, then waits for this connection's announcement.</summary>
        public async Task<AnnouncedClient> ConnectNamedAsync(string segment, ChanWebSocketCapturedFrame frame, string? offered = null)
        {
            var announced = Announcements.NextAsync();
            var client = await ConnectAsync(Port, segment, offered);
            await SendTextAsync(client, frame);
            var (stream, disposed) = await announced.WaitAsync(WaitLimit);
            return new AnnouncedClient(client, stream, disposed);
        }

        /// <summary>Connects and sends an audio frame first, so the connection is announced unnamed at once.</summary>
        public async Task<AnnouncedClient> ConnectUnnamedAsync(string segment)
        {
            var announced = Announcements.NextAsync();
            var client = await ConnectAsync(Port, segment);
            await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            var (stream, disposed) = await announced.WaitAsync(WaitLimit);
            stream.ChannelId.Should().Be(segment, "a binary first frame leaves the connection under its path segment");
            return new AnnouncedClient(client, stream, disposed);
        }

        public async ValueTask DisposeAsync()
        {
            Announcements.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>Every announcement, in order, each with its disposal sentinel subscribed inside the emission.</summary>
    private sealed class Announced : IDisposable
    {
        private readonly Channel<(IAudioStream Stream, Task Disposed)> _channel = Channel.CreateUnbounded<(IAudioStream Stream, Task Disposed)>();
        private readonly ConcurrentQueue<IAudioStream> _all = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        public Announced(WebSocketAudioServer server) =>
            _subscription = server.OnStreamConnected.Subscribe(stream =>
            {
                _all.Enqueue(stream);
                _channel.Writer.TryWrite((stream, WhenDisposed(stream)));
                _first.TrySetResult();
            });

        /// <summary>Completes at the first announcement, without consuming it from <see cref="NextAsync"/>.</summary>
        public Task FirstAnnounced => _first.Task;

        public int Count => _all.Count;

        public IReadOnlyCollection<IAudioStream> All => _all.ToArray();

        public Task<(IAudioStream Stream, Task Disposed)> NextAsync() => _channel.Reader.ReadAsync().AsTask();

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class AnnouncedClient(ClientWebSocket client, IAudioStream stream, Task disposed) : IDisposable
    {
        public ClientWebSocket Client { get; } = client;

        public IAudioStream Stream { get; } = stream;

        public async Task CloseAsync()
        {
            await Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await disposed.WaitAsync(WaitLimit);
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed record LogEntry(LogLevel Level, string? EventName, string Message);

    /// <summary>Records every entry the server and its sessions log, with the formatted message.</summary>
    private sealed class RecordingLogger : ILogger<WebSocketAudioServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, formatter(state, exception)));

        public string Describe() =>
            string.Join("; ", Entries.Select(e => $"{e.Level}:{e.EventName}:{e.Message}"));
    }
}
