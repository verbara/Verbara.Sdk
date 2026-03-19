using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests.DependencyInjection;

public sealed class AudioSocketServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAudioSocketServer_ShouldRegister_AudioSocketOptionsAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAudioSocketServer(opts => opts.Port = 9093);

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AudioSocketOptions>();
        options.Port.Should().Be(9093);

        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(s => s is AudioSocketServer);
    }
}
