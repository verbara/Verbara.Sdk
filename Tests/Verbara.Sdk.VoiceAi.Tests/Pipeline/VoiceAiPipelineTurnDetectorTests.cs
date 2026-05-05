using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

public class VoiceAiPipelineTurnDetectorTests
{
    private static VoiceAiPipeline BuildPipelineWithDetector(
        ITurnDetector turnDetector,
        FakeSpeechRecognizer? stt = null,
        FakeSpeechSynthesizer? tts = null,
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
        var tts = new FakeSpeechSynthesizer()
            .WithDelay(TimeSpan.FromSeconds(2))
            .WithSilence(TimeSpan.FromMilliseconds(500));
        var handler = new FakeConversationHandler().WithResponses(["respuesta larga", "ok"]);

        var detector = new FakeTurnDetector()
            // First utterance: 3 frames
            .WithSignal(TurnAction.SpeechStarted)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.EndOfUtterance)
            // Wait frames during TTS start-up (these arrive while pipeline is in Speaking state)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.Continue)
            // Barge-in: user speaks over assistant
            .WithSignal(TurnAction.BargIn)
            .WithSignal(TurnAction.Continue)
            .WithSignal(TurnAction.EndOfUtterance);

        var pipeline = BuildPipelineWithDetector(detector, stt, tts, handler);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        // Act: send first utterance, wait for TTS to start, then send barge-in frames.
        await RunPipelineWithBargInSequence(pipeline);

        // Assert
        events.Should().Contain(e => e is BargInDetectedEvent);
    }

    private static async Task RunPipelineWithFrames(VoiceAiPipeline pipeline, int frameCount)
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int i = 0; i < frameCount; i++)
            await client.SendAudioAsync(new byte[320]);

        await Task.Delay(500);
        await client.SendHangupAsync();
        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithBargInSequence(VoiceAiPipeline pipeline)
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        // First utterance: 3 frames → triggers STT → handler → TTS (with 2s delay)
        for (int i = 0; i < 3; i++)
            await client.SendAudioAsync(new byte[320]);

        // Wait for TTS playback to begin (pipeline enters Speaking state)
        await Task.Delay(300);

        // Send frames during TTS: 3 Continue signals consumed, then BargIn + Continue + EndOfUtterance
        for (int i = 0; i < 6; i++)
        {
            await client.SendAudioAsync(new byte[320]);
            await Task.Delay(20);
        }

        await Task.Delay(500);
        await client.SendHangupAsync();
        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await server.StopAsync(CancellationToken.None);
    }
}
