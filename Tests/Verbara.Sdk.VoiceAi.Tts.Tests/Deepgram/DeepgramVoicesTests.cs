using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;

/// <summary>
/// Offline pins on the Deepgram catalog. The live membership check lives in
/// <c>VoiceCatalog.VoiceCatalogConformanceTests</c>; these are the parts worth failing fast on
/// without a credential.
/// </summary>
public class DeepgramVoicesTests
{
    [Fact]
    public void Helios_ShouldUseTheAura1Id_WhenRead()
    {
        // Until 2026-08-18 this constant carried "aura-2-helios-en", which the API rejects with
        // 400 "No such model/version combination found." — Helios exists only in Aura 1.
        DeepgramVoices.HeliosLegacy.Should().Be("aura-helios-en");
    }

    [Fact]
    public void Helios_ShouldStillResolveToTheSameWorkingId_WhenCalledByItsOldName()
    {
        // The old name survives as an obsolete alias: removing a public const is a binary break
        // (ApiCompat CP0002 against the package-validation baseline), and callers who kept it should
        // get audio rather than the 400 the old value earned them.
#pragma warning disable VSDK0001 // pinning the obsolete alias is the point of this test
        DeepgramVoices.Helios.Should().Be(DeepgramVoices.HeliosLegacy);
#pragma warning restore VSDK0001
    }

    [Fact]
    public void Model_ShouldDefaultToThalia_WhenNothingIsConfigured()
    {
        new DeepgramTtsOptions().Model.Should().Be(DeepgramVoices.Thalia);
    }

    [Fact]
    public void Catalog_ShouldNameEveryLanguageItClaimsToCover_WhenInspected()
    {
        // The multilingual TODO this closed asked for confirmed ids in nl, fr, de, it and ja.
        var suffixes = new[] { "-en", "-es", "-de", "-fr", "-it", "-ja", "-nl" };

        foreach (var suffix in suffixes)
        {
            new[]
            {
                DeepgramVoices.Thalia, DeepgramVoices.Celeste, DeepgramVoices.Elara,
                DeepgramVoices.Agathe, DeepgramVoices.Cinzia, DeepgramVoices.Izanami,
                DeepgramVoices.Daphne,
            }.Should().ContainSingle(id => id.EndsWith(suffix, StringComparison.Ordinal),
                $"the catalog should carry exactly one representative for '{suffix}' in this pin");
        }
    }
}
