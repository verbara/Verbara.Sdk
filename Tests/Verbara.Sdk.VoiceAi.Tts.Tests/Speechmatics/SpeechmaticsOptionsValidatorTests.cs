using Verbara.Sdk.VoiceAi.Tts.Speechmatics;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Speechmatics;

/// <summary>
/// The voice check that the service refuses to perform.
/// </summary>
/// <remarks>
/// <c>POST /generate/{unknown}</c> answers <c>200 audio/wav</c> and synthesises the fallback
/// speaker, so a misspelt voice reaches production as a perfectly healthy-looking response. These
/// tests pin the only place left that can object.
/// </remarks>
public class SpeechmaticsOptionsValidatorTests
{
    private static SpeechmaticsOptions Configured(Action<SpeechmaticsOptions>? configure = null)
    {
        var options = new SpeechmaticsOptions { ApiKey = "test-key" };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void Voice_ShouldDefaultToJack_WhenNothingIsConfigured()
    {
        // Jack is also the voice the service falls back to, so callers who were on the old
        // 'eleanor' default keep hearing exactly the same speaker across this change.
        new SpeechmaticsOptions().Voice.Should().Be(SpeechmaticsVoices.Jack);
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenVoiceIsTheShippedDefault()
    {
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured())
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("sarah")]
    [InlineData("theo")]
    [InlineData("megan")]
    [InlineData("jack")]
    public void Validate_ShouldSucceed_WhenVoiceIsInTheCatalog(string voice)
    {
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured(o => o.Voice = voice))
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenVoiceIsEleanor()
    {
        // The regression pin. 'eleanor' was the shipped default until 2026-08-18 and is not a
        // Speechmatics voice; live pitch measurement put it on top of the fallback speaker.
        var result = new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured(o => o.Voice = "eleanor"));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("eleanor").And.Contain("jack");
    }

    [Theory]
    [InlineData("Sarah")]
    [InlineData("SARAH")]
    [InlineData("Jack")]
    public void Validate_ShouldFail_WhenVoiceDiffersFromTheCatalogOnlyByCase(string voice)
    {
        // Measured 2026-08-18: 'sarah' returns the 180 Hz speaker while 'Sarah' and 'SARAH' return
        // the ~90 Hz fallback. Case-insensitive matching here would pass a value the API ignores.
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured(o => o.Voice = voice))
            .Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenVoiceIsUnlistedAndAllowUnlistedVoiceIsSet()
    {
        // The service is a vendor-labelled preview; the roster may grow between SDK releases.
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured(o =>
            {
                o.Voice = "a-voice-the-vendor-added-after-this-release";
                o.AllowUnlistedVoice = true;
            }))
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenVoiceIsEmpty_EvenWithAllowUnlistedVoice()
    {
        // An empty segment is not an unlisted voice, it is a broken route: /generate/ is a 404.
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, Configured(o =>
            {
                o.Voice = string.Empty;
                o.AllowUnlistedVoice = true;
            }))
            .Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldStillApplyDataAnnotations_WhenVoiceIsValid()
    {
        new SpeechmaticsOptionsValidator()
            .Validate(name: null, new SpeechmaticsOptions { ApiKey = string.Empty })
            .Failed.Should().BeTrue("the source-generated DataAnnotations rules run first");
    }

    [Fact]
    public void All_ShouldBeTheFourVoicesTheVendorPublishes_WhenRead()
    {
        SpeechmaticsVoices.All.Should().Equal("sarah", "theo", "megan", "jack");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eleanor")]
    public void IsKnown_ShouldReturnFalse_WhenVoiceIsNotInTheCatalog(string? voice)
    {
        SpeechmaticsVoices.IsKnown(voice).Should().BeFalse();
    }
}
