using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests;

public sealed class OpenAiRealtimeOptionsValidatorTests
{
    private readonly OpenAiRealtimeOptionsValidator _validator = new();

    [Fact]
    public void Validate_ShouldSucceed_WhenApiKeyAndModelAreSet()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "sk-test-key-123",
            Model = "gpt-4o-realtime-preview",
        };

        var result = _validator.Validate(null, options);

        result.Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_ShouldFail_WhenApiKeyIsMissing()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = null!,
            Model = "gpt-4o-realtime-preview",
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ApiKey");
    }

    [Fact]
    public void Validate_ShouldFail_WhenApiKeyIsEmpty()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "",
            Model = "gpt-4o-realtime-preview",
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ApiKey");
    }

    [Fact]
    public void Validate_ShouldFail_WhenModelIsMissing()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "sk-test-key",
            Model = null!,
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
    }

    [Fact]
    public void Validate_ShouldFail_WhenModelIsEmpty()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "sk-test-key",
            Model = "",
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
    }

    [Fact]
    public void Validate_ShouldFail_WhenBothApiKeyAndModelAreMissing()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = null!,
            Model = null!,
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldSucceed_WithDefaultModelValue()
    {
        // OpenAiRealtimeOptions.Model defaults to "gpt-4o-realtime-preview"
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "sk-test-key",
        };

        var result = _validator.Validate(null, options);

        result.Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenVoiceAndInstructionsAreCustomized()
    {
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = "sk-test-key",
            Model = "gpt-4o-realtime-preview",
            Voice = "shimmer",
            Instructions = "You are a helpful assistant.",
        };

        var result = _validator.Validate(null, options);

        result.Should().Be(ValidateOptionsResult.Success);
    }
}
