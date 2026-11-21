using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Internal;

/// <summary>
/// Annotation contracts read through the shipped <see cref="SttJsonReadOptions"/>, independent of the
/// receive-loop wiring. The loop tests prove that wiring; these tests prove the intentional nullable
/// exceptions also work under the fence.
/// </summary>
public sealed class SttReadContractTests
{
    [Theory]
    [InlineData("type")]
    [InlineData("text")]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenCartesiaMemberIsNullable(string member)
    {
        var json = member == "type" ? "{\"type\":null}" : "{\"text\":null}";

        var result = JsonSerializer.Deserialize(json, SttJsonReadOptions.TypeInfo<CartesiaSttTranscriptMessage>());

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

        var result = JsonSerializer.Deserialize(json, SttJsonReadOptions.TypeInfo<AssemblyAiTurnMessage>());

        result.Should().NotBeNull();
        if (member == "type") result!.Type.Should().BeNull();
        else result!.Transcript.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenGoogleTranscriptIsNullable()
    {
        var result = JsonSerializer.Deserialize("{\"transcript\":null}", SttJsonReadOptions.TypeInfo<GoogleSpeechAlternative>());

        result.Should().NotBeNull();
        result!.Transcript.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ShouldRejectNullGreetingKind_WhenSpeechmaticsRecognitionStartedMessageIsNull()
    {
        // This member is unused by dispatch: the first transcript decode already rejects
        // message:null, so no socket payload can reach this second DTO with a null kind.
        // This is an annotation compatibility control: it holds with or without the null fixes.
        var act = () => JsonSerializer.Deserialize("{\"message\":null}", SttJsonReadOptions.TypeInfo<SpeechmaticsRecognitionStartedMessage>());

        act.Should().Throw<JsonException>();
    }
}
