using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Internal;

/// <summary>
/// The bare <c>error</c>-member test the ElevenLabs and LMNT envelope peeks apply to a message their typed
/// decode could not read. A member that carries a value names a failure, in whatever shape it arrives; a
/// member that is the empty value of its kind — <c>null</c>, <c>""</c>, <c>false</c>, <c>0</c>, <c>{}</c>,
/// <c>[]</c> — is how a JSON reply says "no error", and names none.
/// </summary>
public sealed class TtsErrorEnvelopeTests
{
    [Theory]
    [InlineData("""{"error":false}""")]
    [InlineData("""{"error":0}""")]
    [InlineData("""{"error":0.0}""")]
    [InlineData("""{"error":{}}""")]
    [InlineData("""{"error":[]}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":""}""")]
    [InlineData("""{"type":"buffer_empty"}""")]
    public void HasErrorMember_ShouldBeFalse_WhenTheErrorMemberIsAbsentOrTheEmptyValueOfItsKind(string json)
    {
        using var document = JsonDocument.Parse(json);

        TtsErrorEnvelope.HasErrorMember(document.RootElement)
            .Should().BeFalse("an empty, false or zero error member says no error occurred");
    }

    [Theory]
    [InlineData("""{"error":"Invalid API key"}""")]
    [InlineData("""{"error":true}""")]
    [InlineData("""{"error":1008}""")]
    [InlineData("""{"error":{"reason":"Invalid API key"}}""")]
    [InlineData("""{"error":["Invalid API key"]}""")]
    public void HasErrorMember_ShouldBeTrue_WhenTheErrorMemberCarriesAValue(string json)
    {
        using var document = JsonDocument.Parse(json);

        TtsErrorEnvelope.HasErrorMember(document.RootElement)
            .Should().BeTrue("a value in the member the vendor marks a failure with names that failure, in any shape");
    }
}
