using Asterisk.Sdk.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeCorrect()
    {
        var opts = new AudioSocketOptions();

        opts.ListenAddress.Should().Be("0.0.0.0");
        opts.Port.Should().Be(9092);
        opts.MaxConcurrentSessions.Should().Be(1000);
        opts.DefaultFormat.Should().Be(AudioFormat.Slin16Mono8kHz);
        opts.ReceiveBufferSize.Should().Be(4096);
        opts.ConnectionTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Port_ShouldBeSettable()
    {
        var opts = new AudioSocketOptions { Port = 9999 };

        opts.Port.Should().Be(9999);
    }

    [Fact]
    public void ListenAddress_ShouldBeSettable()
    {
        var opts = new AudioSocketOptions { ListenAddress = "127.0.0.1" };

        opts.ListenAddress.Should().Be("127.0.0.1");
    }

    [Fact]
    public void MaxConcurrentSessions_ShouldBeSettable()
    {
        var opts = new AudioSocketOptions { MaxConcurrentSessions = 5000 };

        opts.MaxConcurrentSessions.Should().Be(5000);
    }

    [Fact]
    public void DefaultFormat_ShouldBeSettable()
    {
        var opts = new AudioSocketOptions { DefaultFormat = AudioFormat.Slin16Mono16kHz };

        opts.DefaultFormat.Should().Be(AudioFormat.Slin16Mono16kHz);
    }
}
