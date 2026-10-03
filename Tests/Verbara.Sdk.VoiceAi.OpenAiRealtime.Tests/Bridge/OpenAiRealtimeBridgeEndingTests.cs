using System.Globalization;
using System.Net.WebSockets;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// How a Realtime session ends when one of its two far ends ends it: the caller, through the
/// AudioSocket session, or the vendor, through the WebSocket. Every test here runs on a session token
/// that is never cancelled, so the ending under test is the only one there is.
/// </summary>
/// <remarks>
/// <para>
/// The vendor never closes a healthy session on its own. Measured live on 2026-09-27, the bridge was
/// still waiting on it sixty seconds after the caller hung up, with no completion counted and no
/// duration recorded; when the bridge closed first, the vendor answered with <c>1000</c> about 1.1 s
/// later. The fake's <see cref="RealtimeFakeServer.HoldOpenUntilClientCloses"/> is that vendor, and
/// <see cref="RealtimeFakeServer.HoldOpenUntilDisposed"/> is one that never answers.
/// </para>
/// <para>
/// No test here waits on a clock. Each waits on the signal it asserts, bounded by
/// <see cref="SignalTimeout"/>, whose expiry is a failure.
/// </para>
/// <para>
/// Once the bridge has sent its close, it waits for the vendor's answer for at most ten seconds. The
/// tests of that bound run it on a <see cref="FakeTimeProvider"/> and never on the real clock: the
/// bound equals <see cref="SignalTimeout"/>, so a test that let it run would race its own safety net.
/// Such a test hangs the caller up, waits for the bridge's close to reach the fake and then for the
/// clock to report the bound armed, and only then moves the clock. The bound's timer is created with
/// the session, with no due time, so its creation is not the arm. A function call the vendor requested
/// holds the bound while it runs, so when one is running at the hangup, the arm comes at its return.
/// The bridge's connect bound runs on the same clock and arms first, when the dial starts; it is spent
/// once the session opens, so a test passes over its arm before it reads the close bound's.
/// </para>
/// </remarks>
public sealed class OpenAiRealtimeBridgeEndingTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An event the fake delivers so that the bridge publishes something once its output loop runs.
    /// Never asserted on; see <c>OpenAiRealtimeBridgeTests.LoopsRunningMarkerEvent</c>.
    /// </summary>
    private const string LoopsRunningMarkerEvent = """{"type":"input_audio_buffer.speech_started"}""";

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    /// <summary>The message of <see cref="RefusalFrame"/>.</summary>
    private const string RefusalMessage = "The session was refused.";

    /// <summary>The error frame a refusing vendor sends before its close.</summary>
    private const string RefusalFrame =
        """{"type":"error","error":{"type":"invalid_request_error","message":"The session was refused."}}""";

    /// <summary>
    /// How long the bridge waits for the vendor to answer its close: ten seconds, counted from the
    /// close. It is a private constant of the bridge, not an option, so the tests state it here.
    /// </summary>
    private static readonly TimeSpan CloseAnswerBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the bridge's connect may take: five seconds, the first bound it arms on its clock. It
    /// is a private value of the bridge, not an option, so the tests state it here.
    /// </summary>
    private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a function call may hold the session: the default <c>FunctionCallTimeout</c>, 30 seconds. Its arm
    /// comes when the call starts.
    /// </summary>
    private static readonly TimeSpan FunctionCallBound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HandleSessionAsync_ShouldCloseTheVendorSessionAndComplete_WhenTheCallerHangsUpOnAHealthySession()
    {
        // Arrange: a vendor like the live one, which sends nothing more after its burst and closes
        // only in answer to a close from the client.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = CreateBridge(fakeOpenAi);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up once both loops run, and nothing more reaches the bridge
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();

            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            var clientClose = await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            using (new AssertionScope())
            {
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                clientClose.Should().Be(
                    WebSocketCloseStatus.NormalClosure,
                    "the bridge closes toward the vendor with a normal closure when the caller hangs up");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "a session the caller ended and the vendor closed in answer is a completion");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(
                    0, "the session ended, so its duration is recorded");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldFailWithTheTransportError_WhenTheVendorDropsTheConnectionInAnswerToTheClose()
    {
        // Arrange: a vendor that holds the socket and never answers a close, so the test chooses what
        // happens once the bridge's close arrives. Abort needs this hold: without it the fake has
        // already closed by the time the test could drop the connection.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = CreateBridge(fakeOpenAi);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up once both loops run; the vendor drops the TCP connection, with
            // no close frame, the moment the bridge's close reaches it
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();

            await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            fakeOpenAi.Abort();

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                fault.Should().BeOfType<WebSocketException>(
                    "a transport that dies is not a clean ending, even under a close the bridge started");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "a connection dropped instead of closed is what this counter is for");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    0, "the vendor never answered the close, so nothing completed the handshake");
            }

            ((WebSocketException)fault!).WebSocketErrorCode.Should().Be(
                WebSocketError.ConnectionClosedPrematurely,
                "the socket read end-of-stream where the vendor's answering close should have been");
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A vendor that never answers the bridge's close. The bound ends the wait ten seconds after the
    /// close, and the session completes: the caller ended it. What the bound cost is the vendor's close
    /// code, so the session is also counted once in <c>sessions.close_unanswered</c> and a warning names
    /// the bound. Measured on the wall clock before the manual clock existed: <c>S1-never-audio</c>,
    /// 10,006 ms.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCompleteAndCountTheUnansweredClose_WhenTheVendorNeverAnswersTheClose()
    {
        // Arrange: a vendor that holds the socket and never answers a close
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up once both loops run; the whole bound passes on the clock
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            clock.Advance(CloseAnswerBound);
            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "the caller ended the session; the vendor's silence does not make it a failure");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    1, "the bound ended the wait, and every such session is counted once");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "CloseUnanswered"
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("10000 ms", StringComparison.Ordinal),
                    "the warning names the bound that ended the session");
                metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(
                    0, "the session ended, so its duration is recorded");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A vendor that keeps talking after the bridge's close and never answers it. The bound is counted
    /// from the close and is not restarted by a vendor message, so five frames two seconds apart do not
    /// extend it: the session ends ten seconds after the close, two seconds after the last frame. A
    /// bound restarted per message held the call 65 to 70 s on the wall clock while the vendor kept
    /// talking; the bound from the close ended it at 10,005 ms, counted (<c>T5</c>).
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldNotExtendTheBound_WhenTheVendorKeepsSendingAfterTheClose()
    {
        // Arrange: a vendor that holds after the client's close and answers only when asked, which
        // this test never does. Every frame goes through SendEventAsync, so EventsToSend stays empty.
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fakeOpenAi.SendEventAsync(LoopsRunningMarkerEvent);
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);

            // Act: the caller hangs up; then, five times, the vendor sends a frame, the bridge
            // forwards it, and two seconds pass
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            for (var frame = 1; frame <= 5; frame++)
            {
                var text = $"frame {frame}";
                using var forwarded = new RealtimeEventCollector(
                    bridge.Events,
                    e => e.OfType<RealtimeTranscriptEvent>().Any(t => t.Transcript == text));
                await fakeOpenAi.SendEventAsync(
                    $$"""{"type":"response.output_audio_transcript.delta","delta":"{{text}}"}""");
                await forwarded.Satisfied.WaitAsync(SignalTimeout);
                clock.Advance(TimeSpan.FromSeconds(2));
            }

            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "the caller ended the session; a vendor that talks on does not make it a failure");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    1, "the bound ended the wait although the last frame came two seconds before it ran out");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "CloseUnanswered"
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("10000 ms", StringComparison.Ordinal),
                    "the warning names the bound that ended the session");
                metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(
                    0, "the session ended, so its duration is recorded");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The bound running out in the instant a vendor frame arrives. Cancelling the bound's token aborts
    /// the socket under the read in flight, but a read that already holds the frame still returns it.
    /// The bridge forwards that frame, and its loop then ends on the socket's state, not through the
    /// cancellation's catch. That ending is the bound's too, so it is reported the same way: once in
    /// <c>sessions.close_unanswered</c> and with the warning. Measured on the wall clock before the
    /// manual clock existed (<c>T5</c>): with the loop's end reporting nothing, 6 and 7 of 10 runs in two
    /// trees ended with neither the count nor the warning; with it reporting the bound, 10 of 10 counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instant is made, not waited for: <see cref="WebSocketReceiveHook"/> moves the clock inside the
    /// bridge's read, after the read has the frame and before it returns it. Two frames written back to
    /// back, with the clock moved from the bridge's event for the first, do not reach this ending: the
    /// next read then starts on a token that is already cancelled and throws before it looks at the
    /// frame waiting for it, so the session ends through the catch. Measured: 20 of 20 such sessions
    /// counted with the loop's end reporting nothing, so that trigger cannot tell the fix from its
    /// absence. Without the hook, one frame sent and the clock moved at once left 6, 9, 15 and 18 of 20
    /// sessions unreported in four runs, and 0 of 20 once the loop's end reports the bound: the race is
    /// the bridge's, not the hook's.
    /// </para>
    /// <para>
    /// The hook listens to the runtime's private WebSocket diagnostics, which may change in any release.
    /// If they do, this test fails rather than passing without reaching the race: the hook's signal
    /// never comes, the frame is not forwarded, or the bridge reads again after it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HandleSessionAsync_ShouldReportTheBound_WhenItRunsOutAsAVendorFrameArrives()
    {
        // Arrange: a vendor that holds after the client's close and answers only when asked, which
        // this test never does. Every frame goes through SendEventAsync, so EventsToSend stays empty.
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            const string lateFrame = "a late frame";
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var receive = new WebSocketReceiveHook();
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());
            using var forwarded = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeTranscriptEvent>().Any(t => t.Transcript == lateFrame));

            var sessionTask = receive.Watch(
                () => bridge.HandleSessionAsync(session, CancellationToken.None).AsTask());
            await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fakeOpenAi.SendEventAsync(LoopsRunningMarkerEvent);
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            // Act: the vendor sends one more frame, and the whole bound passes inside the read that
            // returns it. The bridge has read every frame sent before this one: session.created went
            // out ahead of the marker, and the marker is published.
            receive.Arm(() => clock.Advance(CloseAnswerBound));
            await fakeOpenAi.SendEventAsync(
                $$"""{"type":"response.output_audio_transcript.delta","delta":"{{lateFrame}}"}""");
            await receive.Fired.WaitAsync(SignalTimeout);

            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                forwarded.Events.OfType<RealtimeTranscriptEvent>().Should().Contain(
                    t => t.Transcript == lateFrame,
                    "the read already held the frame when the bound ran out, so it returned it");
                receive.ReceivesStartedAfterwards.Should().Be(
                    0,
                    "the bound aborted the socket under that read, so the loop ended on the socket's state "
                    + "without reading again: the ending under test, not the cancellation's catch");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "the caller ended the session; a frame at the bound does not make it a failure");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    1, "the bound ended the wait, whichever way the loop left, and every such session is counted once");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "CloseUnanswered"
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("10000 ms", StringComparison.Ordinal),
                    "the warning names the bound that ended the session");
                metrics.GetDouble("openai_realtime.session.duration_ms").Should().BeGreaterThan(
                    0, "the session ended, so its duration is recorded");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The same instant one level down: the bound runs out inside the read that returns only the
    /// <em>first part</em> of a message, a single frame larger than the loop's 64 KiB buffer or the
    /// first fragment of a fragmented one. That read succeeds with the socket already aborted under it.
    /// Reading the rest threw <see cref="WebSocketException"/> (<c>InvalidState</c>), which no catch
    /// took, and the session the caller ended was counted as failed: measured 50 of 50 with the hook,
    /// and, with no hook, in 3.7–12.7 % of the sessions whose close went unanswered while frames over
    /// 64 KiB or fragmented messages streamed back to back, depending on the harness.
    /// </summary>
    /// <remarks>
    /// A vendor that merely stops mid-message does not reach this ending: the loop is then waiting in a
    /// read, and the bound ends it through the cancellation's catch (50 of 50 counted, measured). Only a
    /// read that returns a part at the bound's instant does, which is why the hook makes the instant.
    /// </remarks>
    [Theory]
    [InlineData("one 70,000-character frame")]
    [InlineData("the first of two fragments")]
    public async Task HandleSessionAsync_ShouldReportTheBound_WhenItRunsOutInsideAReadThatReturnsPartOfAMessage(string shape)
    {
        // Arrange: a vendor that holds after the client's close and never answers it
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var receive = new WebSocketReceiveHook();
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            var sessionTask = receive.Watch(
                () => bridge.HandleSessionAsync(session, CancellationToken.None).AsTask());
            await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fakeOpenAi.SendEventAsync(LoopsRunningMarkerEvent);
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            // Act: the whole bound passes inside the read that returns the message's first part
            receive.Arm(() => clock.Advance(CloseAnswerBound));
            await SendPartOfAMessageAsync(fakeOpenAi, shape);
            await receive.Fired.WaitAsync(SignalTimeout);
            var ending = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                ending.Should().BeNull("the caller ended the session; the bound aborting a read does not make it a failure");
                receive.ReceivesStartedAfterwards.Should().Be(
                    0, "the socket is aborted, so the loop must not read the rest of the message");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the caller ended the session");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    1, "the bound ended the wait, whichever read it ran out in");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "CloseUnanswered" && e.Level == LogLevel.Warning,
                    "the warning is the only record of what the bound cost");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The host's cancellation in that same instant. The session token reaches the read through the
    /// bound's token, so it aborts the socket the same way, and the rest of the message threw
    /// <see cref="WebSocketException"/> too: a requested cancellation was counted as a failure (50 of 50
    /// with the hook). A requested cancellation is a completion (see
    /// <see cref="HandleSessionAsync_ShouldNotCountTheBound_WhenTheHostCancelsAfterTheHangup"/>).
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCompleteWithoutCountingTheBound_WhenTheHostCancelsInsideAReadThatReturnsPartOfAMessage()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var receive = new WebSocketReceiveHook();
            using var host = new CancellationTokenSource();
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            var sessionTask = receive.Watch(() => bridge.HandleSessionAsync(session, host.Token).AsTask());
            await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await fakeOpenAi.SendEventAsync(LoopsRunningMarkerEvent);
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            // Act: the host cancels inside the read that returns the first 64 KiB of a 70,000-character frame
            receive.Arm(host.Cancel);
            await SendPartOfAMessageAsync(fakeOpenAi, "one 70,000-character frame");
            await receive.Fired.WaitAsync(SignalTimeout);
            var ending = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                ending.Should().BeNull("a requested cancellation is a completion");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the host ended the session");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the host's cancellation is not the bound running out");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().NotContain(e => e.EventId.Name == "CloseUnanswered");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The first part of a message, in one of the two shapes that make a read return only part.</summary>
    private static Task SendPartOfAMessageAsync(RealtimeFakeServer fakeOpenAi, string shape) => shape switch
    {
        // 70,000 characters in one frame: the first read fills the 64 KiB buffer and returns the rest unread.
        "one 70,000-character frame" => fakeOpenAi.SendEventAsync(
            $$"""{"type":"response.output_audio_transcript.delta","delta":"{{new string('x', 70_000)}}"}"""),
        // A message in two frames: the first read returns the first frame alone.
        "the first of two fragments" => fakeOpenAi.SendFragmentsAsync(
            ["""{"type":"response.output_audio_transcript.delta","delta":"ab""", """cd"}"""]),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    /// <summary>
    /// A control: a vendor that answers the close with <c>1000</c> just inside the bound. The session
    /// ends at the answer as an ordinary completion, with no warning and nothing counted as unanswered.
    /// A 5 s bound falsely warned here (measured at 5.0, 7.0 and 9.9 s); the 10 s bound kept the answer
    /// at 9,906 ms (<c>P1b-answer-9900</c>).
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCompleteWithoutWarning_WhenTheVendorAnswersWithinTheBound()
    {
        // Arrange: a vendor that answers the client's close only when the test says so
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up; 9.9 s pass; then the vendor answers
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            clock.Advance(TimeSpan.FromMilliseconds(9_900));
            // Recorded rather than thrown: if the bound has already ended the session, the connection
            // may be gone, and the assertions below say why better than a failed send does.
            var answerFault = await Record.ExceptionAsync(
                () => fakeOpenAi.AnswerClientCloseAsync(WebSocketCloseStatus.NormalClosure, ""));
            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                answerFault.Should().BeNull("the vendor answers on a connection the bridge still holds");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "a normal answer to the caller's ending is a completion");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the vendor answered the close");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the answer came within the bound");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered", "an answered close is nothing to warn about");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A control: the host cancels the session token while the bridge waits for the vendor's answer.
    /// A requested cancellation is a completion, as it always was, and it is not the bound running out,
    /// so nothing is counted as unanswered and nothing warns (<c>P1b-cancel-7000</c>).
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldNotCountTheBound_WhenTheHostCancelsAfterTheHangup()
    {
        // Arrange: a vendor that never answers a close
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());
            using var host = new CancellationTokenSource();

            // Act: the caller hangs up; 7 s into the bound, the host cancels the session
            var sessionTask = bridge.HandleSessionAsync(session, host.Token).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            clock.Advance(TimeSpan.FromSeconds(7));
            await host.CancelAsync();
            await sessionTask.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "a requested cancellation is a completion");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the host ended the wait");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the host's cancellation is not the bound running out");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered", "the bound did not end this session");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A function call still running when the caller hangs up, with a vendor that answers the close
    /// with <c>1000</c> at once. The bridge does not read while the function runs, so the answer waits
    /// in the socket, and the bound is held for as long as the function takes: fifteen seconds pass on
    /// the clock and the session is not counted. The function runs to its end on the session token. Its
    /// result is not sent, because the bridge's close is already out; the call is still published. The
    /// session then reads the answer and completes. Measured on the wall clock before the manual clock
    /// existed: <c>Q4-fn15000-honor-answer1100</c>, 14,505 ms, no count. With the result sent, the
    /// session failed with <see cref="WebSocketException"/> <c>InvalidState</c>
    /// (<c>Q4-H2-nobound</c>).
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCompleteAndPublishTheCall_WhenAFunctionOutlastsTheBoundAndTheVendorAnswers()
    {
        // Arrange: a vendor that asks for one function call and then answers the client's close only
        // when the test says so; a function that runs until the test releases it
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.EventsToSend.Add(HeldFunction.CallEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            var function = new HeldFunction();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log, function);
            using var metrics = new MeterCapture(MeterName);
            using var calls = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeFunctionCalledEvent>().Any());

            // Act: the caller hangs up while the function runs; the vendor answers the close at once;
            // fifteen seconds pass; then the function returns
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await function.Started.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();
            await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);

            await fakeOpenAi.AnswerClientCloseAsync(WebSocketCloseStatus.NormalClosure, "");
            clock.Advance(TimeSpan.FromSeconds(15));
            function.Release();

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                fault.Should().BeNull("a function's result that can no longer be sent does not fail the session");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the vendor answered the close");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the bound is held while the function runs, and the answer was waiting when it returned");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered", "the vendor answered the close");
                AssertTheCallRanAndItsResultWasNotSent(fakeOpenAi, metrics, log, calls);
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A function call still running when the caller hangs up, with a vendor that never answers the
    /// close. The close goes out while the function runs, and does not start the bound; the function's
    /// return does, with the full ten seconds. When they pass, the session completes and is counted,
    /// and the call is published although its result was not sent. Measured on the wall clock before
    /// the manual clock existed: <c>Q4-fn3000-honor-never</c>, 12,512 ms, counted. A bound that was
    /// never resumed after the function left the session running until the host cancelled it.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldRestartTheFullBound_WhenAFunctionReturnsAfterTheHangupAndTheVendorIsSilent()
    {
        // Arrange: a vendor that asks for one function call and never answers a close; a function that
        // runs until the test releases it
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.EventsToSend.Add(HeldFunction.CallEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            var function = new HeldFunction();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log, function);
            using var metrics = new MeterCapture(MeterName);
            using var calls = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeFunctionCalledEvent>().Any());

            // Act: the caller hangs up while the function runs; the function returns; the bound it
            // restarts passes on the clock
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await function.Started.WaitAsync(SignalTimeout);
            await client.SendHangupAsync();
            await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);

            function.Release();
            PassOverTheConnectArm(clock);
            PassOverTheFunctionArm(clock);
            var armedDue = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
            clock.Advance(CloseAnswerBound);

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                fault.Should().BeNull("a function's result that can no longer be sent does not fail the session");
                armedDue.Should().Be(CloseAnswerBound, "the function's return restarts the full bound");
                clock.TimersArmed.TryRead(out _).Should().BeFalse(
                    "the bound was armed once, when the function returned: the close that went out while it ran did not start it");
                sessionTask.Status.Should().Be(
                    TaskStatus.RanToCompletion, "the caller ended the session, so it completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    1, "the caller ended the session; the vendor's silence does not make it a failure");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    1, "the bound ended the wait, and every such session is counted once");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(0, "nothing broke");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "CloseUnanswered"
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("10000 ms", StringComparison.Ordinal),
                    "the warning names the bound that ended the session");
                AssertTheCallRanAndItsResultWasNotSent(fakeOpenAi, metrics, log, calls);
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Reading the caller fails, and the vendor never answers a close. The bridge closes toward the
    /// vendor as it does at a hangup, waits for the answer for the same ten seconds, and when they pass
    /// the session fails with the read's own exception. The unanswered-close count and its warning are
    /// for a session the caller ended, so neither moves for this one. The read fails because the owner
    /// disposed the AudioSocket session before handing it over (ADR-0053 R2). Measured on the wall
    /// clock before the manual clock existed (<c>Q8-input-fault-never</c>): a bridge that closed only
    /// after a completed read was still waiting on the vendor at 30 s, with nothing counted; one that
    /// closed on the fault as well ended the session <c>Faulted</c> with this exception, counted once as
    /// failed and never as unanswered.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCloseAndFailWithTheReadFault_WhenReadingTheCallerFailsAndTheVendorIsSilent()
    {
        // Arrange: a vendor that holds the socket and never answers a close; a caller's session its
        // owner has already disposed, so the bridge's first read of it throws
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await session.DisposeAsync();
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);

            // Act: the bridge's close reaches the vendor and arms the bound; the whole bound passes on
            // the clock
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            var clientClose = await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            PassOverTheConnectArm(clock);
            var armedDue = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
            var endedBeforeTheBound = sessionTask.IsCompleted;

            clock.Advance(CloseAnswerBound);
            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                clientClose.Should().Be(
                    WebSocketCloseStatus.NormalClosure,
                    "a failed read closes toward the vendor by the same rule as a hangup");
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                endedBeforeTheBound.Should().BeFalse(
                    "the bridge waits for the vendor's answer before it reports the fault, as it does at a hangup");
                fault.Should().BeOfType<ObjectDisposedException>(
                    "the session fails with the read's own exception once the bound ends the wait");
                sessionTask.Status.Should().Be(TaskStatus.Faulted, "a failed read is a failure, not an ending");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "the read's fault reaches the terminal block, which counts the failure once");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(0, "the session failed");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the counter is for a session the caller ended; this one failed");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered",
                    "the warning speaks of a caller who hung up, and this caller did not");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Reading the caller fails, and the vendor answers the bridge's close with <c>1000</c>, as the live
    /// one did (about 1.1 s later; the fake answers at once). The answer ends the wait, with no time
    /// passing on the clock, and the session fails with the read's own exception. The bound is armed by
    /// the close but never runs out, so nothing is counted as unanswered.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldFailWithTheReadFault_WhenReadingTheCallerFailsAndTheVendorAnswers()
    {
        // Arrange: a vendor that answers the client's close with 1000 at once; a caller's session its
        // owner has already disposed, so the bridge's first read of it throws
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await session.DisposeAsync();
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);

            // Act: nothing moves the clock; the vendor's answer is the only thing that can end the wait
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            var clientClose = await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));
            PassOverTheConnectArm(clock);

            // Assert
            using (new AssertionScope())
            {
                clientClose.Should().Be(
                    WebSocketCloseStatus.NormalClosure,
                    "a failed read closes toward the vendor by the same rule as a hangup");
                clock.TimersArmed.TryRead(out var armedDue).Should().BeTrue("the bridge's close arms the bound");
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                fault.Should().BeOfType<ObjectDisposedException>(
                    "the session fails with the read's own exception once the vendor answers");
                sessionTask.Status.Should().Be(TaskStatus.Faulted, "a failed read is a failure, not an ending");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "the read's fault reaches the terminal block, which counts the failure once");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(0, "the session failed");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the vendor answered the close, and the bound never ran out");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered", "the vendor answered the close");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The vendor rejects the session: an error frame, then a close with a failure code, as the live
    /// vendor ended a refused session (<c>4000 beta_api_shape_disabled</c> for the retired beta shape,
    /// <c>4004 model_not_found</c> for a retired model, <c>3000 invalid_api_key</c> for a bogus key, all
    /// measured 2026-09-27). The close code is the vendor saying why, so the session is a failure that
    /// carries it, counted once as failed. <c>1001</c> is a failure here as it is for every speech
    /// client. The caller hangs up after the close, so that a bridge which ignores the close still
    /// returns rather than holding the test: measured on the fake before the fix, it returned normally at
    /// the hangup and counted a completion (<c>FAKE-today-close4000</c>).
    /// </summary>
    [Theory]
    [InlineData(4000, "invalid_request_error.beta_api_shape_disabled")]
    [InlineData(4004, "invalid_request_error.model_not_found")]
    [InlineData(3000, "invalid_request_error.invalid_api_key")]
    [InlineData(1001, "going away")]
    public async Task HandleSessionAsync_ShouldThrowACloseCodeFailure_WhenTheVendorClosesWithAFailureCode(
        int code, string reason)
    {
        // Arrange: a vendor that holds the socket and never closes on its own, so the only close is the
        // one the test sends. EventsToSend stays empty: every frame goes through SendEventAsync.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, new FakeTimeProvider(), log);
            using var metrics = new MeterCapture(MeterName);
            using var errors = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeErrorEvent>().Any());

            // Act: the vendor sends an error frame and closes; after the close, the caller hangs up
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await VendorRefusesTheSessionAsync(fakeOpenAi, errors, (WebSocketCloseStatus)code, reason);
            await client.SendHangupAsync();

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            var failure = fault as SpeechProviderFailureException;
            var expectedCode = code.ToString(CultureInfo.InvariantCulture);
            using (new AssertionScope())
            {
                fault.Should().BeOfType<SpeechProviderFailureException>(
                    "a close with any code but a normal closure is the vendor saying the session failed");
                (failure?.Signal).Should().Be(
                    SpeechProviderFailureSignal.CloseCode, "the close code carried the failure");
                (failure?.Code).Should().Be(expectedCode, "the vendor's own close code, verbatim");
                (failure?.Provider).Should().Be("OpenAiRealtime", "the bridge names the provider that failed");
                (failure?.Message).Should().Contain(reason, "the vendor's reason says why the session failed");
                errors.Events.OfType<RealtimeErrorEvent>().Should().ContainSingle(
                    e => e.Message == RefusalMessage,
                    "the error frame before the close is still published as an event");
                sessionTask.Status.Should().Be(TaskStatus.Faulted, "the vendor ended the session with a failure");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "the failure reaches the terminal block, which counts it once");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(
                    0, "a session the vendor refused did not complete");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "SessionError"
                        && e.Level == LogLevel.Error
                        && e.Message.Contains(reason, StringComparison.Ordinal),
                    "the terminal block logs the failure with the vendor's reason");
                log.Entries.Should().ContainSingle(
                    e => e.EventId.Name == "VendorClosed"
                        && e.Level == LogLevel.Information
                        && e.Message.Contains($"{expectedCode} {reason}", StringComparison.Ordinal),
                    "every close from the vendor is logged with its code and reason");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A control: the same refusal shape, an error frame and then a close, but with a normal closure or
    /// with a close that carries no code. Both are the vendor ending the session, not failing it, so the
    /// session completes: the close-code rule does not reach past the codes that mean failure. On a
    /// <see cref="ClientWebSocket"/> the close with no code reads as a normal closure with no reason
    /// (see <c>RealtimeFakeServerTests</c>), so its row shows that the fake's code-less close is not read
    /// as a failure.
    /// </summary>
    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure, "")]
    [InlineData(WebSocketCloseStatus.Empty, null)]
    public async Task HandleSessionAsync_ShouldComplete_WhenTheVendorClosesNormally(
        WebSocketCloseStatus status, string? reason)
    {
        // Arrange: a vendor that holds the socket and never closes on its own
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, new FakeTimeProvider(), log);
            using var metrics = new MeterCapture(MeterName);
            using var errors = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeErrorEvent>().Any());

            // Act: the vendor sends an error frame and closes; after the close, the caller hangs up
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await VendorRefusesTheSessionAsync(fakeOpenAi, errors, status, reason);
            await client.SendHangupAsync();

            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            using (new AssertionScope())
            {
                fault.Should().BeNull("a normal closure, or a close with no code, ends the session without failing it");
                sessionTask.Status.Should().Be(TaskStatus.RanToCompletion, "the session completed");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(1, "the session completed");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    0, "an error frame the vendor closed normally after is an event, not a failure");
                log.Entries.Should().NotContain(e => e.EventId.Name == "SessionError", "nothing failed");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The vendor closes while the caller is still on the line and still sending audio. The session
    /// ends at that close, not at a hangup that may come much later: it returns for a normal closure and
    /// throws for a failure code. Measured against the fake before the fix, a session the vendor closed
    /// with either code went on reading the caller until the hangup (<c>FAKE-today-close1000</c>,
    /// <c>FAKE-today-close4000</c>); live, a session refused in its first 20 ms sent 597 messages into
    /// the closed socket. This test never hangs up before its assertions.
    /// </summary>
    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure, "", "returned")]
    [InlineData((WebSocketCloseStatus)4004, "invalid_request_error.model_not_found", "threw close code 4004")]
    public async Task HandleSessionAsync_ShouldEndAtTheVendorsClose_WhenTheCallerIsStillOnTheLine(
        WebSocketCloseStatus status, string reason, string expectedOutcome)
    {
        // Arrange: a vendor that holds the socket and never closes on its own
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = CreateBridge(fakeOpenAi, new FakeTimeProvider());
            using var metrics = new MeterCapture(MeterName);

            // Act: the caller speaks, and the bridge forwards it to the vendor; then the vendor closes
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
            await client.SendAudioAsync(new byte[320]);
            await fakeOpenAi.WaitForClientFrameAsync("\"input_audio_buffer.append\"").WaitAsync(SignalTimeout);

            await fakeOpenAi.SendCloseAsync(status, reason);
            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            var outcome = fault switch
            {
                null => "returned",
                SpeechProviderFailureException failure => $"threw close code {failure.Code}",
                _ => $"threw {fault.GetType().Name}",
            };
            using (new AssertionScope())
            {
                outcome.Should().Be(
                    expectedOutcome,
                    "the session ends at the vendor's close while the caller is still on the line, "
                    + "and is classified by that close");
                (metrics.Get("openai_realtime.sessions.completed") + metrics.Get("openai_realtime.sessions.failed"))
                    .Should().Be(1, "the session was counted once, when the vendor ended it");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The caller hangs up, and the vendor answers the bridge's close with a failure code six seconds
    /// later, inside the bound. A close with an error is still a failure, whoever closed first, so the
    /// session fails with that code; the bound did not run out, so nothing is counted as unanswered.
    /// Measured on the wall clock before the manual clock existed: <c>Q2-answer-4000-at-6000</c>,
    /// <c>Faulted</c> at 6,010 ms. A 5 s bound counted that session as a completion.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldFail_WhenTheVendorAnswersTheCloseWithAFailureCodeWithinTheBound()
    {
        // Arrange: a vendor that answers the client's close only when the test says so
        await using var fakeOpenAi = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            var clock = new FakeTimeProvider();
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            await using var bridge = CreateBridge(fakeOpenAi, clock, log);
            using var metrics = new MeterCapture(MeterName);
            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());

            // Act: the caller hangs up; 6 s pass; then the vendor answers the close with 4000
            var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
            await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
            var armedDue = await HangUpAndWaitForTheArmAsync(client, fakeOpenAi, clock);

            clock.Advance(TimeSpan.FromSeconds(6));
            // Recorded rather than thrown, as in the 1000 control above: the assertions below say why
            // better than a failed send does.
            var answerFault = await Record.ExceptionAsync(
                () => fakeOpenAi.AnswerClientCloseAsync((WebSocketCloseStatus)4000, "probe_error"));
            var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

            // Assert
            var failure = fault as SpeechProviderFailureException;
            using (new AssertionScope())
            {
                armedDue.Should().Be(CloseAnswerBound, "the bound is ten seconds, counted from the bridge's close");
                answerFault.Should().BeNull("the vendor answers on a connection the bridge still holds");
                fault.Should().BeOfType<SpeechProviderFailureException>(
                    "a close with an error is a failure, even when it answers the bridge's own close");
                (failure?.Signal).Should().Be(
                    SpeechProviderFailureSignal.CloseCode, "the close code carried the failure");
                (failure?.Code).Should().Be("4000", "the vendor's own close code, verbatim");
                (failure?.Message).Should().Contain("probe_error", "the vendor's reason says why");
                sessionTask.Status.Should().Be(TaskStatus.Faulted, "the vendor's answer was a failure");
                metrics.Get("openai_realtime.sessions.failed").Should().Be(
                    1, "the failure reaches the terminal block, which counts it once");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(0, "the session failed");
                metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(
                    0, "the vendor answered within the bound");
                log.Entries.Should().NotContain(
                    e => e.EventId.Name == "CloseUnanswered", "the vendor answered the close");
            }
        }
        finally
        {
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The vendor refuses the session the way the live one did: an error frame, then a close. Waits for
    /// the bridge to publish the error frame before sending the close, so the close is the next thing
    /// the bridge reads.
    /// </summary>
    private static async Task VendorRefusesTheSessionAsync(
        RealtimeFakeServer fakeOpenAi,
        RealtimeEventCollector errors,
        WebSocketCloseStatus status,
        string? reason)
    {
        await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
        await fakeOpenAi.SendEventAsync(RefusalFrame);
        await errors.Satisfied.WaitAsync(SignalTimeout);
        await fakeOpenAi.SendCloseAsync(status, reason);
    }

    /// <summary>
    /// What a function call that returned after the bridge's close leaves behind: it ran to its end, so
    /// it is published once with its own result; its result and the <c>response.create</c> that would
    /// follow it never reached the vendor, which is logged; and <c>messages.sent</c> counts only the
    /// <c>session.update</c>, the one frame these sessions send (the caller sends no audio).
    /// </summary>
    private static void AssertTheCallRanAndItsResultWasNotSent(
        RealtimeFakeServer fakeOpenAi,
        MeterCapture metrics,
        RecordingLogger<OpenAiRealtimeBridge> log,
        RealtimeEventCollector calls)
    {
        // Two assertions rather than ContainSingle(...).Which: a failed Which ends the assertion scope,
        // and the red would then hide the assertions below it.
        var published = calls.Events.OfType<RealtimeFunctionCalledEvent>().ToArray();
        published.Should().ContainSingle(
            "the function ran, so the call is published whether or not its result could be sent");
        published.Should().OnlyContain(
            e => e.ResultJson == HeldFunction.Result,
            "the function ran to its end on the session token; the hangup did not cancel it");
        log.Entries.Should().ContainSingle(
            e => e.EventId.Name == "FunctionResultNotSent"
                && e.Level == LogLevel.Information
                && e.Message.Contains(HeldFunction.FunctionName, StringComparison.Ordinal),
            "a result that is dropped is logged, naming its function");
        fakeOpenAi.ReceivedMessages.Should().NotContain(
            m => m.Contains("\"type\":\"conversation.item.create\"", StringComparison.Ordinal)
                || m.Contains("\"type\":\"response.create\"", StringComparison.Ordinal),
            "the result came after the bridge's close, so neither it nor a new response reaches the vendor");
        metrics.Get("openai_realtime.messages.sent").Should().Be(
            1, "only the session.update went out; a result that was not sent is not counted");
    }

    /// <summary>
    /// A function the vendor calls once per session, which reports when it starts and then runs until
    /// the test calls <see cref="Release"/>. It honours its token: a cancelled token would end it with an
    /// <see cref="OperationCanceledException"/>, which the bridge turns into an error result.
    /// </summary>
    private sealed class HeldFunction : IRealtimeFunctionHandler
    {
        public const string FunctionName = "lookup_account";

        public const string Result = """{"balance":42}""";

        /// <summary>The vendor's request for this function, as it arrives on the wire.</summary>
        public const string CallEvent =
            """{"type":"response.function_call_arguments.done","call_id":"call-held","name":"lookup_account","arguments":"{}"}""";

        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => FunctionName;

        public string Description => "Looks up an account, for as long as the test says";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        /// <summary>Completes when the bridge has called the function.</summary>
        public Task Started => _started.Task;

        public void Release() => _released.TrySetResult();

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _started.TrySetResult();
            await _released.Task.WaitAsync(ct).ConfigureAwait(false);
            return Result;
        }
    }

    /// <summary>
    /// Hangs the caller up and returns the bound's due time, once the bridge's close has reached the
    /// fake and the clock has reported the bound armed: the moment a test may move the clock.
    /// </summary>
    private static async Task<TimeSpan> HangUpAndWaitForTheArmAsync(
        AudioSocketClient client, RealtimeFakeServer fakeOpenAi, FakeTimeProvider clock)
    {
        await client.SendHangupAsync();
        await fakeOpenAi.ClientCloseReceived.WaitAsync(SignalTimeout);
        PassOverTheConnectArm(clock);
        return await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
    }

    /// <summary>
    /// Passes over the connect bound's arm, when the clock holds one: the bridge makes it first, when
    /// its dial starts, and it is spent once the session opens, so the next arm read is the close
    /// bound's. Called once the session has opened, by which time that arm is already on the clock.
    /// </summary>
    private static void PassOverTheConnectArm(FakeTimeProvider clock)
    {
        if (clock.TimersArmed.TryPeek(out var due) && due == ConnectBound)
            clock.TimersArmed.TryRead(out _);
    }

    /// <summary>
    /// Passes over the arm of a function call's own bound (<c>FunctionCallTimeout</c>, 30 s by default), when the
    /// clock holds one: the bridge makes it when the call starts, before the call returns and before any close
    /// bound's arm, so the next arm read is the close bound's. Called after <see cref="PassOverTheConnectArm"/>.
    /// </summary>
    private static void PassOverTheFunctionArm(FakeTimeProvider clock)
    {
        if (clock.TimersArmed.TryPeek(out var due) && due == FunctionCallBound)
            clock.TimersArmed.TryRead(out _);
    }

    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>();
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(SignalTimeout);
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(
        RealtimeFakeServer fakeOpenAi,
        TimeProvider? clock = null,
        ILogger<OpenAiRealtimeBridge>? logger = null,
        IRealtimeFunctionHandler? function = null)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        return new OpenAiRealtimeBridge(
            options,
            new RealtimeFunctionRegistry(function is null ? [] : [function]),
            logger ?? NullLogger<OpenAiRealtimeBridge>.Instance)
        {
            BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
            TimeProvider = clock ?? TimeProvider.System,
        };
    }
}
