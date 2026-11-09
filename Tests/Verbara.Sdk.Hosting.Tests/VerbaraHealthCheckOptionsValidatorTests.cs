using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// The host-wide health-check tags: empty until the host names one, and a null, empty or whitespace tag fails
/// validation naming <c>Tags</c>; any other name, the readiness tag included, is accepted.
/// </summary>
public sealed class VerbaraHealthCheckOptionsValidatorTests
{
    [Fact]
    public void Tags_ShouldBeEmpty_WhenConstructed()
    {
        var options = new VerbaraHealthCheckOptions();

        options.Tags.Should().BeEmpty("the SDK names no tag of its own");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_ShouldFailNamingTags_WhenATagIsBlank(string? tag)
    {
        var options = new VerbaraHealthCheckOptions();
        options.Tags.Add("voice");
        options.Tags.Add(tag!);

        var result = new VerbaraHealthCheckOptionsValidator().Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue("a tag with no text cannot select a check");
        result.FailureMessage.Should().Contain(nameof(VerbaraHealthCheckOptions.Tags), "the failure names the option to fix");
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenTagsAreReadyAndVoice()
    {
        var options = new VerbaraHealthCheckOptions();
        options.Tags.Add("ready");
        options.Tags.Add("voice");

        var result = new VerbaraHealthCheckOptionsValidator().Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue("the SDK does not know the host's vocabulary, so any non-blank tag is accepted");
    }
}
