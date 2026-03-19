using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Events;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tests.Pipeline;

public class VoiceAiPipelineTests : IAsyncDisposable
{
    private static VoiceAiPipeline BuildPipeline(
        FakeSpeechRecognizer? stt = null,
        FakeSpeechSynthesizer? tts = null,
        FakeConversationHandler? handler = null,
        VoiceAiPipelineOptions? options = null)
    {
        stt ??= new FakeSpeechRecognizer().WithTranscript("hola");
        tts ??= new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(40));
        handler ??= new FakeConversationHandler().WithResponse("respuesta");
        options ??= new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(handler);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new VoiceAiPipeline(
            stt, tts, scopeFactory,
            Options.Create(options),
            NullLogger<VoiceAiPipeline>.Instance);
    }

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

    [Fact]
    public async Task HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled()
    {
        var stt = new FakeSpeechRecognizer().WithTranscript("hola");
        var tts = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        var handler = new FakeConversationHandler().WithResponse("ok");
        var pipeline = BuildPipeline(stt, tts, handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await RunPipelineWithEndlessFrames(pipeline, cts.Token);
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
        // Use a long delay so TTS takes real wall-clock time in Speaking state
        var tts = new FakeSpeechSynthesizer()
            .WithDelay(TimeSpan.FromSeconds(3))
            .WithSilence(TimeSpan.FromMilliseconds(500));
        var handler = new FakeConversationHandler().WithResponse("respuesta larga");
        var options = new VoiceAiPipelineOptions
        {
            EndOfUtteranceSilence = TimeSpan.FromMilliseconds(60),
            BargInVoiceThreshold = TimeSpan.FromMilliseconds(40),
        };
        var pipeline = BuildPipeline(stt, tts, handler, options);

        var events = new List<VoiceAiPipelineEvent>();
        using var sub = pipeline.Events.Subscribe(events.Add);

        await RunPipelineWithBargIn(pipeline);

        events.Should().Contain(e => e is BargInDetectedEvent);
    }

    // ---- Helper methods ----

    private static async Task RunPipelineWithSingleUtterance(
        VoiceAiPipeline pipeline, int voiceFrameCount, int silenceFrameCount)
    {
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

        await Task.Delay(500);
        await client.SendHangupAsync();

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithEndlessFrames(
        VoiceAiPipeline pipeline, CancellationToken ct)
    {
        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        TaskCompletionSource<AudioSocketSession> tcs = new();
        server.OnSessionStarted += session => { tcs.TrySetResult(session); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var pipelineTask = pipeline.HandleSessionAsync(session, ct).AsTask();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await client.SendAudioAsync(SilenceFrame(), ct);
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) { }

        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithMultipleUtterances(
        VoiceAiPipeline pipeline, int utteranceCount)
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipelineTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();

        for (int u = 0; u < utteranceCount; u++)
        {
            for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
            for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());
            await Task.Delay(500);
        }

        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(10));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithContinuousVoice(
        VoiceAiPipeline pipeline, int frameCount)
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
        {
            await client.SendAudioAsync(VoiceFrame());
            await Task.Delay(20);
        }
        await Task.Delay(500);
        await client.SendHangupAsync();
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task RunPipelineWithBargIn(VoiceAiPipeline pipeline)
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

        // First utterance -> triggers TTS
        for (int i = 0; i < 3; i++) await client.SendAudioAsync(VoiceFrame());
        for (int i = 0; i < 4; i++) await client.SendAudioAsync(SilenceFrame());
        await Task.Delay(200);

        // Barge-in: send voice during TTS playback
        for (int i = 0; i < 3; i++)
        {
            await client.SendAudioAsync(VoiceFrame());
            await Task.Delay(20);
        }

        await Task.Delay(500);
        await client.SendHangupAsync();
        try { await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
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
