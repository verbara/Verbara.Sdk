using System.Reactive.Linq;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

public sealed class OpenAiRealtimeBridgeTests
{
    [Fact]
    public async Task HandleSessionAsync_SendsSessionUpdate_OnConnect()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        try { await bridge.HandleSessionAsync(session, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert — the first client message should be session.update
        fakeOpenAi.ReceivedMessages.Should().ContainSingle(m => m.Contains("\"session.update\""));

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

        var events = new List<RealtimeEvent>();
        using var sub = bridge.Events.Subscribe(events.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        try { await bridge.HandleSessionAsync(session, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert
        events.Should().ContainSingle(e => e is RealtimeResponseStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeResponseEndedEvent);

        var ended = events.OfType<RealtimeResponseEndedEvent>().Single();
        ended.Duration.Should().BeGreaterThan(TimeSpan.Zero);

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

        var events = new List<RealtimeEvent>();
        using var sub = bridge.Events.Subscribe(events.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        try { await bridge.HandleSessionAsync(session, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert
        var transcripts = events.OfType<RealtimeTranscriptEvent>().ToList();
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

        var events = new List<RealtimeEvent>();
        using var sub = bridge.Events.Subscribe(events.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        try { await bridge.HandleSessionAsync(session, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert
        var errorEvent = events.OfType<RealtimeErrorEvent>().Should().ContainSingle().Subject;
        errorEvent.Message.Should().Be("rate limit exceeded");

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleSessionAsync_CancellationToken_TerminatesBothLoops()
    {
        // Arrange — server sends no events (keeps connection open until close)
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi);

        using var cts = new CancellationTokenSource();

        // Act — cancel after a short delay
        var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await Task.Delay(200);
        await cts.CancelAsync();

        // Assert — should complete (not hang)
        var completed = await Task.WhenAny(sessionTask, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().BeSameAs(sessionTask, "HandleSessionAsync should terminate when cancelled");

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

        var events = new List<RealtimeEvent>();
        using var sub = bridge.Events.Subscribe(events.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        try { await bridge.HandleSessionAsync(session, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert
        events.Should().ContainSingle(e => e is RealtimeSpeechStartedEvent);
        events.Should().ContainSingle(e => e is RealtimeSpeechStoppedEvent);

        // Cleanup
        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        bridge.BaseUri = new Uri($"ws://localhost:{fakeOpenAi.Port}/");
        return bridge;
    }
}
