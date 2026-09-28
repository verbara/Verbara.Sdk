using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// The user-visible half of the recognizers' bounds: a <see cref="VoiceAiPipeline"/> call whose
/// recognizer's vendor never answers the end of input, or never answers the upgrade, ends when the
/// caller hangs up, and is counted.
/// </summary>
/// <remarks>
/// <para>
/// The caller says one utterance and hangs up. The pipeline hands the utterance to one of the four
/// WebSocket recognizers, whose vendor transcribes it and then never answers the end of input. Before
/// the bound, the handler never returned, not even after the AudioSocket server stopped, and the
/// session was never counted: the published contract says the session runs "until the AudioSocket
/// disconnects". The synthesizer is healthy and is never reached, because the utterance yields no
/// final transcript.
/// </para>
/// <para>
/// The recognizer's bound runs on a <see cref="FakeTimeProvider"/>, which the test advances past the
/// bound once it has armed. This is the only class in the assembly that runs a pipeline, so the
/// process-wide VoiceAi counters it reads see no one else's session; xunit runs its tests one at a
/// time. That is why the connect row lives here rather than beside the connect-bound tests.
/// </para>
/// </remarks>
public sealed class SttPipelineMuteVendorTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The ruled silence bound after the end of input.</summary>
    private static readonly TimeSpan EndOfInputBound = TimeSpan.FromSeconds(10);

    /// <summary>The connect bound of a recognizer built with default options.</summary>
    private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(5);

    public static TheoryData<string> Clients => RecognizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task HandleSessionAsync_ShouldReturnAndCountTheRecognitionFailed_WhenTheSttVendorNeverAnswersTheEndOfInput(string client)
    {
        // Arrange
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        using var meters = new VoiceAiMeters();

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(new FixedAnswerHandler());
        services.AddSingleton<ITurnDetector>(new OneUtteranceDetector());
        await using var provider = services.BuildServiceProvider();
        await using var pipeline = new VoiceAiPipeline(
            RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port, clock),
            new OneChunkSynthesizer(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VoiceAiPipelineOptions()),
            NullLogger<VoiceAiPipeline>.Instance);
        using var errors = new PipelineErrors(pipeline);
        await using var call = await AudioSocketCall.StartAsync();

        var sessionTask = pipeline.HandleSessionAsync(call.Session, CancellationToken.None).AsTask();
        for (var i = 0; i < 3; i++)
            await call.Caller.SendAudioAsync(new byte[320]);
        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);

        // Act — the caller hangs up while the recognizer waits on its vendor.
        await call.HangUpAsync();
        await AdvancePastTheArmedBoundAsync(clock, EndOfInputBound);
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull("the handler returns once the recognition it had in flight reached its bound");
            meters.Count("voiceai.sessions.completed").Should().Be(1, "a call that ends at the hangup is a completed session");
            meters.Count("voiceai.sessions.failed").Should().Be(0);
            meters.Records("voiceai.session.duration_ms").Should().Be(1, "the session's duration was recorded");
            meters.Count("stt.transcriptions.failed").Should().BeGreaterThanOrEqualTo(1, "the mute vendor failed the recognition");
        }

        var error = errors.Snapshot.Should().ContainSingle("one recognition was in flight").Subject;
        error.Source.Should().Be(PipelineErrorSource.Stt);
        error.Exception.Should().BeOfType<SpeechProviderFailureException>()
            .Which.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
    }

    /// <summary>
    /// Deepgram's upgrade is held unanswered. Before the connect bound, Deepgram had none: the turn
    /// waited on the dial after the caller hung up, and after the AudioSocket server stopped, and the
    /// session was never counted.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldReturnAndCountTheRecognitionFailed_WhenTheSttVendorNeverAnswersTheUpgrade()
    {
        // Arrange
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var clock = new FakeTimeProvider();
        using var meters = new VoiceAiMeters();

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(new FixedAnswerHandler());
        services.AddSingleton<ITurnDetector>(new OneUtteranceDetector());
        await using var provider = services.BuildServiceProvider();
        await using var pipeline = new VoiceAiPipeline(
            RecognizerEndOfInputPeers.CreateRecognizer("Deepgram", stalled.Port, clock),
            new OneChunkSynthesizer(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VoiceAiPipelineOptions()),
            NullLogger<VoiceAiPipeline>.Instance);
        using var errors = new PipelineErrors(pipeline);
        await using var call = await AudioSocketCall.StartAsync();

        var sessionTask = pipeline.HandleSessionAsync(call.Session, CancellationToken.None).AsTask();
        for (var i = 0; i < 3; i++)
            await call.Caller.SendAudioAsync(new byte[320]);
        await stalled.RequestReceived.WaitAsync(SignalTimeout);

        // Act — the caller hangs up while the recognizer's dial is held.
        await call.HangUpAsync();
        await AdvancePastTheArmedBoundAsync(clock, ConnectBound);
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull("the handler returns once the recognition it had in flight reached its connect bound");
            meters.Count("voiceai.sessions.completed").Should().Be(1, "a call that ends at the hangup is a completed session");
            meters.Count("voiceai.sessions.failed").Should().Be(0);
            meters.Records("voiceai.session.duration_ms").Should().Be(1, "the session's duration was recorded");
            meters.Count("stt.transcriptions.failed").Should().BeGreaterThanOrEqualTo(1, "the unanswered upgrade failed the recognition");
        }

        var error = errors.Snapshot.Should().ContainSingle("one recognition was in flight").Subject;
        error.Source.Should().Be(PipelineErrorSource.Stt);
        error.Exception.Should().BeOfType<SpeechProviderFailureException>()
            .Which.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
    }

    /// <summary>
    /// Waits for the recognizer to arm the bound of <paramref name="bound"/> on <paramref name="clock"/>,
    /// then moves the clock past it. The recognizer arms its connect bound (5 s) as it dials and its
    /// end-of-input bound (10 s) once its end of input is out, both on this clock, so each test names the
    /// one it runs out. Before that bound existed nothing arms it, and this times out with the handler
    /// still running.
    /// </summary>
    private static async Task AdvancePastTheArmedBoundAsync(FakeTimeProvider clock, TimeSpan bound)
    {
        TimeSpan due;
        do
        {
            due = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
        }
        while (due != bound);

        clock.Advance(due);
    }

    /// <summary>One AudioSocket call on a loopback server: the session the pipeline serves and the caller's end.</summary>
    private sealed class AudioSocketCall : IAsyncDisposable
    {
        private readonly AudioSocketServer _server;

        private AudioSocketCall(AudioSocketServer server, AudioSocketClient caller, AudioSocketSession session)
        {
            _server = server;
            Caller = caller;
            Session = session;
        }

        public AudioSocketClient Caller { get; }

        public AudioSocketSession Session { get; }

        public static async Task<AudioSocketCall> StartAsync()
        {
            var server = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
            var accepted = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnSessionStarted += s =>
            {
                accepted.TrySetResult(s);
                return ValueTask.CompletedTask;
            };
            await server.StartAsync(CancellationToken.None);

            // 127.0.0.1, never "localhost": the name resolves ::1 first on this host (ADR-0044).
            var caller = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
            await caller.ConnectAsync(CancellationToken.None);
            return new AudioSocketCall(server, caller, await accepted.Task.WaitAsync(SignalTimeout));
        }

        /// <summary>The caller hangs up.</summary>
        public async Task HangUpAsync()
        {
            try
            {
                await Caller.SendHangupAsync();
            }
            catch (IOException)
            {
                // The server already tore the session down, so the hangup frame could not be written;
                // the call is over either way.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Caller.DisposeAsync();
            await _server.StopAsync(CancellationToken.None);
            await _server.DisposeAsync();
        }
    }

    /// <summary>Every <see cref="PipelineErrorEvent"/> the pipeline publishes.</summary>
    private sealed class PipelineErrors : IObserver<VoiceAiPipelineEvent>, IDisposable
    {
        private readonly List<PipelineErrorEvent> _errors = [];
        private readonly Lock _gate = new();
        private readonly IDisposable _subscription;

        public PipelineErrors(VoiceAiPipeline pipeline) => _subscription = pipeline.Events.Subscribe(this);

        public IReadOnlyList<PipelineErrorEvent> Snapshot
        {
            get
            {
                lock (_gate)
                    return [.. _errors];
            }
        }

        public void OnNext(VoiceAiPipelineEvent value)
        {
            if (value is not PipelineErrorEvent error)
                return;

            lock (_gate)
                _errors.Add(error);
        }

        public void OnError(Exception error)
        {
            // The pipeline's subject never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // The pipeline completes its events when it is disposed; what was recorded stays.
        }

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>
    /// The pipeline's counters (<c>Verbara.Sdk.VoiceAi</c>) and the recognition counters
    /// (<c>Verbara.Sdk.VoiceAi.Stt</c>), summed, and how many times each histogram recorded.
    /// </summary>
    private sealed class VoiceAiMeters : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = [];
        private readonly Dictionary<string, int> _records = [];
        private readonly Lock _gate = new();

        public VoiceAiMeters()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name is "Verbara.Sdk.VoiceAi" or "Verbara.Sdk.VoiceAi.Stt")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                lock (_gate)
                    _counts[instrument.Name] = _counts.GetValueOrDefault(instrument.Name) + measurement;
            });
            _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            {
                lock (_gate)
                    _records[instrument.Name] = _records.GetValueOrDefault(instrument.Name) + 1;
            });
            _listener.Start();
        }

        public long Count(string instrument)
        {
            lock (_gate)
                return _counts.GetValueOrDefault(instrument);
        }

        public int Records(string instrument)
        {
            lock (_gate)
                return _records.GetValueOrDefault(instrument);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Speech on the first frame, the end of the utterance on the third, nothing after.</summary>
    private sealed class OneUtteranceDetector : ITurnDetector
    {
        private int _frames;

        public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
            => new(Interlocked.Increment(ref _frames) switch
            {
                1 => TurnAction.SpeechStarted,
                3 => TurnAction.EndOfUtterance,
                _ => TurnAction.Continue,
            });

        public void Reset() => Interlocked.Exchange(ref _frames, 0);
    }

    private sealed class FixedAnswerHandler : IConversationHandler
    {
        public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct = default)
            => ValueTask.FromResult("ok");
    }

    /// <summary>A healthy synthesizer: one 20 ms chunk, then the end.</summary>
    private sealed class OneChunkSynthesizer : SpeechSynthesizer
    {
        public override string ProviderName => "OneChunk";

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text, AudioFormat outputFormat, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new byte[320];
        }
    }
}
