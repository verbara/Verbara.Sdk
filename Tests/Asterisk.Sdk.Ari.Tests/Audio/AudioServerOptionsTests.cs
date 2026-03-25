using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public sealed class AudioServerOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeReasonable()
    {
        var options = new AudioServerOptions();

        options.AudioSocketPort.Should().Be(9092);
        options.WebSocketPort.Should().Be(9093);
        options.ListenAddress.Should().Be("0.0.0.0");
        options.MaxConcurrentStreams.Should().Be(1000);
        options.DefaultFormat.Should().Be("slin16");
        options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Properties_ShouldBeSettable()
    {
        var options = new AudioServerOptions
        {
            AudioSocketPort = 5000,
            WebSocketPort = 0,
            ListenAddress = "127.0.0.1",
            MaxConcurrentStreams = 500,
            DefaultFormat = "ulaw",
            IdleTimeout = TimeSpan.FromSeconds(120)
        };

        options.AudioSocketPort.Should().Be(5000);
        options.WebSocketPort.Should().Be(0);
        options.ListenAddress.Should().Be("127.0.0.1");
        options.MaxConcurrentStreams.Should().Be(500);
        options.DefaultFormat.Should().Be("ulaw");
        options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }
}
