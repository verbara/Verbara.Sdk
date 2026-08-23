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
public class VoiceAiPipelineTurnDetectorTests
{
    /// <summary>
    /// Bounds every harness wait. Reaching it means the signal never arrived.
    /// </summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static VoiceAiPipeline BuildPipelineWithDetector(
        ITurnDetector turnDetector,
        FakeSpeechRecognizer? stt = null,
        SpeechSynthesizer? tts = null,
        FakeConversationHandler? handler = null)
    {
        stt ??= new FakeSpeechRecognizer().WithTranscript("hola");
        tts ??= new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        handler ??= new FakeConversationHandler().WithResponse("respuesta");

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        services.AddSingleton<ITurnDetector>(turnDetector);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };

        return new VoiceAiPipeline(
            stt, tts, scopeFactory,
            Options.Create(options),
            NullLogger<VoiceAiPipeline>.Instance);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldUseFakeTurnDetector_WhenRegisteredInDi()
    {
        // Arrange: detector returns SpeechStarted → Continue → EndOfUtterance,
        // then defaults to Continue for remaining frames.
        var detector = new FakeTurnDetector()
            .WithSignal(TurnAction.SpeechStarted)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.EndOfUtterance);

        var handler = new FakeConversationHandler().WithResponse("ok");
        var pipeline = BuildPipelineWithDetector(detector, handler: handler);

        // Act
        await RunPipelineWithFrames(pipeline, frameCount: 4);

        // Assert: detector was called at least once per frame sent,
        // and the handler processed the utterance.
        detector.CallCount.Should().BeGreaterOrEqualTo(3);
        handler.CallCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEmitBargInEvent_WhenDetectorSignalsBargIn()
    {
        // Arrange: first utterance (SpeechStarted → Continue → EndOfUtterance),
        // then BargIn during TTS playback, then close out the barge-in utterance.
        var stt = new FakeSpeechRecognizer().WithTranscripts(["interrumpe", "barge"]);
        var tts = new ParkingSpeechSynthesizer();
        var handler = new FakeConversationHandler().WithResponses(["respuesta larga", "ok"]);

        // Six signals, one per frame the harness sends. The three Continues that used to sit in the
        // middle were not part of the scenario: they absorbed whatever frames arrived during a
        // 300 ms delay, and a script padded to match a clock describes the clock, not the scenario.
        var detector = new FakeTurnDetector()
            // First utterance: 3 frames
            .WithSignal(TurnAction.SpeechStarted)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.EndOfUtterance)
            // Barge-in: user speaks over the assistant, then closes the utterance out
            .WithSignal(TurnAction.BargIn)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.EndOfUtterance);

        var pipeline = BuildPipelineWithDetector(detector, stt, tts, handler);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        // Act: send the first utterance, wait until the assistant is provably mid-sentence, then
        // send the barge-in frames.
        await RunPipelineWithBargInSequence(pipeline, tts);

        // Assert
        events.Should().Contain(e => e is BargInDetectedEvent);
    }

    private static async Task RunPipelineWithFrames(VoiceAiPipeline pipeline, int frameCount)
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

        for (int i = 0; i < frameCount; i++)
            await client.SendAudioAsync(new byte[320]);

        // The scripted EndOfUtterance on frame 3 starts a response cycle; it is over before the
        // hangup. The fourth frame needs no signal of its own -- nothing asserts on it.
        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);

        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(SignalTimeout);
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithBargInSequence(
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

        // First utterance: 3 frames -> STT -> handler -> TTS
        for (int i = 0; i < 3; i++)
            await client.SendAudioAsync(new byte[320]);

        // The synthesis has yielded a chunk and parked, so the pipeline is in Speaking state and
        // stays there. Sending the barge-in before this point would cancel a synthesis that had not
        // started; the 300 ms delay this replaces was betting it had.
        await tts.Parked.WaitAsync(SignalTimeout);

        // BargIn + Continue + EndOfUtterance, one frame per remaining script signal.
        for (int i = 0; i < 3; i++)
            await client.SendAudioAsync(new byte[320]);

        await capture.WaitFor<BargInDetectedEvent>().WaitAsync(SignalTimeout);

        // Ends the park so the barge-in utterance's own synthesis can finish rather than deadlock.
        tts.Release();

        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(SignalTimeout);
        await server.StopAsync(CancellationToken.None);
    }
}
