using System.Text.Json;
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

    private sealed class ThrowingFunction(Exception exception) : IRealtimeFunctionHandler
    {
        public ThrowingFunction(string message = "intentional failure")
            : this(new InvalidOperationException(message))
        {
        }

        public string Name => "boom";
        public string Description => "Always throws";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw exception;
    }

    /// <summary>
    /// An exception whose <see cref="Message"/> is null. The property is annotated as never null, but
    /// it is virtual, and nothing stops an override from returning null at run time.
    /// </summary>
    private sealed class MessagelessException : Exception
    {
        public override string Message => null!;
    }

    /// <summary>
    /// Handler exception messages, keyed by the shape they exercise, each with the exact
    /// <c>function_call_output</c> text the bridge must send for it. The key is the theory's argument
    /// rather than the message itself so that test names stay printable: several of these messages
    /// carry line breaks and control characters.
    /// </summary>
    private static readonly Dictionary<string, (string Message, string Output)> ThrownMessages = new(StringComparer.Ordinal)
    {
        // A JSON string cannot hold these as they are: each is escaped, and decodes back to the message.
        ["Backslash"] = ("file not found: C:\\temp\\new", """{"error":"file not found: C:\\temp\\new"}"""),
        ["TrailingBackslash"] = ("path ends in a separator \\", """{"error":"path ends in a separator \\"}"""),
        ["BackslashBeforeQuote"] = ("argument was \\\"quoted\\\" already", """{"error":"argument was \\\"quoted\\\" already"}"""),
        ["LineBreak"] = ("first line\r\nsecond line", """{"error":"first line\r\nsecond line"}"""),
        ["Tab"] = ("column\tvalue", """{"error":"column\tvalue"}"""),
        ["ControlCharacter"] = ("unit separator \u001F and bell \u0007", """{"error":"unit separator \u001F and bell \u0007"}"""),

        // Valid inside a JSON string as they are. Apart from the double quote, which JSON requires to be
        // escaped, these must reach the model exactly as the handler wrote them.
        ["Apostrophe"] = ("Customer's account can't be found", """{"error":"Customer's account can't be found"}"""),
        ["PlusSign"] = ("No agent available for +15551234567", """{"error":"No agent available for +15551234567"}"""),
        ["AngleBracketsAndAmpersand"] = ("retries must be > 0 & < 5", """{"error":"retries must be > 0 & < 5"}"""),
        ["DoubleQuote"] = ("say \"hi\"", """{"error":"say \"hi\""}"""),
        ["AccentedLetter"] = ("Cliente n\u00E3o encontrado", "{\"error\":\"Cliente n\u00E3o encontrado\"}"),

        // Also valid as they are, but the relaxed encoder still escapes every space separator other than
        // U+0020 (en-US short times carry U+202F) and every character outside the Basic Multilingual Plane.
        // Each decodes back to the message, and the model reads the escape text.
        ["NarrowNoBreakSpace"] = ("Agent back at 10:30\u202FAM", """{"error":"Agent back at 10:30\u202FAM"}"""),
        ["Emoji"] = ("Call dropped \uD83D\uDCDE", """{"error":"Call dropped \uD83D\uDCDE"}"""),
    };

    /// <summary>What one throwing function call produced on each surface a consumer can observe.</summary>
    private sealed record ThrowingCallResult(
        IReadOnlyList<string> ItemFrames,
        IReadOnlyList<RealtimeFunctionCalledEvent> Published,
        TaskStatus SessionStatus);

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

    /// <summary>
    /// Runs one session in which the model calls a handler that throws <paramref name="exception"/>,
    /// and returns what that call produced: the <c>conversation.item.create</c> frames OpenAI received,
    /// the <see cref="RealtimeFunctionCalledEvent"/>s the bridge published, and how the session ended.
    /// </summary>
    /// <remarks>
    /// The session is ended only once the output frame, the <c>response.create</c> that must follow it
    /// and the event have all arrived, and then by cancelling its token, which a session the call left
    /// healthy answers by completing. The status is returned rather than awaited so that a faulted
    /// session reads as a failed assertion, not as an exception thrown out of the arrangement.
    /// </remarks>
    private static async Task<ThrowingCallResult> CallThrowingFunctionAsync(Exception exception)
    {
        await using var fakeOpenAi = new RealtimeFakeServer();
        fakeOpenAi.EventsToSend.Add(
            """{"type":"response.function_call_arguments.done","call_id":"call-err","name":"boom","arguments":"{}"}""");
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        await using var bridge = CreateBridge(fakeOpenAi, [new ThrowingFunction(exception)]);

        using var collector = new RealtimeEventCollector(
            bridge.Events, e => e.OfType<RealtimeFunctionCalledEvent>().Any());
        using var cts = new CancellationTokenSource();

        var bridgeTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
        await Task.WhenAll(
            fakeOpenAi.WaitForClientFrameAsync("\"type\":\"conversation.item.create\""),
            fakeOpenAi.WaitForClientFrameAsync("\"type\":\"response.create\""),
            collector.Satisfied).WaitAsync(SignalTimeout);

        await cts.CancelAsync();
        await Task.WhenAny(bridgeTask).WaitAsync(SignalTimeout);

        var result = new ThrowingCallResult(
            fakeOpenAi.ReceivedMessages
                .Where(m => m.Contains("\"type\":\"conversation.item.create\"", StringComparison.Ordinal))
                .ToArray(),
            collector.Events.OfType<RealtimeFunctionCalledEvent>().ToArray(),
            bridgeTask.Status);

        await client.SendHangupAsync();
        await client.DisposeAsync();
        await audioServer.StopAsync(CancellationToken.None);
        return result;
    }

    /// <summary>The <c>function_call_output</c> text as OpenAI decodes it from a <c>conversation.item.create</c> frame.</summary>
    private static string? OutputOf(string itemFrame)
    {
        using var frameDocument = JsonDocument.Parse(itemFrame);
        return frameDocument.RootElement.GetProperty("item").GetProperty("output").GetString();
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

    /// <summary>
    /// The <c>function_call_output</c> the bridge sends for a throwing handler must be a JSON object
    /// whose <c>error</c> is the handler's message, character for character, written without escapes
    /// the model would have to read past — on the wire and on the
    /// <see cref="RealtimeFunctionCalledEvent"/> it publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output used to be spliced together by hand with only the double quote escaped. A backslash
    /// then read as the start of an escape sequence: <c>\t</c> and <c>\n</c> silently became a tab and
    /// a line break, and a trailing one swallowed the closing quote. A raw line break, tab or other
    /// control character is not allowed inside a JSON string at all. Either way OpenAI received an
    /// output that did not parse, or that parsed to a different message.
    /// </para>
    /// <para>
    /// The exact text is pinned as well as the decoded message because the model reads the text as
    /// written; nothing decodes it a second time. An HTML-safe encoder would still produce valid JSON,
    /// but would hand the model <c>\u0027</c> for every apostrophe and <c>\u002B</c> for every plus
    /// sign in messages that were readable as they were. The relaxed encoder still escapes space
    /// separators other than U+0020 and characters outside the Basic Multilingual Plane; the last two
    /// rows pin that as well.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Backslash")]
    [InlineData("TrailingBackslash")]
    [InlineData("BackslashBeforeQuote")]
    [InlineData("LineBreak")]
    [InlineData("Tab")]
    [InlineData("ControlCharacter")]
    [InlineData("Apostrophe")]
    [InlineData("PlusSign")]
    [InlineData("AngleBracketsAndAmpersand")]
    [InlineData("DoubleQuote")]
    [InlineData("AccentedLetter")]
    [InlineData("NarrowNoBreakSpace")]
    [InlineData("Emoji")]
    public async Task HandleSessionAsync_ShouldSendTheExactMessageAsReadableJson_WhenAFunctionHandlerThrows(string shape)
    {
        // Arrange
        var (message, expectedOutput) = ThrownMessages[shape];

        // Act
        var call = await CallThrowingFunctionAsync(new InvalidOperationException(message));

        // Assert — what OpenAI receives
        var output = OutputOf(call.ItemFrames.Should().ContainSingle().Subject);
        output.Should().NotBeNull();
        Func<JsonDocument> parseOutput = () => JsonDocument.Parse(output!);
        using (var outputDocument = parseOutput.Should().NotThrow("the function_call_output sent to OpenAI must be JSON").Subject)
        {
            outputDocument.RootElement.GetProperty("error").GetString()
                .Should().Be(message, "the output must carry the handler's message unchanged");
        }

        output.Should().Be(expectedOutput, "the model reads the output as written, so the exact escapes it holds are pinned");

        // Assert — what the bridge publishes about the same call, and the session it leaves behind
        call.Published.Should().ContainSingle()
            .Which.ResultJson.Should().Be(output, "the event reports the result that was sent");
        call.SessionStatus.Should().Be(TaskStatus.RanToCompletion, "a handler's exception must not fault the session");
    }

    /// <summary>
    /// A handler exception whose <see cref="Exception.Message"/> is null must still be answered with an
    /// error the model can read, and must leave the session running.
    /// </summary>
    /// <remarks>
    /// Splicing a null message into a string threw inside the catch block, so the exception escaped the
    /// function call: OpenAI never received an output or a <c>response.create</c> for it, no later server
    /// event was processed, and the session ended faulted. Serializing the null as it is would instead
    /// send <c>{"error":null}</c>, which reads as a call that did not fail, so the bridge sends the
    /// exception's type name.
    /// </remarks>
    [Fact]
    public async Task HandleSessionAsync_ShouldSendTheExceptionTypeName_WhenAFunctionHandlerThrowsWithoutAMessage()
    {
        // Act
        var call = await CallThrowingFunctionAsync(new MessagelessException());

        // Assert
        var output = OutputOf(call.ItemFrames.Should().ContainSingle().Subject);
        output.Should().Be("""{"error":"MessagelessException"}""", "an error without a message must still read as an error");
        call.Published.Should().ContainSingle()
            .Which.ResultJson.Should().Be(output, "the event reports the result that was sent");
        call.SessionStatus.Should().Be(TaskStatus.RanToCompletion, "a handler's exception must not fault the session");
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
