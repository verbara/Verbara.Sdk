using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// The source-generated validator Deepgram's options gain with the connect-timeout range, shaped like its siblings':
/// it rejects a <c>ConnectTimeoutSeconds</c> outside 1–600 and, like every sibling, a <c>BaseUri</c> that is not
/// <c>ws://</c> or <c>wss://</c>, the pattern the options already declared and nothing checked.
/// </summary>
public sealed class DeepgramOptionsValidatorTests
{
    private static DeepgramOptions Configured(Action<DeepgramOptions>? configure = null)
    {
        var options = new DeepgramOptions { ApiKey = "test-key" };
        configure?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public void Validate_ShouldFailNamingTheOption_WhenConnectTimeoutSecondsIsOutsideOneTo600(int value)
    {
        var result = new DeepgramOptionsValidator().Validate(null, Configured(o => o.ConnectTimeoutSeconds = value));

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("ConnectTimeoutSeconds");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(null)]
    public void Validate_ShouldSucceed_WhenConnectTimeoutSecondsIsInsideOneTo600(int? value)
    {
        var options = Configured(o => o.ConnectTimeoutSeconds = value ?? o.ConnectTimeoutSeconds);

        new DeepgramOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFailNamingBaseUri_WhenBaseUriIsNotAWebSocketUri()
    {
        var result = new DeepgramOptionsValidator().Validate(null, Configured(o => o.BaseUri = "https://example.invalid"));

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("BaseUri");
        }
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenBaseUriIsTheDefault()
    {
        new DeepgramOptionsValidator().Validate(null, Configured()).Succeeded.Should().BeTrue();
    }
}
