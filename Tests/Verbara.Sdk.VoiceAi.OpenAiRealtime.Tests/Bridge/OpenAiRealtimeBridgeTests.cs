using System.Net.WebSockets;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

public sealed class OpenAiRealtimeBridgeTests
{
    /// <summary>
    /// Upper bound on any single wait below. Reaching it is a failure, never a pace: every wait here
    /// is on a signal that arrives in milliseconds over a loopback socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <em>only</em> clock in these tests. The session token carries no timer at all, so
    /// no test here can reach its assertions by waiting one out — the shape that cost this class five
    /// seconds per test. Each test waits on the signal it asserts on, bounded by a
    /// <c>WaitAsync(SignalTimeout)</c> whose expiry is a <see cref="TimeoutException"/>, then cancels
    /// the token explicitly to end the session.
    /// </para>
    /// <para>
    /// These tests were written around a hangup/dispose race in <c>Verbara.Sdk.VoiceAi.AudioSocket</c>
    /// — a hangup that overtook <c>ReadAudioAsync</c>'s first <c>MoveNext</c> threw
    /// <see cref="ObjectDisposedException"/> out of the bridge, 1 run in 10 under CPU saturation.
    /// ADR-0053 fixed it: a hangup now ends the sequence whatever the ordering. Cancelling first and
    /// hanging up in cleanup is kept because it is the clearer way to end a session under test, not
    /// because the race is still there.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An event the fake delivers purely so the bridge publishes something. It is never asserted on.
    /// </summary>
    /// <remarks>
    /// A test that cancels the session needs to know both loops are running first, and the
    /// server-side capture of <c>session.update</c> does not establish that: the fake can read that
    /// frame off the wire while the client's <c>SendAsync</c> has not yet completed. A
    /// <em>published</em> event cannot be observed until <c>OutputLoop</c> is running, which is
    /// after that send completed, so it is the sentinel that establishes it.
    /// <para>
    /// It was originally introduced because cancelling before the loops started landed on the one
    /// await the bridge did not guard and faulted the session with
    /// <see cref="TaskCanceledException"/> — 1 run in 10 under CPU saturation. ADR-0053 brought that
    /// window under the same handling as the loops, so the sentinel no longer protects against a
    /// fault; it is kept because the assertions on live socket state still need to know the loops
    /// are up. The setup window now has its own coverage in
    /// <c>OpenAiRealtimeBridgeSetupWindowTests</c>.
    /// </para>
    /// </remarks>
    private const string LoopsRunningMarkerEvent = """{"type":"input_audio_buffer.speech_started"}""";

    private const string MeterName = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    /// <summary>
    /// One 20 ms frame of assistant audio: 480 silent samples at the Realtime API's 24 kHz, which
    /// the bridge's downsampler turns into 160 samples at the session's 8 kHz. Silent because the
    /// samples are never inspected — what matters is that a write to Asterisk is attempted at all.
    /// </summary>
    private static readonly string AssistantAudioDeltaEvent =
        $$"""{"type":"response.audio.delta","delta":"{{Convert.ToBase64String(new byte[960])}}"}""";

    [Fact]
    public async Task HandleSessionAsync_SendsSessionUpdate_OnConnect()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var loopsRunning = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());
        using var cts = new CancellationTokenSource();

        // Act — end on the frame under assertion, not on a token expiry
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);
        await loopsRunning.Satisfied.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        await sessionTask.WaitAsync(SignalTimeout);

        // Assert — the first client message should be session.update
        fakeOpenAi.ReceivedMessages.Should().ContainSingle(m => m.Contains("\"session.update\""));

        // …and the fake must have had it before it answered. This is the assertion that makes the
        // protocol sentinel load-bearing: without it, replacing the sentinel with the fixed delay it
        // superseded leaves the whole suite green, because every other assertion reads the end state
        // and the fake's drain loop captures session.update whenever it arrives.
        fakeOpenAi.FramesCapturedWhenAnswering
            .Should().Contain(m => m.Contains("\"session.update\""),
                "the fake answers on the client's request frame, not on a timer");
        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesResponseStartedAndEndedEvents()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"response.created"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"response.done"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var collector = new RealtimeEventCollector(
            bridge.Events,
            e => e.OfType<RealtimeResponseStartedEvent>().Any()
              && e.OfType<RealtimeResponseEndedEvent>().Any());
        using var cts = new CancellationTokenSource();

        // Act
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await collector.Satisfied.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        await sessionTask.WaitAsync(SignalTimeout);

        // Assert
        var events = collector.Events;
        events.Should().ContainSingle(e => e is RealtimeResponseStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeResponseEndedEvent);

        var started = events.OfType<RealtimeResponseStartedEvent>().Single();
        var ended = events.OfType<RealtimeResponseEndedEvent>().Single();

        // The previous `Duration > Zero` held only because the fake slept 5 ms between the two
        // events — it asserted the fake's timer, not the bridge, and would be a coin flip with the
        // timer gone. What the bridge does guarantee is that Duration is the interval between the
        // two events it published, and that is checkable without any delay at all.
        ended.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        ended.Duration.Should().BeCloseTo(ended.Timestamp - started.Timestamp, TimeSpan.FromMilliseconds(50));

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesTranscriptEvents()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"response.audio_transcript.delta","delta":"Hello"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"response.audio_transcript.done","transcript":"Hello world"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeTranscriptEvent>().Count() == 2);
        using var cts = new CancellationTokenSource();

        // Act
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await collector.Satisfied.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        await sessionTask.WaitAsync(SignalTimeout);

        // Assert
        var transcripts = collector.Events.OfType<RealtimeTranscriptEvent>().ToList();
        transcripts.Should().HaveCount(2);

        transcripts[0].Transcript.Should().Be("Hello");
        transcripts[0].IsFinal.Should().BeFalse();

        transcripts[1].Transcript.Should().Be("Hello world");
        transcripts[1].IsFinal.Should().BeTrue();

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesErrorEvent_OnOpenAiError()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"error","error":{"message":"rate limit exceeded"}}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeErrorEvent>().Any());
        using var cts = new CancellationTokenSource();

        // Act
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await collector.Satisfied.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        await sessionTask.WaitAsync(SignalTimeout);

        // Assert
        var errorEvent = collector.Events.OfType<RealtimeErrorEvent>().Should().ContainSingle().Subject;
        errorEvent.Message.Should().Be("rate limit exceeded");

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_CancellationToken_TerminatesBothLoops()
    {
        // Arrange — the fake holds the socket open, so OutputLoop is blocked on a *live* socket when
        // the token fires. Without the hold the fake closes as soon as it has answered, OutputLoop
        // returns on the Close frame, and this test passes for a reason cancellation had no part in.
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var loopsRunning = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());
        using var cts = new CancellationTokenSource();

        // Act — cancel once both loops are demonstrably running against a live socket
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await loopsRunning.Satisfied.WaitAsync(SignalTimeout);

        fakeOpenAi.SocketState.Should().Be(
            WebSocketState.Open,
            "cancellation has to be observed on a live socket, or the loops end on the server's close instead");

        await cts.CancelAsync();

        // Assert — both loops end on the token, and the session returns rather than faulting
        await sessionTask.WaitAsync(SignalTimeout);
        sessionTask.Status.Should().Be(TaskStatus.RanToCompletion);

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_PublishesSpeechEvents()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add("""{"type":"input_audio_buffer.speech_started"}""");
        fakeOpenAi.EventsToSend.Add("""{"type":"input_audio_buffer.speech_stopped"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var collector = new RealtimeEventCollector(
            bridge.Events,
            e => e.OfType<RealtimeSpeechStartedEvent>().Any()
              && e.OfType<RealtimeSpeechStoppedEvent>().Any());
        using var cts = new CancellationTokenSource();

        // Act
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await collector.Satisfied.WaitAsync(SignalTimeout);
        await cts.CancelAsync();
        await sessionTask.WaitAsync(SignalTimeout);

        // Assert
        var events = collector.Events;
        events.Should().ContainSingle(e => e is RealtimeSpeechStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeSpeechStoppedEvent);

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller hanging up while the assistant is speaking — the commonest ending there is, and
    /// the one the bridge used to mistake for a crash: the write to a torn-down session threw
    /// <see cref="ObjectDisposedException"/> straight out of <c>OutputLoop</c>, faulting the session
    /// and incrementing <c>sessions.failed</c> for a perfectly ordinary hangup (ADR-0053).
    /// </summary>
    /// <remarks>
    /// The ordering is authored, not hoped for. The fake sends nothing of its own and holds the
    /// socket open, so the single delta this test injects is guaranteed to arrive after the session
    /// is gone — <c>OnHangup</c> having fired is the proof, not a delay. <c>EventsToSend</c> could
    /// not express this: its burst leaves the instant <c>session.update</c> lands, which is before
    /// anything has ended.
    /// </remarks>
    [Fact]
    public async Task HandleSessionAsync_ShouldEndAsCompleted_WhenTheCallerHangsUpWhileTheAssistantIsSpeaking()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);
        using var metrics = new MeterCapture(MeterName);

        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => tornDown.TrySetResult();

        // Act — an uncancelled token throughout: this ending is the caller's, not a shutdown
        var sessionTask = bridge.HandleSessionAsync(session, CancellationToken.None).AsTask();
        await fakeOpenAi.SessionUpdateReceived.WaitAsync(SignalTimeout);

        await client.SendHangupAsync();
        await tornDown.Task.WaitAsync(SignalTimeout);       // the transport is demonstrably gone…

        await fakeOpenAi.SendEventAsync(AssistantAudioDeltaEvent);   // …and only then does it speak

        // Assert
        await sessionTask.WaitAsync(SignalTimeout);
        using (new AssertionScope())
        {
            sessionTask.Status.Should().Be(
                TaskStatus.RanToCompletion, "a hangup is an ending, not a fault");
            metrics.Get("openai_realtime.sessions.completed").Should().Be(1);
            metrics.Get("openai_realtime.sessions.failed").Should()
                .Be(0, "nothing broke — the far end simply stopped listening");
        }

        // Cleanup
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
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

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return (session, audioServer, client);
    }

    private static OpenAiRealtimeBridge CreateBridge(
        RealtimeFakeServer fakeOpenAi,
        IEnumerable<IRealtimeFunctionHandler>? handlers = null)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "alloy",
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        });
        var registry = new RealtimeFunctionRegistry(handlers ?? []);
        var bridge = new OpenAiRealtimeBridge(options, registry, NullLogger<OpenAiRealtimeBridge>.Instance);
        bridge.BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/");
        return bridge;
    }
}
