using System.Text.Json;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

public sealed class RealtimeMessageSerializationTests
{
    // Test-only lookup keeps the tests-first commit buildable before the helper exists. There is
    // no local options copy or fallback: every contract assertion reads the shipped For<T> field.
    private static JsonTypeInfo<T> StrictInfo<T>()
    {
        // Literal closed type names let the trim/AOT analyzers follow this test seam too;
        // MakeGenericType would require runtime code generation. Product code uses direct fields.
        var cache = typeof(T) == typeof(ServerEventBase)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.ServerEventBase, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : typeof(T) == typeof(ResponseAudioDeltaEvent)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.ResponseAudioDeltaEvent, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : typeof(T) == typeof(ResponseAudioTranscriptDeltaEvent)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.ResponseAudioTranscriptDeltaEvent, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : typeof(T) == typeof(ResponseAudioTranscriptDoneEvent)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.ResponseAudioTranscriptDoneEvent, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : typeof(T) == typeof(FunctionCallArgumentsDoneEvent)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.FunctionCallArgumentsDoneEvent, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : typeof(T) == typeof(ServerErrorEvent)
            ? Type.GetType("Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.RealtimeJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal.ServerErrorEvent, Verbara.Sdk.VoiceAi.OpenAiRealtime]], Verbara.Sdk.VoiceAi.OpenAiRealtime")
            : throw new InvalidOperationException("Unsupported DTO type in the strict-read test seam.");
        cache.Should().NotBeNull("the shipped read fence must exist");
        var field = cache!.GetField("TypeInfo", BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (JsonTypeInfo<T>)field!.GetValue(null)!;
    }

    [Theory]
    [InlineData("ServerEventBase", "type")]
    [InlineData("ResponseAudioDeltaEvent", "type")]
    [InlineData("ResponseAudioDeltaEvent", "delta")]
    [InlineData("ResponseAudioTranscriptDeltaEvent", "type")]
    [InlineData("ResponseAudioTranscriptDeltaEvent", "delta")]
    [InlineData("ResponseAudioTranscriptDoneEvent", "type")]
    [InlineData("ResponseAudioTranscriptDoneEvent", "transcript")]
    [InlineData("FunctionCallArgumentsDoneEvent", "type")]
    [InlineData("FunctionCallArgumentsDoneEvent", "call_id")]
    [InlineData("FunctionCallArgumentsDoneEvent", "name")]
    [InlineData("FunctionCallArgumentsDoneEvent", "arguments")]
    [InlineData("ServerErrorEvent", "type")]
    public void Deserialize_ShouldRejectExplicitNull_WhenMemberIsFenced(string dto, string member)
    {
        // Hand-authored payloads. Absent siblings retain defaults; only this member is mutated.
        var json = "{\"" + member + "\":null}";
        JsonTypeInfo info = dto switch
        {
            "ServerEventBase" => StrictInfo<ServerEventBase>(),
            "ResponseAudioDeltaEvent" => StrictInfo<ResponseAudioDeltaEvent>(),
            "ResponseAudioTranscriptDeltaEvent" => StrictInfo<ResponseAudioTranscriptDeltaEvent>(),
            "ResponseAudioTranscriptDoneEvent" => StrictInfo<ResponseAudioTranscriptDoneEvent>(),
            "FunctionCallArgumentsDoneEvent" => StrictInfo<FunctionCallArgumentsDoneEvent>(),
            "ServerErrorEvent" => StrictInfo<ServerErrorEvent>(),
            _ => throw new ArgumentOutOfRangeException(nameof(dto)),
        };
        var act = () => JsonSerializer.Deserialize(json, info);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserialize_ShouldAcceptExplicitNull_WhenErrorMessageIsNullable()
    {
        var result = JsonSerializer.Deserialize("""{"type":"error","error":{"message":null}}""", StrictInfo<ServerErrorEvent>());

        result.Should().NotBeNull();
        result!.Error.Should().NotBeNull();
        result.Error!.Message.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ShouldKeepSnakeCaseNaming_WhenUsingStrictReadMetadata()
    {
        var info = StrictInfo<FunctionCallArgumentsDoneEvent>();

        var snake = JsonSerializer.Deserialize(
            """{"type":"response.function_call_arguments.done","call_id":"c1","name":"f","arguments":"{}"}""", info);
        var camel = JsonSerializer.Deserialize(
            """{"type":"response.function_call_arguments.done","callId":"c1","name":"f","arguments":"{}"}""", info);

        info.Options.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.SnakeCaseLower);
        info.Options.RespectNullableAnnotations.Should().BeTrue();
        info.Options.IsReadOnly.Should().BeTrue();
        snake.Should().NotBeNull();
        snake!.CallId.Should().Be("c1");
        snake.Name.Should().Be("f");
        snake.Arguments.Should().Be("{}");
        camel.Should().NotBeNull();
        camel!.CallId.Should().BeEmpty("camelCase is an absent wire member, which retains its default");
    }

    [Fact]
    public void Deserialize_ShouldReturnNull_WhenStrictReadPayloadIsRootNull()
    {
        JsonSerializer.Deserialize("null", StrictInfo<ServerEventBase>()).Should().BeNull();
    }

    // ── InputAudioBufferAppendRequest ────────────────────────────────────────

    [Fact]
    public void InputAudioBufferAppendRequest_ShouldRoundTrip()
    {
        var request = new InputAudioBufferAppendRequest { Audio = "dGVzdA==" };

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.InputAudioBufferAppendRequest);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.InputAudioBufferAppendRequest);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("input_audio_buffer.append");
        deserialized.Audio.Should().Be("dGVzdA==");
    }

    [Fact]
    public void InputAudioBufferAppendRequest_ShouldSerializeWithSnakeCaseNaming()
    {
        var request = new InputAudioBufferAppendRequest { Audio = "base64data" };

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.InputAudioBufferAppendRequest);

        json.Should().Contain("\"type\":");
        json.Should().Contain("\"audio\":");
        json.Should().Contain("\"input_audio_buffer.append\"");
        json.Should().Contain("\"base64data\"");
    }

    [Fact]
    public void InputAudioBufferAppendRequest_DefaultAudio_ShouldBeEmpty()
    {
        var request = new InputAudioBufferAppendRequest();

        request.Audio.Should().BeEmpty();
        request.Type.Should().Be("input_audio_buffer.append");
    }

    // ── InputAudioBufferCommitRequest ────────────────────────────────────────

    [Fact]
    public void InputAudioBufferCommitRequest_ShouldRoundTrip()
    {
        var request = new InputAudioBufferCommitRequest();

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.InputAudioBufferCommitRequest);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.InputAudioBufferCommitRequest);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("input_audio_buffer.commit");
    }

    [Fact]
    public void InputAudioBufferCommitRequest_ShouldSerializeTypeField()
    {
        var request = new InputAudioBufferCommitRequest();
        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.InputAudioBufferCommitRequest);

        json.Should().Contain("\"type\":\"input_audio_buffer.commit\"");
    }

    // ── ResponseAudioDeltaEvent ──────────────────────────────────────────────

    [Fact]
    public void ResponseAudioDeltaEvent_ShouldRoundTrip()
    {
        var evt = new ResponseAudioDeltaEvent
        {
            Type = "response.output_audio.delta",
            Delta = "SGVsbG8=",
        };

        var json = JsonSerializer.Serialize(evt, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("response.output_audio.delta");
        deserialized.Delta.Should().Be("SGVsbG8=");
    }

    [Fact]
    public void ResponseAudioDeltaEvent_ShouldDeserializeFromOpenAiJson()
    {
        const string json = """{"type":"response.output_audio.delta","delta":"AAAA"}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be("response.output_audio.delta");
        evt.Delta.Should().Be("AAAA");
    }

    // ── ResponseCreateRequest ────────────────────────────────────────────────

    [Fact]
    public void ResponseCreateRequest_ShouldRoundTrip()
    {
        var request = new ResponseCreateRequest();

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.ResponseCreateRequest);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseCreateRequest);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("response.create");
    }

    // ── ConversationItemCreateRequest ────────────────────────────────────────

    [Fact]
    public void ConversationItemCreateRequest_ShouldRoundTrip()
    {
        var request = new ConversationItemCreateRequest
        {
            Item = new ConversationItem
            {
                Type = "function_call_output",
                CallId = "call-123",
                Output = """{"result":"ok"}""",
            },
        };

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.ConversationItemCreateRequest);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ConversationItemCreateRequest);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("conversation.item.create");
        deserialized.Item.Should().NotBeNull();
        deserialized.Item.Type.Should().Be("function_call_output");
        deserialized.Item.CallId.Should().Be("call-123");
        deserialized.Item.Output.Should().Be("""{"result":"ok"}""");
    }

    [Fact]
    public void ConversationItemCreateRequest_ShouldSerializeSnakeCase()
    {
        var request = new ConversationItemCreateRequest
        {
            Item = new ConversationItem
            {
                Type = "function_call_output",
                CallId = "call-1",
                Output = "{}",
            },
        };

        var json = JsonSerializer.Serialize(request, RealtimeJsonContext.Default.ConversationItemCreateRequest);

        json.Should().Contain("\"call_id\":");
    }

    // ── FunctionCallErrorOutput ──────────────────────────────────────────────

    /// <summary>
    /// The bridge writes this type through <see cref="OpenAiRealtimeBridge.ReadableOutputContext"/>
    /// rather than <see cref="RealtimeJsonContext.Default"/>. The model reads the text as written, so an
    /// HTML-safe encoder's escapes for the apostrophe, the plus sign, <c>&gt;</c> and a non-ASCII letter
    /// would reach it verbatim; the backslash and the control character are escaped because a JSON
    /// string requires it.
    /// </summary>
    [Fact]
    public void FunctionCallErrorOutput_ShouldSerializeAsReadableErrorProperty_WhenWrittenThroughReadableOutputContext()
    {
        var output = new FunctionCallErrorOutput
        {
            Error = "Can't transfer Jos\u00E9's call to +15551234567: retries > 3 at C:\\queues\u0007",
        };

        var json = JsonSerializer.Serialize(output, OpenAiRealtimeBridge.ReadableOutputContext.FunctionCallErrorOutput);

        json.Should().Be("""{"error":"Can't transfer José's call to +15551234567: retries > 3 at C:\\queues\u0007"}""");
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Should().ContainSingle()
            .Which.Name.Should().Be("error", "the options replace the context's own, and must keep its snake_case naming");
        document.RootElement.GetProperty("error").GetString().Should().Be(output.Error);
    }

    // ── FunctionCallArgumentsDoneEvent ────────────────────────────────────────

    [Fact]
    public void FunctionCallArgumentsDoneEvent_ShouldDeserialize()
    {
        const string json = """{"type":"response.function_call_arguments.done","call_id":"call-42","name":"get_weather","arguments":"{\"city\":\"NYC\"}"}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.FunctionCallArgumentsDoneEvent);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be("response.function_call_arguments.done");
        evt.CallId.Should().Be("call-42");
        evt.Name.Should().Be("get_weather");
        evt.Arguments.Should().Be("""{"city":"NYC"}""");
    }

    [Fact]
    public void FunctionCallArgumentsDoneEvent_ShouldRoundTrip()
    {
        var evt = new FunctionCallArgumentsDoneEvent
        {
            Type = "response.function_call_arguments.done",
            CallId = "call-99",
            Name = "search",
            Arguments = """{"q":"test"}""",
        };

        var json = JsonSerializer.Serialize(evt, RealtimeJsonContext.Default.FunctionCallArgumentsDoneEvent);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.FunctionCallArgumentsDoneEvent);

        deserialized.Should().NotBeNull();
        deserialized!.CallId.Should().Be("call-99");
        deserialized.Name.Should().Be("search");
    }

    // ── ServerErrorEvent ─────────────────────────────────────────────────────

    [Fact]
    public void ServerErrorEvent_ShouldDeserialize()
    {
        const string json = """{"type":"error","error":{"message":"rate limit exceeded"}}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerErrorEvent);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be("error");
        evt.Error.Should().NotBeNull();
        evt.Error!.Message.Should().Be("rate limit exceeded");
    }

    [Fact]
    public void ServerErrorEvent_ShouldHandleNullError()
    {
        const string json = """{"type":"error"}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerErrorEvent);

        evt.Should().NotBeNull();
        evt!.Error.Should().BeNull();
    }

    // ── ResponseAudioTranscriptDeltaEvent ────────────────────────────────────

    [Fact]
    public void ResponseAudioTranscriptDeltaEvent_ShouldRoundTrip()
    {
        var evt = new ResponseAudioTranscriptDeltaEvent
        {
            Type = "response.output_audio_transcript.delta",
            Delta = "Hello",
        };

        var json = JsonSerializer.Serialize(evt, RealtimeJsonContext.Default.ResponseAudioTranscriptDeltaEvent);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDeltaEvent);

        deserialized.Should().NotBeNull();
        deserialized!.Delta.Should().Be("Hello");
    }

    // ── ResponseAudioTranscriptDoneEvent ─────────────────────────────────────

    [Fact]
    public void ResponseAudioTranscriptDoneEvent_ShouldRoundTrip()
    {
        var evt = new ResponseAudioTranscriptDoneEvent
        {
            Type = "response.output_audio_transcript.done",
            Transcript = "Hello world",
        };

        var json = JsonSerializer.Serialize(evt, RealtimeJsonContext.Default.ResponseAudioTranscriptDoneEvent);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDoneEvent);

        deserialized.Should().NotBeNull();
        deserialized!.Transcript.Should().Be("Hello world");
    }

    // ── ServerEventBase ──────────────────────────────────────────────────────

    [Fact]
    public void ServerEventBase_ShouldDeserializeUnknownType()
    {
        const string json = """{"type":"some.unknown.event","extra":"ignored"}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be("some.unknown.event");
    }

    [Fact]
    public void ServerEventBase_ShouldHandleEmptyObject()
    {
        const string json = """{}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase);

        evt.Should().NotBeNull();
        evt!.Type.Should().BeEmpty();
    }
}
