using Asterisk.Sdk.Ari.Client;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Client;

public sealed class AriClientOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeReasonable()
    {
        var options = new AriClientOptions();

        options.BaseUrl.Should().Be("http://localhost:8088");
        options.Username.Should().BeEmpty();
        options.Password.Should().BeEmpty();
        options.Application.Should().BeEmpty();
        options.AutoReconnect.Should().BeTrue();
        options.ReconnectInitialDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.ReconnectMaxDelay.Should().Be(TimeSpan.FromSeconds(30));
        options.ReconnectMultiplier.Should().Be(2.0);
        options.MaxReconnectAttempts.Should().Be(0);
    }

    [Fact]
    public void ReconnectProperties_ShouldBeSettable()
    {
        var options = new AriClientOptions
        {
            AutoReconnect = false,
            ReconnectInitialDelay = TimeSpan.FromSeconds(5),
            ReconnectMaxDelay = TimeSpan.FromMinutes(2),
            ReconnectMultiplier = 1.5,
            MaxReconnectAttempts = 10
        };

        options.AutoReconnect.Should().BeFalse();
        options.ReconnectInitialDelay.Should().Be(TimeSpan.FromSeconds(5));
        options.ReconnectMaxDelay.Should().Be(TimeSpan.FromMinutes(2));
        options.ReconnectMultiplier.Should().Be(1.5);
        options.MaxReconnectAttempts.Should().Be(10);
    }

    [Fact]
    public void ConfigureAudioServer_ShouldBeNullByDefault()
    {
        var options = new AriClientOptions();

        options.ConfigureAudioServer.Should().BeNull();
    }

    [Fact]
    public void ConfigureAudioServer_ShouldBeSettable()
    {
        var options = new AriClientOptions();
        options.ConfigureAudioServer = audio => audio.AudioSocketPort = 5000;

        options.ConfigureAudioServer.Should().NotBeNull();
    }
}
