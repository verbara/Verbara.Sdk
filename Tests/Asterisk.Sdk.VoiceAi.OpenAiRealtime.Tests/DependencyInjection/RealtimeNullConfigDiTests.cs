using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.DependencyInjection;

public sealed class RealtimeNullConfigDiTests
{
    [Fact]
    public void AddOpenAiRealtimeBridge_ShouldRegisterServices_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAudioSocketServer(o => o.Port = 0);

        // The null configure path should still add Options<OpenAiRealtimeOptions> registration.
        // We can't resolve the bridge because validation will fail (no ApiKey),
        // but we can verify the service registrations are in place.
        services.AddOpenAiRealtimeBridge(configure: null);

        // Verify registrations exist: VoiceAiSessionBroker is a concrete type registered as hosted service
        services.Should().Contain(sd => sd.ServiceType == typeof(IHostedService));
        services.Should().Contain(sd => sd.ServiceType == typeof(ISessionHandler));
    }
}
