using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.DependencyInjection;

public class TtsDiTests
{
    [Fact]
    public async Task AddElevenLabsSpeechSynthesizer_ShouldRegisterAsSpeechSynthesizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElevenLabsSpeechSynthesizer(o => { o.ApiKey = "test"; o.VoiceId = "v1"; });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechSynthesizer>().Should().BeOfType<ElevenLabsSpeechSynthesizer>();
    }

    [Fact]
    public async Task AddAzureTtsSpeechSynthesizer_ShouldRegisterAsSpeechSynthesizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureTtsSpeechSynthesizer(o =>
        {
            o.ApiKey = "test";
            o.Region = "eastus";
            o.VoiceName = "es-CO-SalomeNeural";
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SpeechSynthesizer>().Should().BeOfType<AzureTtsSpeechSynthesizer>();
    }

    [Fact]
    public async Task AddElevenLabsSpeechSynthesizer_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElevenLabsSpeechSynthesizer(o =>
        {
            o.ApiKey = "my-key";
            o.VoiceId = "my-voice";
            o.Stability = 0.8f;
        });
        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<ElevenLabsOptions>>().Value;
        opts.Stability.Should().Be(0.8f);
        opts.VoiceId.Should().Be("my-voice");
    }
}
