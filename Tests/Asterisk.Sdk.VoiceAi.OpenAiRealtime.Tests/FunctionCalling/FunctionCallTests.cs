using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public sealed class FunctionCallTests
{
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
        bridge.BaseUri = new Uri($"ws://localhost:{fakeOpenAi.Port}/");
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
        var events = new List<RealtimeEvent>();
        using var sub = bridge.Events.Subscribe(e => events.Add(e));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("result") && m.Contains("100"));
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"response.create\""));
        events.OfType<RealtimeFunctionCalledEvent>()
            .Should().ContainSingle(e => e.FunctionName == "multiply");

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        // Result must contain error JSON — handler must not cause the bridge to throw
        fakeOpenAi.ReceivedMessages
            .Should().Contain(m => m.Contains("\"type\":\"conversation.item.create\"") && m.Contains("error"));

        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Bridge_UnknownFunction_DoesNotCrash()
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-x","name":"nonexistent","arguments":"{}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi, []); // no handlers

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();
        await client.SendHangupAsync();

        // Should complete without throwing
        try { await bridgeTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }

        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
    }
}
