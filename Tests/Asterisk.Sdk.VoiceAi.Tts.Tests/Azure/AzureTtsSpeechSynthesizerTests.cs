using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Azure;
using Asterisk.Sdk.VoiceAi.Tts.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Azure;

public class AzureTtsSpeechSynthesizerTests
{
    private static AzureTtsOptions ValidOptions => new()
    {
        ApiKey = "azure-tts-key",
        Region = "eastus",
        VoiceName = "es-CO-SalomeNeural",
        OutputFormat = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm
    };

    [Fact]
    public async Task SynthesizeAsync_ShouldPostSsmlXml()
    {
        var mock = new MockHttpMessageHandler(new byte[320]);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/ssml+xml");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldEscapeXmlInText()
    {
        var mock = new MockHttpMessageHandler(new byte[320]);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("<script>alert('xss')</script>", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        mock.LastRequestBody.Should().NotContain("<script>");
        mock.LastRequestBody.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldChunkedResponseAsFrames()
    {
        var mock = new MockHttpMessageHandler(new byte[640]);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock), chunkSize: 320);

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldUseApiKeyHeader()
    {
        var mock = new MockHttpMessageHandler(new byte[320]);
        var synth = new AzureTtsSpeechSynthesizer(
            Options.Create(ValidOptions), new HttpClient(mock));

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        mock.LastRequest!.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var vals)
            .Should().BeTrue();
        vals!.Should().Contain("azure-tts-key");
    }
}
