using System.Text.Json;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

public sealed class RealtimeMessageSerializationTests
{
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
            Type = "response.audio.delta",
            Delta = "SGVsbG8=",
        };

        var json = JsonSerializer.Serialize(evt, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);
        var deserialized = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be("response.audio.delta");
        deserialized.Delta.Should().Be("SGVsbG8=");
    }

    [Fact]
    public void ResponseAudioDeltaEvent_ShouldDeserializeFromOpenAiJson()
    {
        const string json = """{"type":"response.audio.delta","delta":"AAAA"}""";

        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be("response.audio.delta");
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
            Type = "response.audio_transcript.delta",
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
            Type = "response.audio_transcript.done",
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
