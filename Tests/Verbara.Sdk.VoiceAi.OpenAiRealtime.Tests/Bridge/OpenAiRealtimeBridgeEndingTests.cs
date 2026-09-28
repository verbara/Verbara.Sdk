using System.Net.WebSockets;
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

    /// <summary>
    /// How long the bridge waits for the vendor to answer its close: ten seconds, counted from the
    /// close. It is a private constant of the bridge, not an option, so the tests state it here.
    /// </summary>
    private static readonly TimeSpan CloseAnswerBound = TimeSpan.FromSeconds(10);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        return await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
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
