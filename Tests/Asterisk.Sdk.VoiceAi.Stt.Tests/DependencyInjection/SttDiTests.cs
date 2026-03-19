using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.Google;
using Asterisk.Sdk.VoiceAi.Stt.Whisper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.DependencyInjection;

public class SttDiTests
{
    [Fact]
    public async Task AddDeepgramSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeepgramSpeechRecognizer(o => o.ApiKey = "test");
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<DeepgramSpeechRecognizer>();
    }

    [Fact]
    public async Task AddWhisperSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWhisperSpeechRecognizer(o => o.ApiKey = "test");
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<WhisperSpeechRecognizer>();
    }

    [Fact]
    public async Task AddAzureWhisperSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureWhisperSpeechRecognizer(o =>
        {
            o.ApiKey = "test";
            o.Endpoint = new Uri("https://example.openai.azure.com/openai/deployments");
            o.DeploymentName = "whisper";
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<AzureWhisperSpeechRecognizer>();
    }

    [Fact]
    public async Task AddGoogleSpeechRecognizer_ShouldRegisterAsSpeechRecognizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoogleSpeechRecognizer(o => o.ApiKey = "test");
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<GoogleSpeechRecognizer>();
    }
}
