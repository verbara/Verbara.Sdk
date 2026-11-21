using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Internal;

/// <summary>
/// Annotation contracts independent of the production receive-loop wiring. The loop tests prove
/// that wiring; these tests prove the intentional nullable exceptions also work under the fence.
/// </summary>
public sealed class SttReadContractTests
{
    [Theory]
    [InlineData("type")]
    [InlineData("text")]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenCartesiaMemberIsNullable(string member)
    {
        var json = member == "type" ? "{\"type\":null}" : "{\"text\":null}";

        var result = JsonSerializer.Deserialize(json, Strict<CartesiaSttTranscriptMessage>());

        result.Should().NotBeNull();
        if (member == "type") result!.Type.Should().BeNull();
        else result!.Text.Should().BeNull();
    }

    [Theory]
    [InlineData("type")]
    [InlineData("transcript")]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenAssemblyAiMemberIsNullable(string member)
    {
        var json = member == "type" ? "{\"type\":null}" : "{\"transcript\":null}";

        var result = JsonSerializer.Deserialize(json, Strict<AssemblyAiTurnMessage>());

        result.Should().NotBeNull();
        if (member == "type") result!.Type.Should().BeNull();
        else result!.Transcript.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenGoogleTranscriptIsNullable()
    {
        var result = JsonSerializer.Deserialize("{\"transcript\":null}", Strict<GoogleSpeechAlternative>());

        result.Should().NotBeNull();
        result!.Transcript.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ShouldRejectNullGreetingKind_WhenSpeechmaticsRecognitionStartedMessageIsNull()
    {
        // This member is unused by dispatch: the first transcript decode already rejects
        // message:null, so no socket payload can reach this second DTO with a null kind.
        // This is an annotation compatibility control, green before the production fix.
        var act = () => JsonSerializer.Deserialize("{\"message\":null}", Strict<SpeechmaticsRecognitionStartedMessage>());

        act.Should().Throw<JsonException>();
    }

    private static JsonTypeInfo<T> Strict<T>()
    {
        var options = new JsonSerializerOptions(VoiceAiSttJsonContext.Default.Options)
        {
            RespectNullableAnnotations = true
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
    }
}
