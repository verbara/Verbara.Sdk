using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Every speech-recognition and speech-synthesis turn the pipeline starts ends in exactly one of
/// <c>completed</c>, <c>failed</c> or <c>cancelled</c>, whatever ends it, so an operator can compute
/// what is still in flight from the counters alone: per arm, once the session handler has returned,
/// <c>started = completed + failed + cancelled</c>. A turn somebody outside the provider cut short —
/// the token the session runs under, a barge-in, the pipeline's disposal, or the far end leaving
/// mid-playback — is <c>cancelled</c>, tagged <c>voiceai.ending</c> with what ended it, and
/// <c>completed</c> means the provider's work was used in full.
/// </summary>
/// <remarks>
/// <para>
/// The counters are read by instrument name through a <see cref="MeterListener"/>, never through the
/// static fields, so the <c>…cancelled</c> instruments are looked up the way an exporter finds them:
/// an instrument that does not exist reads as no measurement at all.
/// </para>
/// <para>
/// Nothing here waits on a clock to order a step. The turn detector says which frame it decided on, a
/// parked provider says it is parked, the session says it hung up, and the session handler's return is
/// observed as the pipeline's own <c>voiceai.sessions.*</c> measurement, which it records last. Every
/// wait is bounded by <see cref="SignalTimeout"/>, whose expiry is a failure, never a pace.
/// </para>
/// <para>
/// The broker rows stop the broker with a token the test cancels while the stop may still be running
/// (the stop is started, the token cancelled, and only then is the stop awaited), or dispose the broker
/// without a stop, so they hold whether or not a graceful stop waits for the handlers it started.
/// </para>
/// </remarks>
[Collection(SessionCounterGroup.Name)]
public sealed class VoiceAiPipelineTurnAccountingTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string Stt = "stt.transcriptions";
    private const string Tts = "tts.syntheses";

    /// <summary>The ways a session's turns end that the accounting must close over.</summary>
    public enum Ending
    {
        /// <summary>One utterance recognised, answered and played in full.</summary>
        NormalTurn,

        /// <summary>Two utterances: the recognizer fails the first, the synthesizer fails the second.</summary>
        ProviderFailureInEachArm,

        /// <summary>The synthesizer cancels a source of its own while nobody asked it to stop.</summary>
        SynthesizerCancelsItself,

        /// <summary>The caller speaks over the synthesis.</summary>
        BargeIn,

        /// <summary>The pipeline is disposed while a synthesis is in flight.</summary>
        PipelineDisposal,

        /// <summary>The caller hangs up and the next audio write finds the session ended.</summary>
        FarEndLeavesMidPlayback,

        /// <summary>The token handed to <c>HandleSessionAsync</c> is cancelled during recognition.</summary>
        HostCancelsDuringRecognition,

        /// <summary>The token handed to <c>HandleSessionAsync</c> is cancelled during synthesis.</summary>
        HostCancelsDuringSynthesis,

        /// <summary>The broker is stopped with a token already cancelled, during recognition.</summary>
        BrokerStopTokenCancelledDuringRecognition,

        /// <summary>The broker's stop token is cancelled and the broker disposed, during recognition.</summary>
        BrokerStoppedAndDisposedDuringRecognition,

        /// <summary>The broker's stop token is cancelled and the broker disposed, during synthesis.</summary>
        BrokerStoppedAndDisposedDuringSynthesis,

        /// <summary>The broker is disposed without a stop, during synthesis.</summary>
        BrokerDisposedDuringSynthesis,
    }

    public static TheoryData<Ending> EveryEnding()
    {
        var data = new TheoryData<Ending>();
        foreach (var ending in Enum.GetValues<Ending>())
            data.Add(ending);
        return data;
    }

    /// <summary>
    /// The invariant, over every ending: for each arm, the turns started equal the turns completed,
    /// failed and cancelled. Before the <c>…cancelled</c> counters, a turn whose provider call was in
    /// flight when the session's token was cancelled — by the host, or by the broker's stop or
    /// disposal — was counted started and nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryEnding))]
    public async Task HandleSessionAsync_ShouldCountEveryStartedTurnInExactlyOneEnding_WhateverEndsTheSession(Ending ending)
    {
        var outcome = await RunAsync(ending);

        using (new AssertionScope())
        {
            outcome.Fault.Should().BeNull("no ending here is a fault the handler rethrows");
            foreach (var arm in new[] { Stt, Tts })
            {
                var started = outcome.Sum($"{arm}.started");
                var completed = outcome.Sum($"{arm}.completed");
                var failed = outcome.Sum($"{arm}.failed");
                var cancelled = outcome.Sum($"{arm}.cancelled");
                var ended = completed + failed + cancelled;
                ended.Should().Be(
                    started,
                    "every {0} turn started ends in exactly one bucket once the handler has returned "
                    + "(ending {1}: started {2}, completed {3}, failed {4}, cancelled {5})",
                    arm, ending, started, completed, failed, cancelled);
            }

            outcome.Sum($"{Stt}.started").Should().BeGreaterThan(0, "every ending here starts a recognition");
        }
    }

    /// <summary>
    /// A turn somebody outside the provider cut short is counted cancelled, tagged with what ended it,
    /// and in no other ending: the host's or the broker's token (<c>session-cancelled</c>), a barge-in,
    /// the pipeline's disposal, or the far end leaving mid-playback. Each such turn still leaves the
    /// session completed, and publishes no pipeline error.
    /// </summary>
    [Theory]
    [InlineData(Ending.HostCancelsDuringRecognition, Stt, "session-cancelled")]
    [InlineData(Ending.HostCancelsDuringSynthesis, Tts, "session-cancelled")]
    [InlineData(Ending.BargeIn, Tts, "barge-in")]
    [InlineData(Ending.PipelineDisposal, Tts, "disposal")]
    [InlineData(Ending.FarEndLeavesMidPlayback, Tts, "far-end")]
    [InlineData(Ending.BrokerStopTokenCancelledDuringRecognition, Stt, "session-cancelled")]
    [InlineData(Ending.BrokerStoppedAndDisposedDuringRecognition, Stt, "session-cancelled")]
    [InlineData(Ending.BrokerStoppedAndDisposedDuringSynthesis, Tts, "session-cancelled")]
    [InlineData(Ending.BrokerDisposedDuringSynthesis, Tts, "session-cancelled")]
    public async Task HandleSessionAsync_ShouldCountTheTurnCancelledWithWhatEndedIt_WhenSomeoneOutsideTheProviderEndsIt(
        Ending ending, string arm, string expectedEnding)
    {
        var outcome = await RunAsync(ending);
        var otherArm = arm == Stt ? Tts : Stt;

        using (new AssertionScope())
        {
            outcome.Fault.Should().BeNull("a requested ending is not a fault");
            outcome.Measurements($"{arm}.cancelled").Should().Equal(
                new[] { $"1 voiceai.ending={expectedEnding}" },
                "the cut-short turn is counted cancelled once, tagged with what ended it ({0})", ending);
            outcome.Sum($"{arm}.completed").Should().Be(
                0, "a turn cut short is not completed: its provider's work was not used in full ({0})", ending);
            outcome.Sum($"{arm}.failed").Should().Be(0, "nobody's fault ended the turn ({0})", ending);
            outcome.Sum($"{otherArm}.cancelled").Should().Be(0, "only one turn was cut short ({0})", ending);
            outcome.Events.OfType<PipelineErrorEvent>().Should().BeEmpty("a requested ending publishes no error");
            outcome.Sum("voiceai.sessions.completed").Should().Be(1, "the session itself ended normally");
            outcome.Sum("voiceai.sessions.failed").Should().Be(0);

            if (ending is Ending.BargeIn or Ending.FarEndLeavesMidPlayback)
            {
                outcome.Events.OfType<SynthesisEndedEvent>().Should().ContainSingle(
                    "the synthesis still publishes its ending event as before");
            }
        }
    }

    /// <summary>
    /// The half that keeps the change from over-correcting: a synthesis whose audio was all written to
    /// a live session is completed, and nothing is counted cancelled.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCountTheSynthesisCompletedAndNothingCancelled_WhenItPlaysInFull()
    {
        var outcome = await RunAsync(Ending.NormalTurn);

        using (new AssertionScope())
        {
            outcome.Fault.Should().BeNull();
            outcome.Sum($"{Stt}.completed").Should().Be(1);
            outcome.Sum($"{Tts}.completed").Should().Be(1, "the whole answer was written to a live session");
            outcome.Measurements($"{Stt}.cancelled").Should().BeEmpty();
            outcome.Measurements($"{Tts}.cancelled").Should().BeEmpty();
            outcome.Events.OfType<SynthesisEndedEvent>().Should().ContainSingle();
        }
    }

    /// <summary>
    /// A provider's own failure stays a failure in its arm and is not counted cancelled.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldCountAProviderFailureFailedAndNothingCancelled_WhenEachArmFailsOnce()
    {
        var outcome = await RunAsync(Ending.ProviderFailureInEachArm);

        using (new AssertionScope())
        {
            outcome.Fault.Should().BeNull("a provider failure fails its turn, not the session");
            outcome.Sum($"{Stt}.started").Should().Be(2);
            outcome.Sum($"{Stt}.failed").Should().Be(1);
            outcome.Sum($"{Stt}.completed").Should().Be(1);
            outcome.Sum($"{Tts}.started").Should().Be(1);
            outcome.Sum($"{Tts}.failed").Should().Be(1);
            outcome.Sum($"{Tts}.completed").Should().Be(0);
            outcome.Measurements($"{Stt}.cancelled").Should().BeEmpty();
            outcome.Measurements($"{Tts}.cancelled").Should().BeEmpty();
            outcome.Events.OfType<PipelineErrorEvent>().Should().HaveCount(2);
        }
    }

    // ---- Harness ----

    /// <summary>What one session left behind: the counters, the events and any fault.</summary>
    private sealed record Outcome(
        IReadOnlyList<TaggedMeasurement> Recorded,
        IReadOnlyList<VoiceAiPipelineEvent> Events,
        Exception? Fault)
    {
        public long Sum(string instrument) =>
            Recorded.Where(m => m.Instrument == instrument).Sum(m => m.Value);

        /// <summary>Every measurement of <paramref name="instrument"/>, as "value tag=value,…".</summary>
        public IReadOnlyList<string> Measurements(string instrument) =>
            [.. Recorded.Where(m => m.Instrument == instrument).Select(m => $"{m.Value} {m.Tags}")];
    }

    private static async Task<Outcome> RunAsync(Ending ending)
    {
        using var meters = new TaggedMeterCapture();
        return ending switch
        {
            Ending.BrokerStopTokenCancelledDuringRecognition
                or Ending.BrokerStoppedAndDisposedDuringRecognition
                or Ending.BrokerStoppedAndDisposedDuringSynthesis
                or Ending.BrokerDisposedDuringSynthesis => await RunBehindTheBrokerAsync(ending, meters),
            _ => await RunDirectAsync(ending, meters),
        };
    }

    /// <summary>
    /// Runs one session by calling <c>HandleSessionAsync</c> directly, as a host with its own token does.
    /// </summary>
    private static async Task<Outcome> RunDirectAsync(Ending ending, TaggedMeterCapture meters)
    {
        var detector = ending switch
        {
            Ending.ProviderFailureInEachArm => new ScriptedTurnDetector(
                TurnAction.SpeechStarted, TurnAction.EndOfUtterance,
                TurnAction.SpeechStarted, TurnAction.EndOfUtterance),
            Ending.BargeIn => new ScriptedTurnDetector(
                TurnAction.SpeechStarted, TurnAction.EndOfUtterance, TurnAction.BargIn),
            _ => new ScriptedTurnDetector(TurnAction.SpeechStarted, TurnAction.EndOfUtterance),
        };

        var recognizer = ending switch
        {
            Ending.HostCancelsDuringRecognition => (SpeechRecognizer)new ParkingRecognizer(),
            Ending.ProviderFailureInEachArm => new FailsFirstRecognizer(),
            _ => new FakeSpeechRecognizer().WithTranscript("hola"),
        };

        await using var synthesizer = ending switch
        {
            Ending.ProviderFailureInEachArm => (SpeechSynthesizer)new FakeSpeechSynthesizer()
                .WithError(new InvalidOperationException("The synthesis provider failed.")),
            Ending.SynthesizerCancelsItself => new SelfCancellingSpeechSynthesizer(),
            Ending.BargeIn or Ending.PipelineDisposal or Ending.HostCancelsDuringSynthesis =>
                new ParkingSpeechSynthesizer(),
            Ending.FarEndLeavesMidPlayback => new KeepsSpeakingAfterTheParkSpeechSynthesizer(),
            _ => new FakeSpeechSynthesizer().WithAudio(new byte[320]),
        };

        await using var pipeline = BuildPipeline(recognizer, synthesizer, detector);
        using var events = new PipelineEventCapture(pipeline);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += s =>
        {
            accepted.TrySetResult(s);
            return ValueTask.CompletedTask;
        };
        await server.StartAsync(CancellationToken.None);

        // 127.0.0.1, never "localhost": the name resolves ::1 first on this host.
        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);
        var session = await accepted.Task.WaitAsync(SignalTimeout);

        // Subscribed before the session runs, so the far end's ending cannot be missed: the session's
        // teardown sets the flag its next write reads before it raises this event.
        TaskCompletionSource hungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => hungUp.TrySetResult();

        using var hostToken = new CancellationTokenSource();
        var sessionTask = pipeline.HandleSessionAsync(session, hostToken.Token).AsTask();

        await SpeakOneUtteranceAsync(client, detector, firstStep: 0);

        switch (ending)
        {
            case Ending.NormalTurn:
            case Ending.SynthesizerCancelsItself:
                await events.WaitForResponseCycle().WaitAsync(SignalTimeout);
                await client.SendHangupAsync();
                break;

            case Ending.ProviderFailureInEachArm:
                await SpeakOneUtteranceAsync(client, detector, firstStep: 2);
                await events.WaitFor<PipelineErrorEvent>(2).WaitAsync(SignalTimeout);
                await client.SendHangupAsync();
                break;

            case Ending.BargeIn:
                await ((ParkingSpeechSynthesizer)synthesizer).Parked.WaitAsync(SignalTimeout);
                await client.SendAudioAsync(VoiceFrame());
                await detector.Analyzed(2).WaitAsync(SignalTimeout);
                await events.WaitForResponseCycle().WaitAsync(SignalTimeout);
                await client.SendHangupAsync();
                break;

            case Ending.PipelineDisposal:
                await ((ParkingSpeechSynthesizer)synthesizer).Parked.WaitAsync(SignalTimeout);
                await pipeline.DisposeAsync();
                await client.SendHangupAsync();
                break;

            case Ending.FarEndLeavesMidPlayback:
                var keepsSpeaking = (KeepsSpeakingAfterTheParkSpeechSynthesizer)synthesizer;
                await keepsSpeaking.Parked.WaitAsync(SignalTimeout);
                await client.SendHangupAsync();
                await hungUp.Task.WaitAsync(SignalTimeout);
                keepsSpeaking.Release();
                break;

            case Ending.HostCancelsDuringRecognition:
                await ((ParkingRecognizer)recognizer).Parked.WaitAsync(SignalTimeout);
                await hostToken.CancelAsync();
                break;

            case Ending.HostCancelsDuringSynthesis:
                await ((ParkingSpeechSynthesizer)synthesizer).Parked.WaitAsync(SignalTimeout);
                await hostToken.CancelAsync();
                break;
        }

        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));
        return new Outcome(meters.Snapshot(), events.Events, fault);
    }

    /// <summary>
    /// Runs one session behind a real <see cref="VoiceAiSessionBroker"/>, the path the SDK's own
    /// registration takes, and ends it through the broker while a provider call is in flight.
    /// </summary>
    private static async Task<Outcome> RunBehindTheBrokerAsync(Ending ending, TaggedMeterCapture meters)
    {
        var duringRecognition = ending is Ending.BrokerStopTokenCancelledDuringRecognition
            or Ending.BrokerStoppedAndDisposedDuringRecognition;

        var detector = new ScriptedTurnDetector(TurnAction.SpeechStarted, TurnAction.EndOfUtterance);
        var recognizer = duringRecognition
            ? (SpeechRecognizer)new ParkingRecognizer()
            : new FakeSpeechRecognizer().WithTranscript("hola");
        await using var synthesizer = duringRecognition
            ? (SpeechSynthesizer)new FakeSpeechSynthesizer().WithAudio(new byte[320])
            : new ParkingSpeechSynthesizer();

        await using var pipeline = BuildPipeline(recognizer, synthesizer, detector);
        using var events = new PipelineEventCapture(pipeline);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        using var broker = new VoiceAiSessionBroker(server, pipeline, NullLogger<VoiceAiSessionBroker>.Instance);
        await broker.StartAsync(CancellationToken.None);
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);
        await SpeakOneUtteranceAsync(client, detector, firstStep: 0);

        if (duringRecognition)
            await ((ParkingRecognizer)recognizer).Parked.WaitAsync(SignalTimeout);
        else
            await ((ParkingSpeechSynthesizer)synthesizer).Parked.WaitAsync(SignalTimeout);

        switch (ending)
        {
            case Ending.BrokerStopTokenCancelledDuringRecognition:
            {
                using var stopToken = new CancellationTokenSource();
                await stopToken.CancelAsync();
                await broker.StopAsync(stopToken.Token).WaitAsync(SignalTimeout);
                break;
            }

            case Ending.BrokerStoppedAndDisposedDuringRecognition:
            case Ending.BrokerStoppedAndDisposedDuringSynthesis:
            {
                // Started and not awaited, then its token cancelled, then awaited: a stop that waits for
                // the handlers it started returns once the cancellation has reached them, and one that
                // does not wait has already returned.
                using var stopToken = new CancellationTokenSource();
                var stopping = broker.StopAsync(stopToken.Token);
                await stopToken.CancelAsync();
                await stopping.WaitAsync(SignalTimeout);
                broker.Dispose();
                break;
            }

            case Ending.BrokerDisposedDuringSynthesis:
                broker.Dispose();
                break;
        }

        // The pipeline records its session counter last, in the finally of HandleSessionAsync, so this
        // measurement is the session handler having returned.
        await meters.SessionEnded.WaitAsync(SignalTimeout);
        return new Outcome(meters.Snapshot(), events.Events, Fault: null);
    }

    /// <summary>Sends a speech frame and an end-of-utterance frame, each decided on before the next.</summary>
    private static async Task SpeakOneUtteranceAsync(
        AudioSocketClient client, ScriptedTurnDetector detector, int firstStep)
    {
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(firstStep).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(firstStep + 1).WaitAsync(SignalTimeout);
    }

    private static VoiceAiPipeline BuildPipeline(
        SpeechRecognizer recognizer, SpeechSynthesizer synthesizer, ITurnDetector detector)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(new FakeConversationHandler().WithResponse("respuesta"));
        services.AddSingleton(detector);
        var provider = services.BuildServiceProvider();

        return new VoiceAiPipeline(
            recognizer,
            synthesizer,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VoiceAiPipelineOptions()),
            NullLogger<VoiceAiPipeline>.Instance);
    }

    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (int i = 0; i < 160; i++)
        {
            const short sample = 5000;
            buf[i * 2] = unchecked((byte)(sample & 0xFF));
            buf[i * 2 + 1] = unchecked((byte)(sample >> 8));
        }
        return buf;
    }

    /// <summary>One counter measurement: the instrument's name, its value and its tags, sorted.</summary>
    private sealed record TaggedMeasurement(string Instrument, long Value, string Tags);

    /// <summary>
    /// Records every <c>long</c> measurement of the three voice meters with its tags, by instrument name.
    /// </summary>
    /// <remarks>
    /// The instruments are process-wide statics; the class runs in <see cref="SessionCounterGroup"/>, so
    /// what this records belongs to the test that created it. <see cref="SessionEnded"/> completes on the
    /// first <c>voiceai.sessions.completed</c> or <c>voiceai.sessions.failed</c> measurement, which the
    /// pipeline records in the <c>finally</c> of <c>HandleSessionAsync</c>, after every turn counter.
    /// </remarks>
    private sealed class TaggedMeterCapture : IDisposable
    {
        private static readonly string[] MeterNames =
            ["Verbara.Sdk.VoiceAi", "Verbara.Sdk.VoiceAi.Stt", "Verbara.Sdk.VoiceAi.Tts"];

        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<TaggedMeasurement> _recorded = [];
        private readonly TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaggedMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (MeterNames.Contains(instrument.Meter.Name))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(OnLong);
            _listener.Start();
        }

        public Task SessionEnded => _sessionEnded.Task;

        public IReadOnlyList<TaggedMeasurement> Snapshot()
        {
            lock (_gate) return [.. _recorded];
        }

        private void OnLong(Instrument instrument, long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var rendered = new List<string>(tags.Length);
            foreach (var tag in tags)
                rendered.Add($"{tag.Key}={tag.Value}");
            rendered.Sort(StringComparer.Ordinal);

            lock (_gate)
                _recorded.Add(new TaggedMeasurement(instrument.Name, measurement, string.Join(',', rendered)));

            if (instrument.Name is "voiceai.sessions.completed" or "voiceai.sessions.failed")
                _sessionEnded.TrySetResult();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Returns one scripted decision per frame and announces which frame it just decided on. The pipeline
    /// calls <c>Analyze</c> synchronously, once per frame, so "send one frame, wait for its signal" orders
    /// the test exactly. Frames past the end of the script are <see cref="TurnAction.Continue"/>.
    /// </summary>
    private sealed class ScriptedTurnDetector : ITurnDetector
    {
        private readonly TurnAction[] _script;
        private readonly TaskCompletionSource[] _analyzed;
        private int _index;

        public ScriptedTurnDetector(params TurnAction[] script)
        {
            _script = script;
            _analyzed = [.. script.Select(
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        }

        public Task Analyzed(int step) => _analyzed[step].Task;

        public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
        {
            var step = _index;
            if (step >= _script.Length)
                return new TurnSignal(TurnAction.Continue);

            _index = step + 1;
            _analyzed[step].TrySetResult();
            return new TurnSignal(_script[step]);
        }

        public void Reset() => _index = 0;
    }

    /// <summary>
    /// Consumes the utterance, says it is parked, then waits for a release that never comes, so the only
    /// way out of the recognition is the token it was handed.
    /// </summary>
    private sealed class ParkingRecognizer : SpeechRecognizer
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string ProviderName => "ParkingRecognizer";

        public Task Parked => _parked.Task;

        public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
            AudioFormat format,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            {
                // Draining the utterance is all a recognizer does before it waits for its provider.
            }

            _parked.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return new SpeechRecognitionResult("hola", 1f, true, TimeSpan.Zero);
        }
    }

    /// <summary>Fails its first recognition with a provider error and recognises every later one.</summary>
    private sealed class FailsFirstRecognizer : SpeechRecognizer
    {
        private int _calls;

        public override string ProviderName => "FailsFirst";

        public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
            AudioFormat format,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            {
                // The utterance is consumed either way; only the provider's answer differs.
            }

            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("The recognition provider failed.");

            yield return new SpeechRecognitionResult("hola", 1f, true, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Cancels a source of its own and raises that cancellation while the token it was handed is live:
    /// what an elapsed provider deadline looks like. The pipeline counts it a synthesis failure.
    /// </summary>
    private sealed class SelfCancellingSpeechSynthesizer : SpeechSynthesizer
    {
        private readonly CancellationTokenSource _ownDeadline = new();

        public override string ProviderName => "SelfCancelling";

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text,
            AudioFormat outputFormat,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await _ownDeadline.CancelAsync().ConfigureAwait(false);
            _ownDeadline.Token.ThrowIfCancellationRequested();
            yield break;
        }

        public override ValueTask DisposeAsync()
        {
            _ownDeadline.Dispose();
            return base.DisposeAsync();
        }
    }

    /// <summary>
    /// Yields one chunk, parks until released, then yields a second: the first is written to a live
    /// session, the second to one the far end has already left.
    /// </summary>
    private sealed class KeepsSpeakingAfterTheParkSpeechSynthesizer : SpeechSynthesizer
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string ProviderName => "KeepsSpeakingAfterThePark";

        public Task Parked => _parked.Task;

        public void Release() => _release.TrySetResult();

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text,
            AudioFormat outputFormat,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new byte[320];
            _parked.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield return new byte[320];
        }

        public override ValueTask DisposeAsync()
        {
            Release();
            return base.DisposeAsync();
        }
    }
}
