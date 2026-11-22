using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Internal;

/// <summary>
/// Annotation and options contracts through the shipped strict metadata. All TTS response reference
/// members are deliberately nullable, so these controls cannot prove receive-loop options wiring.
/// The governance tree guard holds that wiring; the socket tests hold tolerated frame behaviour.
/// </summary>
public sealed class TtsReadContractTests
{
    // Test-only lookup keeps the tests-first commit buildable before the helper exists. There is
    // no local options copy or fallback: every assertion reads the shipped For<T> field. The literal
    // closed type name lets the trim/AOT analyzers follow this seam; product code uses the field.
    private static JsonTypeInfo<CartesiaTtsServerMessage> StrictCartesiaInfo()
    {
        var cache = Type.GetType("Verbara.Sdk.VoiceAi.Tts.Internal.TtsJsonReadOptions+For`1[[Verbara.Sdk.VoiceAi.Tts.Internal.CartesiaTtsServerMessage, Verbara.Sdk.VoiceAi.Tts]], Verbara.Sdk.VoiceAi.Tts");
        cache.Should().NotBeNull("the shipped read fence must exist");
        var field = cache!.GetField("TypeInfo", BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (JsonTypeInfo<CartesiaTtsServerMessage>)field!.GetValue(null)!;
    }

    [Fact]
    public void Deserialize_ShouldAcceptNullUnderStrictOptions_WhenCartesiaKindIsNullable()
    {
        var result = JsonSerializer.Deserialize("{\"type\":null}", StrictCartesiaInfo());

        result.Should().NotBeNull();
        result!.Type.Should().BeNull();
    }

    [Fact]
    public void Options_ShouldEnforceNullableAnnotations_WhenResolvedForAResponse()
    {
        var options = StrictCartesiaInfo().Options;

        options.RespectNullableAnnotations.Should().BeTrue();
        options.Should().NotBeSameAs(VoiceAiTtsJsonContext.Default.Options);
        VoiceAiTtsJsonContext.Default.Options.RespectNullableAnnotations.Should().BeFalse();
    }

    [Fact]
    public void Options_ShouldRejectChanges_WhenStrictMetadataHasBeenCreated()
    {
        var options = StrictCartesiaInfo().Options;

        options.IsReadOnly.Should().BeTrue();
        var act = () => options.RespectNullableAnnotations = false;
        act.Should().Throw<InvalidOperationException>();
    }
}
