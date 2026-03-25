using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.DependencyInjection;

public sealed class SttDiNullConfigTests
{
    [Fact]
    public async Task AddDeepgramSpeechRecognizer_ShouldWork_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeepgramSpeechRecognizer(configure: null);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<DeepgramSpeechRecognizer>();
    }

    [Fact]
    public async Task AddWhisperSpeechRecognizer_ShouldWork_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWhisperSpeechRecognizer(configure: null);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<WhisperSpeechRecognizer>();
    }

    [Fact]
    public async Task AddAzureWhisperSpeechRecognizer_ShouldWork_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureWhisperSpeechRecognizer(configure: null);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<AzureWhisperSpeechRecognizer>();
    }

    [Fact]
    public async Task AddGoogleSpeechRecognizer_ShouldWork_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoogleSpeechRecognizer(configure: null);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<GoogleSpeechRecognizer>();
    }

    [Fact]
    public void DeepgramOptions_ShouldHaveDefaults()
    {
        var options = new DeepgramOptions();
        options.ApiKey.Should().BeEmpty();
        options.Model.Should().Be("nova-2");
        options.Language.Should().Be("es");
        options.InterimResults.Should().BeTrue();
        options.Punctuate.Should().BeTrue();
    }

    [Fact]
    public void GoogleSpeechOptions_ShouldHaveDefaults()
    {
        var options = new GoogleSpeechOptions();
        options.ApiKey.Should().BeEmpty();
    }
}
