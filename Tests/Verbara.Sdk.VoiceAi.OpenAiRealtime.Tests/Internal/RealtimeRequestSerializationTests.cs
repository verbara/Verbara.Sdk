using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>Frozen UTF-8 literals for the plain send contexts, including intentionally null members.</summary>
public sealed class RealtimeRequestSerializationTests
{
    [Fact]
    public void Serialize_ShouldKeepAppendBytes_WhenUsingTheSendContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new InputAudioBufferAppendRequest { Audio = "AA==" },
            RealtimeJsonContext.Default.InputAudioBufferAppendRequest)
            .Should().Equal("""{"type":"input_audio_buffer.append","audio":"AA=="}"""u8.ToArray());
        JsonSerializer.SerializeToUtf8Bytes(new InputAudioBufferAppendRequest { Audio = null! },
            RealtimeJsonContext.Default.InputAudioBufferAppendRequest)
            .Should().Equal("""{"type":"input_audio_buffer.append","audio":null}"""u8.ToArray());
    }

    [Fact]
    public void Serialize_ShouldKeepCommitBytes_WhenUsingTheSendContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new InputAudioBufferCommitRequest(), RealtimeJsonContext.Default.InputAudioBufferCommitRequest)
            .Should().Equal("""{"type":"input_audio_buffer.commit"}"""u8.ToArray());
        JsonSerializer.SerializeToUtf8Bytes(new InputAudioBufferCommitRequest { Type = null! }, RealtimeJsonContext.Default.InputAudioBufferCommitRequest)
            .Should().Equal("""{"type":null}"""u8.ToArray());
    }

    [Fact]
    public void Serialize_ShouldKeepConversationBytes_WhenUsingTheSendContext()
    {
        var request = new ConversationItemCreateRequest
        {
            Item = new ConversationItem { Type = "function_call_output", CallId = "c1", Output = "{}" },
        };
        JsonSerializer.SerializeToUtf8Bytes(request, RealtimeJsonContext.Default.ConversationItemCreateRequest)
            .Should().Equal("""{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"c1","output":"{}"}}"""u8.ToArray());
        request.Item = new ConversationItem { Type = null!, CallId = null, Output = null };
        JsonSerializer.SerializeToUtf8Bytes(request, RealtimeJsonContext.Default.ConversationItemCreateRequest)
            .Should().Equal("""{"type":"conversation.item.create","item":{"type":null,"call_id":null,"output":null}}"""u8.ToArray());
        request.Item = null!;
        JsonSerializer.SerializeToUtf8Bytes(request, RealtimeJsonContext.Default.ConversationItemCreateRequest)
            .Should().Equal("""{"type":"conversation.item.create","item":null}"""u8.ToArray());
    }

    [Fact]
    public void Serialize_ShouldKeepResponseCreateBytes_WhenUsingTheSendContext()
    {
        JsonSerializer.SerializeToUtf8Bytes(new ResponseCreateRequest(), RealtimeJsonContext.Default.ResponseCreateRequest)
            .Should().Equal("""{"type":"response.create"}"""u8.ToArray());
        JsonSerializer.SerializeToUtf8Bytes(new ResponseCreateRequest { Type = null! }, RealtimeJsonContext.Default.ResponseCreateRequest)
            .Should().Equal("""{"type":null}"""u8.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialize_ShouldKeepErrorOutputBytes_WhenUsingEitherSendContext(bool readable)
    {
        var info = readable ? OpenAiRealtimeBridge.ReadableOutputContext.FunctionCallErrorOutput
            : RealtimeJsonContext.Default.FunctionCallErrorOutput;
        var output = new FunctionCallErrorOutput { Error = "Can't + > & José" };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(output, info);

        bytes.Should().Equal(readable
            ? """{"error":"Can't + > & José"}"""u8.ToArray()
            : """{"error":"Can\u0027t \u002B \u003E \u0026 Jos\u00E9"}"""u8.ToArray());
        JsonSerializer.SerializeToUtf8Bytes(new FunctionCallErrorOutput { Error = null! }, info)
            .Should().Equal("""{"error":null}"""u8.ToArray());
    }
}
