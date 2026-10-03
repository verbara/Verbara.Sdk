using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

/// <summary>
/// A function call the vendor requested holds the session for at most <c>FunctionCallTimeout</c> (30 s by
/// default). When the bound elapses with the session token live, the bridge answers the call as it answers a
/// handler that threw, with <c>{"error":"timeout"}</c>, warns, counts it, cancels the token it handed the handler,
/// publishes the call once with that output, and goes on reading the vendor; the handler is abandoned and whatever
/// it ends with is observed and never sent. Before the bound, a handler that ignored its token and never returned
/// held the session for good, with the caller in silence.
/// </summary>
/// <remarks>
/// <para>
/// Every bound here runs on the bridge's <see cref="FakeTimeProvider"/>. A test moves the clock only once the
/// bound is armed: it passes over the connect bound's arm (5 s, made when the dial starts) and reads the next arm,
/// which is the function call's. Where no bound is armed, as on code without one, that read reaches
/// <see cref="SignalTimeout"/> and the test fails there, with nothing moved.
/// </para>
/// <para>
/// No test waits on the wall clock. Each waits on the signal it asserts, bounded by <see cref="SignalTimeout"/>,
/// whose expiry is a failure.
/// </para>
/// </remarks>
public sealed partial class FunctionCallTimeoutTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The default <c>FunctionCallTimeout</c>, as the requirement states it.</summary>
    private static readonly TimeSpan FunctionBound = TimeSpan.FromSeconds(30);

    /// <summary>The bridge's connect bound: the first arm on its clock.</summary>
    private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(5);

    /// <summary>How long the bridge waits for the vendor to answer its close.</summary>
    private static readonly TimeSpan CloseAnswerBound = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    private const string TimedOutCounter = "openai_realtime.function_calls.timed_out";

    private const string TimeoutOutput = """{"error":"timeout"}""";

    private const string ItemCreate = "\"type\":\"conversation.item.create\"";

    private const string ResponseCreate = "\"type\":\"response.create\"";

    private const string SpeechStarted = """{"type":"input_audio_buffer.speech_started"}""";

    // ── The bound ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTimeoutWarnCountAndGoOn_WhenAFunctionIgnoresItsTokenAndNeverReturns()
    {
        // Red on code without the bound: no 30 s bound is armed, and nothing is ever sent for the call.
        using var function = ScriptedFunction.IgnoresItsTokenAndNeverReturns();
        await using var run = await SessionRun.StartAsync(function);
        try
        {
            var armed = await run.FunctionArmAsync();
            armed.Should().Be(FunctionBound, "the call is bounded by the default FunctionCallTimeout, on the bridge's clock");
            if (armed is null)
                return;

            run.Clock.Advance(FunctionBound - TimeSpan.FromTicks(1));
            var sentBefore = run.ItemFrames().Length;
            run.Clock.Advance(TimeSpan.FromTicks(1));

            await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);
            await run.Fake.SendEventAsync(SpeechStarted);
            await run.Events.WaitForAsync(e => e.OfType<RealtimeSpeechStartedEvent>().Any()).WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                sentBefore.Should().Be(0, "nothing is sent for the call one tick before its bound");
                run.ItemFrames().Should().ContainSingle("the call is answered once");
                run.ItemFrames().Select(CallIdOf).Should().Equal([ScriptedFunction.CallId]);
                run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput], "a timed-out call is answered with the timeout output");
                run.Log.Entries.Should().ContainSingle(e => e.EventId.Name == "FunctionCallTimedOut")
                    .Which.Level.Should().Be(LogLevel.Warning);
                run.Metrics.Get(TimedOutCounter).Should().Be(1);
                run.Metrics.Get("openai_realtime.function_calls.total").Should().Be(1, "a timed-out call is also a call");
                run.SessionTask.IsCompleted.Should().BeFalse("the expiry neither fails nor ends the session");
            }
        }
        finally
        {
            function.Release();
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldSendAFunctionCallOutputWithinItsBound_WhenTheHandlerNeverReturns()
    {
        // The red that reads no arm: the clock is moved by the default bound once the handler has started, and a
        // function_call_output must follow. On code without the bound none is ever sent.
        using var function = ScriptedFunction.IgnoresItsTokenAndNeverReturns();
        await using var run = await SessionRun.StartAsync(function);
        try
        {
            run.Clock.Advance(FunctionBound);
            var sent = await Record.ExceptionAsync(() => run.Fake.WaitForClientFrameAsync(ItemCreate).WaitAsync(SignalTimeout));

            using (new AssertionScope())
            {
                sent.Should().BeNull("a function_call_output is sent for the call once its 30 s bound has elapsed");
                run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput]);
            }
        }
        finally
        {
            function.Release();
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldPublishTheCallOnceWithTheTimeoutOutput_WhenItsBoundElapses()
    {
        using var function = ScriptedFunction.IgnoresItsTokenAndNeverReturns();
        await using var run = await SessionRun.StartAsync(function);
        try
        {
            await run.ElapseTheFunctionBoundAsync();
            await run.Events.WaitForAsync(e => e.OfType<RealtimeFunctionCalledEvent>().Any()).WaitAsync(SignalTimeout);

            run.Events.Events.OfType<RealtimeFunctionCalledEvent>().Select(e => e.ResultJson)
                .Should().Equal([TimeoutOutput], "the call is published once, with what the model received");
        }
        finally
        {
            function.Release();
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTheResultWithoutWarning_WhenAFunctionReturnsInsideItsBound()
    {
        // Control: green with and without the bound.
        using var function = ScriptedFunction.ReturnsWhenReleased();
        await using var run = await SessionRun.StartAsync(function);

        function.Release();
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            run.ItemFrames().Select(OutputOf).Should().Equal([ScriptedFunction.Result]);
            run.Log.Entries.Should().NotContain(e => e.EventId.Name == "FunctionCallTimedOut");
            run.Metrics.Get(TimedOutCounter).Should().Be(0);
        }
    }

    [Theory]
    [InlineData("result")]
    [InlineData("throw")]
    public async Task HandleSessionAsync_ShouldSendNothingMoreAndObserveIt_WhenAnAbandonedFunctionEndsLate(string ending)
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var function = ending == "throw" ? ScriptedFunction.ThrowsWhenReleased() : ScriptedFunction.ReturnsWhenReleased();
            await using (var run = await SessionRun.StartAsync(function))
            {
                await run.ElapseTheFunctionBoundAsync();
                await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);
                await run.Events.WaitForAsync(e => e.OfType<RealtimeFunctionCalledEvent>().Any()).WaitAsync(SignalTimeout);

                function.Release();
                await function.Ended.WaitAsync(SignalTimeout);

                // The vendor proves the session read past the late ending: a message sent after it is relayed.
                await run.Fake.SendEventAsync(SpeechStarted);
                await run.Events.WaitForAsync(e => e.OfType<RealtimeSpeechStartedEvent>().Any()).WaitAsync(SignalTimeout);

                using (new AssertionScope())
                {
                    run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput], "a late ending is never sent");
                    run.Events.Events.OfType<RealtimeFunctionCalledEvent>().Should().ContainSingle("a late ending is not published again");
                }
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

            Volatile.Read(ref unobserved).Should().Be(0, "the abandoned call's ending is observed by the bridge");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // ── What decides "timed out" ────────────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTimeoutAndCount_WhenAFunctionHonoursItsTokenAndThrowsWhenItIsCancelled()
    {
        // The bound cancels the handler's token; the handler stops at once with OperationCanceledException. The
        // call is still answered as timed out: the decision is made before the token is cancelled.
        using var function = ScriptedFunction.HonoursItsToken(onCancel: ct => throw new OperationCanceledException(ct));
        await using var run = await SessionRun.StartAsync(function);

        await run.ElapseTheFunctionBoundAsync();
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput]);
            run.Log.Entries.Should().ContainSingle(e => e.EventId.Name == "FunctionCallTimedOut");
            run.Metrics.Get(TimedOutCounter).Should().Be(1);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCancelTheHandlersTokenAfterDecidingButNotTheSessions_WhenTheBoundElapses()
    {
        // The handler answers its token's cancellation with a normal result the instant it fires. Had the token been
        // cancelled before the bridge decided, that result would have been sent; it is the timeout output instead.
        using var function = ScriptedFunction.HonoursItsToken(onCancel: _ => """{"stopped":true}""");
        await using var run = await SessionRun.StartAsync(function);

        await run.ElapseTheFunctionBoundAsync();
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);
        await function.Ended.WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            function.Token.IsCancellationRequested.Should().BeTrue("the bound's expiry cancels the token handed to the handler");
            run.SessionToken.IsCancellationRequested.Should().BeFalse("the session's own token is not cancelled");
            run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput],
                "the token is cancelled after the call was decided as timed out, so the handler's answer to it is not sent");
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTheHandlersOwnTimeout_WhenItThrowsTimeoutExceptionInsideTheBound()
    {
        // Control: green with and without the bound. The handler's own TimeoutException, one second in on the
        // bridge's clock, is its answer, not the bound's.
        using var function = ScriptedFunction.ThrowsItsOwnTimeoutAfter(TimeSpan.FromSeconds(1));
        await using var run = await SessionRun.StartAsync(function);
        function.UseClock(run.Clock);

        await function.Armed.WaitAsync(SignalTimeout);
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            run.ItemFrames().Select(OutputOf).Should().Equal([$$"""{"error":"{{ScriptedFunction.OwnTimeoutMessage}}"}"""]);
            run.Log.Entries.Should().NotContain(e => e.EventId.Name == "FunctionCallTimedOut");
            run.Metrics.Get(TimedOutCounter).Should().Be(0);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldBoundTheCall_WhenTheHandlerBlocksItsThreadBeforeReturningATask()
    {
        using var function = ScriptedFunction.BlocksItsThread();
        await using var run = await SessionRun.StartAsync(function);
        try
        {
            await run.ElapseTheFunctionBoundAsync();
            await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);
            await run.Fake.SendEventAsync(SpeechStarted);
            await run.Events.WaitForAsync(e => e.OfType<RealtimeSpeechStartedEvent>().Any()).WaitAsync(SignalTimeout);

            function.Release();
            await function.Ended.WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput], "nothing more is sent once the thread is released");
                run.Metrics.Get(TimedOutCounter).Should().Be(1);
            }
        }
        finally
        {
            function.Release();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HandleSessionAsync_ShouldAnswerTimeout_WhenTheFunctionCompletesExactlyAtTheBound(int runNumber)
    {
        // The handler's own delay of exactly 30 s on the bridge's clock: one Advance fires both. The decision reads
        // the bound's flag, so the call is timed out, on every run.
        _ = runNumber;
        using var function = ScriptedFunction.ReturnsAfter(FunctionBound);
        await using var run = await SessionRun.StartAsync(function, beforeSession: function.UseClock);

        await run.ElapseTheFunctionBoundAsync();
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            run.ItemFrames().Select(OutputOf).Should().Equal([TimeoutOutput]);
            run.Log.Entries.Should().ContainSingle(e => e.EventId.Name == "FunctionCallTimedOut");
            run.Metrics.Get(TimedOutCounter).Should().Be(1);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldSendTheResultAloneWithoutWarning_WhenTheBoundElapsesAsTheResultIsWritten()
    {
        // Control: the handler returns 29 s in; the clock passes the 30 s mark right after, while the bridge writes
        // the result. The bound covers the handler only, so the answer is the result, sent once, and nothing is
        // warned or counted. The fake cannot hold a frame, so the clock is moved between the two frames of the
        // answer: once the result has landed and before the response.create that follows it.
        using var function = ScriptedFunction.ReturnsAfter(TimeSpan.FromSeconds(29));
        await using var run = await SessionRun.StartAsync(function, beforeSession: function.UseClock);

        await run.FunctionArmAsync();
        await function.Armed.WaitAsync(SignalTimeout);
        run.Clock.Advance(TimeSpan.FromSeconds(29));
        await run.Fake.WaitForClientFrameAsync(ItemCreate).WaitAsync(SignalTimeout);
        run.Clock.Advance(TimeSpan.FromSeconds(2));
        await run.Fake.WaitForClientFrameAsync(ResponseCreate).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            run.ItemFrames().Select(OutputOf).Should().Equal([ScriptedFunction.Result]);
            run.Log.Entries.Should().NotContain(e => e.EventId.Name == "FunctionCallTimedOut");
            run.Metrics.Get(TimedOutCounter).Should().Be(0);
            run.SessionTask.IsFaulted.Should().BeFalse();
        }
    }

    // ── The host's cancellation ─────────────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldReturnWithoutWaitingForTheHandler_WhenTheHostCancelsDuringAHeldCall()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        using var function = ScriptedFunction.ThrowsWhenReleased();
        try
        {
            await using (var run = await SessionRun.StartAsync(function))
            {
                await run.CancelSessionAsync();
                var ended = await Record.ExceptionAsync(() => run.SessionTask.WaitAsync(SignalTimeout));

                function.Release();
                await function.Ended.WaitAsync(SignalTimeout);

                using (new AssertionScope())
                {
                    ended.Should().BeNull("the session returns at the host's cancellation, without waiting for the handler");
                    run.ItemFrames().Should().BeEmpty("nothing is sent for an abandoned call");
                    run.Log.Entries.Should().NotContain(e => e.EventId.Name == "FunctionCallTimedOut");
                    run.Metrics.Get(TimedOutCounter).Should().Be(0);
                    run.Events.Events.OfType<RealtimeFunctionCalledEvent>().Should().BeEmpty(
                        "a call abandoned by the host's cancellation publishes nothing");
                }
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

            Volatile.Read(ref unobserved).Should().Be(0, "the handler's late fault is observed by the bridge");
        }
        finally
        {
            function.Release();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // ── At the hangup ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSessionAsync_ShouldWarnCountPublishAndRestartTheCloseBound_WhenAFunctionAtTheHangupOutlivesItsBound()
    {
        // The vendor never answers the bridge's close. The function never returns. At its bound nothing is sent,
        // the timeout is warned, counted and published, and the close-answer bound restarts in full from there.
        using var function = ScriptedFunction.IgnoresItsTokenAndNeverReturns();
        await using var run = await SessionRun.StartAsync(function);
        try
        {
            await run.Client.SendHangupAsync();
            await run.Fake.ClientCloseReceived.WaitAsync(SignalTimeout);

            await run.ElapseTheFunctionBoundAsync();
            await run.Events.WaitForAsync(e => e.OfType<RealtimeFunctionCalledEvent>().Any()).WaitAsync(SignalTimeout);
            var closeArm = await run.Clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
            run.Clock.Advance(CloseAnswerBound);
            var fault = await Record.ExceptionAsync(() => run.SessionTask.WaitAsync(SignalTimeout));

            using (new AssertionScope())
            {
                fault.Should().BeNull("the caller ended the session");
                run.ItemFrames().Should().BeEmpty("nothing is sent once the bridge's close is out");
                run.Log.Entries.Should().ContainSingle(e => e.EventId.Name == "FunctionCallTimedOut");
                run.Metrics.Get(TimedOutCounter).Should().Be(1);
                run.Events.Events.OfType<RealtimeFunctionCalledEvent>().Select(e => e.ResultJson).Should().Equal([TimeoutOutput]);
                closeArm.Should().Be(CloseAnswerBound, "the close-answer bound restarts in full at the abandonment");
                run.Metrics.Get("openai_realtime.sessions.close_unanswered").Should().Be(1);
                run.Log.Entries.Should().ContainSingle(e => e.EventId.Name == "CloseUnanswered");
            }
        }
        finally
        {
            function.Release();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? OutputOf(string itemFrame)
    {
        using var document = JsonDocument.Parse(itemFrame);
        return document.RootElement.GetProperty("item").GetProperty("output").GetString();
    }

    private static string? CallIdOf(string itemFrame)
    {
        using var document = JsonDocument.Parse(itemFrame);
        return document.RootElement.GetProperty("item").GetProperty("call_id").GetString();
    }

    /// <summary>
    /// One session against the fake vendor, with one function on the bridge's manual clock. The vendor requests the
    /// call once the session is open; disposal releases everything, the session included, each wait bounded.
    /// </summary>
    private sealed class SessionRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _session = new();
        private readonly AudioSocketServer _audioServer;

        private SessionRun(
            RealtimeFakeServer fake, AudioSocketServer audioServer, AudioSocketClient client, OpenAiRealtimeBridge bridge,
            FakeTimeProvider clock, RecordingLogger<OpenAiRealtimeBridge> log, MeterCapture metrics, EventWaiter events)
        {
            Fake = fake;
            _audioServer = audioServer;
            Client = client;
            Bridge = bridge;
            Clock = clock;
            Log = log;
            Metrics = metrics;
            Events = events;
        }

        public RealtimeFakeServer Fake { get; }

        public AudioSocketClient Client { get; }

        public OpenAiRealtimeBridge Bridge { get; }

        public FakeTimeProvider Clock { get; }

        public RecordingLogger<OpenAiRealtimeBridge> Log { get; }

        public MeterCapture Metrics { get; }

        public EventWaiter Events { get; }

        public Task SessionTask { get; private set; } = Task.CompletedTask;

        public CancellationToken SessionToken => _session.Token;

        public static async Task<SessionRun> StartAsync(
            ScriptedFunction function, Action<FakeTimeProvider>? beforeSession = null, OpenAiRealtimeOptions? options = null,
            bool requestTheCall = true)
        {
            // A vendor that never closes on its own and never answers a close: the session ends only as a test ends it.
            var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
            fake.Start();

            var audioServer = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
            var started = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            audioServer.OnSessionStarted += s =>
            {
                started.TrySetResult(s);
                return ValueTask.CompletedTask;
            };
            await audioServer.StartAsync(CancellationToken.None);
            var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
            await client.ConnectAsync(CancellationToken.None);
            var session = await started.Task.WaitAsync(SignalTimeout);

            var clock = new FakeTimeProvider();
            beforeSession?.Invoke(clock);
            var log = new RecordingLogger<OpenAiRealtimeBridge>();
            var bridge = new OpenAiRealtimeBridge(
                Options.Create(options ?? DefaultOptions()),
                new RealtimeFunctionRegistry([function]),
                log)
            {
                BaseUri = new Uri($"ws://127.0.0.1:{fake.Port}/"),
                TimeProvider = clock,
            };
            var metrics = new MeterCapture(MeterName);
            var events = new EventWaiter(bridge.Events);

            var run = new SessionRun(fake, audioServer, client, bridge, clock, log, metrics, events);
            run.SessionTask = bridge.HandleSessionAsync(session, run._session.Token).AsTask();

            await fake.SessionUpdateReceived.WaitAsync(SignalTimeout);
            if (requestTheCall)
            {
                await fake.SendEventAsync(ScriptedFunction.CallEvent);
                await function.Started.WaitAsync(SignalTimeout);
            }

            return run;
        }

        public static OpenAiRealtimeOptions DefaultOptions() => new()
        {
            ApiKey = "test-key",
            Model = "gpt-realtime",
            InputFormat = AudioFormat.Slin16Mono8kHz,
        };

        /// <summary>The <c>conversation.item.create</c> frames the vendor received, in order.</summary>
        public string[] ItemFrames() =>
            [.. Fake.ReceivedMessages.Where(m => m.Contains(ItemCreate, StringComparison.Ordinal))];

        /// <summary>
        /// The due time of the function call's bound once it is armed: the next arm after the connect bound's.
        /// Faults with <see cref="TimeoutException"/> when none is armed.
        /// </summary>
        public async Task<TimeSpan?> FunctionArmAsync()
        {
            if (Clock.TimersArmed.TryPeek(out var due) && due == ConnectBound)
                Clock.TimersArmed.TryRead(out _);

            try
            {
                return await Clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>Waits for the function call's bound to be armed, then runs it out.</summary>
        public async Task ElapseTheFunctionBoundAsync()
        {
            var due = await FunctionArmAsync();
            due.Should().Be(FunctionBound, "the call's bound is armed on the bridge's clock at the default FunctionCallTimeout");
            Clock.Advance(due!.Value);
        }

        public Task CancelSessionAsync() => _session.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            await _session.CancelAsync();
            await SessionTask.WaitAsync(SignalTimeout).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            Events.Dispose();
            Metrics.Dispose();
            await Bridge.DisposeAsync();
            await Client.DisposeAsync();
            await _audioServer.StopAsync(CancellationToken.None);
            await Fake.DisposeAsync();
            _session.Dispose();
        }
    }

    /// <summary>
    /// Keeps the bridge's events, and completes a wait the moment the events seen so far satisfy its predicate.
    /// </summary>
    private sealed class EventWaiter : IDisposable
    {
        private readonly List<RealtimeEvent> _events = [];
        private readonly List<(Func<IReadOnlyList<RealtimeEvent>, bool> Predicate, TaskCompletionSource Source)> _waits = [];
        private readonly IDisposable _subscription;

        public EventWaiter(IObservable<RealtimeEvent> events) => _subscription = events.Subscribe(OnNext);

        public IReadOnlyList<RealtimeEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public Task WaitForAsync(Func<IReadOnlyList<RealtimeEvent>, bool> predicate)
        {
            lock (_events)
            {
                if (predicate(_events))
                    return Task.CompletedTask;

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((predicate, source));
                return source.Task;
            }
        }

        public void Dispose() => _subscription.Dispose();

        private void OnNext(RealtimeEvent evt)
        {
            lock (_events)
            {
                _events.Add(evt);
                foreach (var (predicate, source) in _waits.Where(w => w.Predicate(_events)).ToArray())
                {
                    source.TrySetResult();
                    _waits.Remove((predicate, source));
                }
            }
        }
    }

    /// <summary>
    /// The one function the vendor calls. Reports when it starts, keeps the token it was handed, and reports when it
    /// ends, however it ends.
    /// </summary>
    private sealed class ScriptedFunction : IRealtimeFunctionHandler, IDisposable
    {
        public const string FunctionName = "lookup_account";

        public const string CallId = "call-bound";

        public const string Result = """{"balance":42}""";

        public const string OwnTimeoutMessage = "the CRM did not answer";

        public const string CallEvent =
            """{"type":"response.function_call_arguments.done","call_id":"call-bound","name":"lookup_account","arguments":"{}"}""";

        private readonly Func<ScriptedFunction, CancellationToken, Task<string>> _body;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _threadGate = new();
        private TimeProvider _clock = TimeProvider.System;

        private ScriptedFunction(Func<ScriptedFunction, CancellationToken, Task<string>> body) => _body = body;

        public string Name => FunctionName;

        public string Description => "Looks up an account, as the test scripts it";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task Started => _started.Task;

        /// <summary>Completes when the handler has returned or thrown.</summary>
        public Task Ended => _ended.Task;

        /// <summary>Completes once the handler's own delay is on the clock.</summary>
        public Task Armed => _armed.Task;

        public CancellationToken Token { get; private set; }

        public static ScriptedFunction IgnoresItsTokenAndNeverReturns() =>
            new(async (self, _) =>
            {
                await self._released.Task.ConfigureAwait(false);
                return Result;
            });

        public static ScriptedFunction ReturnsWhenReleased() =>
            new(async (self, _) =>
            {
                await self._released.Task.ConfigureAwait(false);
                return Result;
            });

        public static ScriptedFunction ThrowsWhenReleased() =>
            new(async (self, _) =>
            {
                await self._released.Task.ConfigureAwait(false);
                throw new InvalidOperationException("late failure");
            });

        public static ScriptedFunction HonoursItsToken(Func<CancellationToken, string> onCancel) =>
            new(async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); // fence-allow: GUARD-TIMEOUT — infinite; the handed token is the only arm
                }
                catch (OperationCanceledException)
                {
                    return onCancel(ct);
                }

                return Result;
            });

        public static ScriptedFunction ThrowsItsOwnTimeoutAfter(TimeSpan after) =>
            new(async (self, _) =>
            {
                await self.ClockSet.ConfigureAwait(false);
                var delay = Task.Delay(after, self._clock, CancellationToken.None); // fence-allow: SIMULATED-WORK — the handler's work, on the bridge's manual clock
                self._armed.TrySetResult();
                await delay.ConfigureAwait(false);
                throw new TimeoutException(OwnTimeoutMessage);
            });

        public static ScriptedFunction ReturnsAfter(TimeSpan after) =>
            new(async (self, _) =>
            {
                var delay = Task.Delay(after, self._clock, CancellationToken.None); // fence-allow: SIMULATED-WORK — the handler's work, on the bridge's manual clock
                self._armed.TrySetResult();
                await delay.ConfigureAwait(false);
                return Result;
            });

        public static ScriptedFunction BlocksItsThread() =>
            new((self, _) =>
            {
                // Blocks the calling thread before any task exists, bounded so a test that fails cannot hold it.
                self._threadGate.Wait(SignalTimeout * 3, CancellationToken.None);
                return Task.FromResult(Result);
            });

        private readonly TaskCompletionSource _clockSet = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task ClockSet => _clockSet.Task;

        public void UseClock(TimeProvider clock)
        {
            _clock = clock;
            _clockSet.TrySetResult();
        }

        public void Release()
        {
            _released.TrySetResult();
            _threadGate.Set();
        }

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Token = ct;
            _started.TrySetResult();
            try
            {
                return await _body(this, ct).ConfigureAwait(false);
            }
            finally
            {
                _ended.TrySetResult();
            }
        }

        public void Dispose() => _threadGate.Dispose();
    }
}
