using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public sealed class FunctionCallTests
{
    /// <summary>
    /// Upper bound on any single wait below, and the only clock in this class. Reaching it is a
    /// failure, never a pace: every wait here is on a signal that arrives in milliseconds over a
    /// loopback socket, and the session token carries no timer at all. Each bridge test waits on the
    /// frame or event it asserts on, then cancels the token explicitly to end the session — the
    /// same shape, and for the same reasons, as <c>OpenAiRealtimeBridgeTests.SignalTimeout</c>,
    /// whose remarks record why the session is not ended by hanging up.
    /// </summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // ── Shared test implementations ─────────────────────────────────────────

    private sealed class AddFunction : IRealtimeFunctionHandler
    {
        public string Name => "add";
        public string Description => "Adds two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":42}""");
    }

    private sealed class MultiplyFunction : IRealtimeFunctionHandler
    {
        public string Name => "multiply";
        public string Description => "Multiplies two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"required":["x","y"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":100}""");
    }

    private sealed class ThrowingFunction : IRealtimeFunctionHandler
    {
        public string Name => "boom";
        public string Description => "Always throws";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new InvalidOperationException("intentional failure");
    }

    // ── Registry unit tests (from Task 5) ───────────────────────────────────

    [Fact]
    public void Registry_TryGetHandler_ReturnsRegisteredHandler()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("add", out var handler);

        found.Should().BeTrue();
        handler.Should().NotBeNull();
        handler!.Name.Should().Be("add");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseForUnknown()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("unknown", out var handler);

        found.Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Registry_AllHandlers_ContainsRegisteredHandlers()
    {
        var handler = new AddFunction();
        var registry = new RealtimeFunctionRegistry([handler]);

        registry.AllHandlers.Should().ContainSingle()
            .Which.Name.Should().Be("add");
    }

    // ── Bridge integration tests (new in Task 10) ────────────────────────────

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
        IEnumerable<IRealtimeFunctionHandler> handlers)
    {
        var options = Options.Create(new OpenAiRealtimeOptions
        {
            ApiKey = "test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "alloy",
            InputFormat = AudioFormat.Slin16Mono8kHz,
        });
        var registry = new RealtimeFunctionRegistry(handlers);
        var bridge = new OpenAiRealtimeBridge(options, registry, NullLogger<OpenAiRealtimeBridge>.Instance);
        bridge.BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/");
        return bridge;
    }

    [Fact]
    public async Task Bridge_ExecutesFunction_AndSendsResultToServer()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-1","name":"multiply","arguments":"{\"x\":10,\"y\":10}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi, [new MultiplyFunction()]);

        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeFunctionCalledEvent>().Any());
        using var cts = new CancellationTokenSource();

        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        // Wait for all three signals this test asserts on. The two frames are captured on the fake's
        // receive loop and the event is published on the bridge's OutputLoop, so they are independent
        // arrivals: waiting for one and asserting the others is the race this change removes.
        await Task.WhenAll(
            fakeOpenAi.WaitForClientFrameAsync("\"type\":\"conversation.item.create\""),
            fakeOpenAi.WaitForClientFrameAsync("\"type\":\"response.create\""),
            collector.Satisfied).WaitAsync(SignalTimeout);

        await cts.CancelAsync();
        await bridgeTask.WaitAsync(SignalTimeout);

        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("result") && m.Contains("100"));
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"response.create\""));
        collector.Events.OfType<RealtimeFunctionCalledEvent>()
            .Should().ContainSingle(e => e.FunctionName == "multiply");

        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_FunctionThrows_SendsErrorJsonToServer()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-err","name":"boom","arguments":"{}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi, [new ThrowingFunction()]);

        using var cts = new CancellationTokenSource();

        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        // End on the frame under assertion — the handler's exception must still produce a result item.
        await fakeOpenAi.WaitForClientFrameAsync("\"type\":\"conversation.item.create\"")
            .WaitAsync(SignalTimeout);

        await cts.CancelAsync();
        await bridgeTask.WaitAsync(SignalTimeout);

        // Result must contain error JSON — handler must not cause the bridge to throw
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("error"));

        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_UnknownFunction_DoesNotCrash()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-x","name":"nonexistent","arguments":"{}"}""");

        // The positive sentinel for an absence assertion. An unknown function is answered with
        // nothing at all — the bridge logs it and returns — so there is no frame to wait for, and
        // "did not crash" measured against no signal is satisfied by a bridge that never ran.
        // This second event travels the same socket behind the first, and OutputLoop awaits each
        // handler before reading the next message, so its RealtimeResponseEndedEvent is proof that
        // the unknown call was processed and the loop came out the other side.
        fakeOpenAi.EventsToSend.Add("""{"type":"response.done"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi, []); // no handlers

        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeResponseEndedEvent>().Any());
        using var cts = new CancellationTokenSource();

        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await collector.Satisfied.WaitAsync(SignalTimeout);

        await cts.CancelAsync();
        await bridgeTask.WaitAsync(SignalTimeout);

        bridgeTask.Status.Should().Be(TaskStatus.RanToCompletion);
        bridgeTask.Exception.Should().BeNull();

        // Nothing was sent back for the unknown call — the branch under test is the silent one.
        fakeOpenAi.ReceivedMessages
            .Should().NotContain(m => m.Contains("\"call_id\":\"call-x\""));

        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }
}
