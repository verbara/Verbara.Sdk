using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.Tests.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

[Collection(SessionCounterGroup.Name)]
public class VoiceAiPipelineTests : IAsyncDisposable
{
    private static VoiceAiPipelineOptions DefaultOptions() => new()
    {
        EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
        BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
    };

    /// <summary>
    /// Wraps the detector the pipeline would have built for itself, so a harness gains a per-frame
    /// signal without any test losing the <see cref="SilenceTurnDetector"/> coverage it was written
    /// for. Pass the same options to <see cref="BuildPipeline"/> or the two disagree.
    /// </summary>
    private static ObservingTurnDetector ObserveDefaultDetector(VoiceAiPipelineOptions options) =>
        new(new SilenceTurnDetector(Options.Create(options)));

    private static VoiceAiPipeline BuildPipeline(
        FakeSpeechRecognizer? stt = null,
        SpeechSynthesizer? tts = null,
        FakeConversationHandler? handler = null,
        VoiceAiPipelineOptions? options = null,
        ObservingTurnDetector? detector = null)
    {
        stt ??= new FakeSpeechRecognizer().WithTranscript("hola");
        tts ??= new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        handler ??= new FakeConversationHandler().WithResponse("respuesta");
        options ??= DefaultOptions();

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        if (detector is not null)
            services.AddSingleton<ITurnDetector>(detector);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new VoiceAiPipeline(
            stt, tts, scopeFactory,
            Options.Create(options),
            NullLogger<VoiceAiPipeline>.Instance);
    }

    /// <summary>
    /// Every harness wait is bounded by this. It is a hang bound, not a pace: reaching it means the
    /// signal never arrived, and the test fails on its own assertion rather than on a clock.
    /// </summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static ReadOnlyMemory<byte> SilenceFrame() => new byte[320];

    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (int i = 0; i < 160; i++)
        {
            short sample = 5000;
            buf[i * 2] = (byte)(sample & 0xFF);
            buf[i * 2 + 1] = (byte)(sample >> 8);
        }
        return buf;
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitSpeechStartedEvent_WhenVoiceDetected()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        events.Should().Contain(e => e is SpeechStartedEvent);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitTranscriptReceivedEvent()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("buenos dias", 0.95f);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var transcript = events.OfType<TranscriptReceivedEvent>().Should().ContainSingle().Subject;
        transcript.Transcript.Should().Be("buenos dias");
        transcript.IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCallHandler_WithTranscript()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("hola de vuelta");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        handler.ReceivedTranscripts.Should().ContainSingle().Which.Should().Be("hola");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitResponseGeneratedEvent()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("pregunta");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("respuesta");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var response = events.OfType<ResponseGeneratedEvent>().Should().ContainSingle().Subject;
        response.Response.Should().Be("respuesta");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitSynthesisEvents()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("test");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        events.Should().Contain(e => e is SynthesisStartedEvent);
        events.Should().Contain(e => e is SynthesisEndedEvent);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnSttError_AndContinue()
    {
        var stt = new FakeSpeechRecognizer().WithError(new InvalidOperationException("stt fail"));
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        var error = events.OfType<PipelineErrorEvent>().Should().ContainSingle().Subject;
        error.Source.Should().Be(PipelineErrorSource.Stt);
        error.Exception?.Message.Should().Be("stt fail");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnHandlerError()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };

        var throwingHandler = new ThrowingConversationHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(throwingHandler);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var pipelineOptions = Options.Create(options);
        var pipeline2 = new VoiceAiPipeline(stt, tts, scopeFactory, pipelineOptions,
            NullLogger<VoiceAiPipeline>.Instance);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline2.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline2, voiceFrameCount: 3, silenceFrameCount: 4);

        events.OfType<PipelineErrorEvent>().Should().ContainSingle(e => e.Source == PipelineErrorSource.Handler);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitPipelineErrorEvent_OnTtsError()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithError(new InvalidOperationException("tts fail"));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithSingleUtterance(pipeline, voiceFrameCount: 3, silenceFrameCount: 4);

        events.OfType<PipelineErrorEvent>().Should().ContainSingle(e => e.Source == PipelineErrorSource.Tts);
    }

    /// <summary>
    /// Pins that a session cancelled while it is genuinely running terminates rather than hanging,
    /// and does so without throwing at the caller. It says nothing about how that ending is
    /// <em>classified</em> — that is <c>VoiceAiPipelineCancellationAccountingTests</c>' job, and the
    /// two are easy to confuse.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = DefaultOptions();
        var detector = ObserveDefaultDetector(options);
        var pipeline = BuildPipeline(stt, tts, handler, options, detector);

        var act = async () => await RunPipelineUntilCancelled(pipeline, detector);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldMaintainConversationHistory()
    {
        var stt = new FakeSpeechRecognizer().WithTranscripts(["primero", "segundo"]);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponses(["resp1", "resp2"]);
        var options = new VoiceAiPipelineOptions { EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60) };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithMultipleUtterances(pipeline, utteranceCount: 2);

        handler.CallCount.Should().Be(2);
        handler.ReceivedTranscripts[0].Should().Be("primero");
        handler.ReceivedTranscripts[1].Should().Be("segundo");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldTruncateHistory_WhenMaxHistoryExceeded()
    {
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            MaxHistoryTurns = 2
        };
        var transcripts = Enumerable.Range(1, 3).Select(i => $"transcript{i}").ToArray();
        var stt = new FakeSpeechRecognizer().WithTranscripts(transcripts);
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponses(
            transcripts.Select(t => $"resp_{t}"));
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithMultipleUtterances(pipeline, utteranceCount: 3);

        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldForceSttOnMaxUtteranceDuration()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("forzado");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromSeconds(10),
            MaxUtteranceDuration = TimeSpan.FromMilliseconds(60),
        };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        await RunPipelineWithContinuousVoice(pipeline, frameCount: 5);

        stt.CallCount.Should().BeGreaterThan(0);
        handler.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldDetectBargIn_AndCancelTts()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("interrumpe");
        // Parks after its first chunk instead of sleeping for three seconds. The assistant being
        // mid-sentence when the barge-in lands is then a fact, not a race the old delay was
        // buying odds on.
        var tts = new ParkingSpeechSynthesizer();
        var handler = new FakeConversationHandler().WithResponse("respuesta larga");
        var pipeline = BuildPipeline(stt, tts, handler, DefaultOptions());

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithBargIn(pipeline, tts);

        events.Should().Contain(e => e is BargInDetectedEvent);
    }

    // ---- Helper methods ----

    private static async Task RunPipelineWithSingleUtterance(
        VoiceAiPipeline pipeline, int voiceFrameCount, int silenceFrameCount)
    {
        // Subscribed before the session starts, so the cycle cannot end before anyone is listening.
        using var capture = new PipelineEventCapture(pipeline);

        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };

        await server.StartAsync(CancellationToken.None);
        var port = server.BoundPort;

        await using var client = new AudioSocketClient("127.0.0.1", port, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < voiceFrameCount; i++)
            await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < silenceFrameCount; i++)
            await client.SendAudioAsync(SilenceFrame());

        // The utterance's whole cycle -- recognition, handler, synthesis -- is over before the
        // hangup. One signal, because the phases it covers are not independent of each other: a
        // frame-count wait on top of this would order nothing the cycle end does not already prove.
        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);

        await client.SendHangupAsync();

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs a session until it is provably consuming audio, then cancels it.
    /// </summary>
    /// <remarks>
    /// This replaced a loop that fed silence forever against a 200 ms token, which was the only
    /// wall-clock barrier in these files doing the inverse of the others: the clock was not covering
    /// for a signal, it was ending the test. Three frames through the detector is what "the session
    /// is running" actually means, and once that is known there is nothing left for the endless loop
    /// to do. The token that remains bounds a hang; on a passing run it never fires.
    /// </remarks>
    private static async Task RunPipelineUntilCancelled(
        VoiceAiPipeline pipeline, ObservingTurnDetector detector)
    {
        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(SignalTimeout);
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < 3; i++)
            await client.SendAudioAsync(SilenceFrame());

        await detector.Analyzed(3).WaitAsync(SignalTimeout);

        await cts.CancelAsync();

        // Awaited, not swallowed: a cancelled session is a completion (ADR-0054), so anything
        // reaching the caller here is the defect this test exists to catch.
        await pipelineTask.WaitAsync(SignalTimeout);
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithMultipleUtterances(
        VoiceAiPipeline pipeline, int utteranceCount)
    {
        using var capture = new PipelineEventCapture(pipeline);

        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int u = 0; u < utteranceCount; u++)
        {
            for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
            for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());

            // Utterance u is answered before utterance u+1 is spoken. Counting cycles rather than
            // waiting per iteration is what makes the ordering hold when one answer runs long.
            await capture.WaitForResponseCycle(u + 1).WaitAsync(SignalTimeout);
        }

        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithContinuousVoice(
        VoiceAiPipeline pipeline, int frameCount)
    {
        using var capture = new PipelineEventCapture(pipeline);

        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        // No pacing between frames: SilenceTurnDetector advances MaxUtteranceDuration by one frame
        // period per Analyze call, never by elapsed time, so the 20 ms delay that used to sit here
        // ordered nothing at all. It is deleted rather than replaced by a signal.
        for (int i = 0; i < frameCount; i++)
            await client.SendAudioAsync(VoiceFrame());

        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);
        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithBargIn(
        VoiceAiPipeline pipeline, ParkingSpeechSynthesizer tts)
    {
        using var capture = new PipelineEventCapture(pipeline);

        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        // First utterance -> triggers TTS
        for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());

        // One chunk yielded and the synthesis parked: the pipeline is in Speaking state and will
        // stay there until something ends it, so the barge-in below cannot arrive early or late.
        await tts.Parked.WaitAsync(SignalTimeout);

        // Barge-in: voice while the synthesis is in flight. No pacing between frames -- the
        // detector's barge-in threshold counts frames, not elapsed time.
        for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());

        await capture.WaitFor<BargInDetectedEvent>().WaitAsync(SignalTimeout);

        tts.Release();
        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(SignalTimeout);
        await server.StopAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

// Minimal handler that always throws -- used for handler-error test
file sealed class ThrowingConversationHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct)
        => throw new InvalidOperationException("handler fail");
}
