using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public sealed class AudioChannelVarsTests
{
    [Fact]
    public void PeerIp_ShouldBeChannelFunction()
    {
        AudioChannelVars.PeerIp.Should().Be("CHANNEL(peerip)");
    }

    [Fact]
    public void WriteFormat_ShouldBeChannelFunction()
    {
        AudioChannelVars.WriteFormat.Should().Be("CHANNEL(writeformat)");
    }

    [Fact]
    public void ReadFormat_ShouldBeChannelFunction()
    {
        AudioChannelVars.ReadFormat.Should().Be("CHANNEL(readformat)");
    }

    [Fact]
    public void ExternalMediaProtocol_ShouldBeKnownValue()
    {
        AudioChannelVars.ExternalMediaProtocol.Should().Be("EXTERNALMEDIA_PROTOCOL");
    }

    [Fact]
    public void ExternalMediaAddress_ShouldBeKnownValue()
    {
        AudioChannelVars.ExternalMediaAddress.Should().Be("EXTERNALMEDIA_ADDRESS");
    }

    [Fact]
    public void WebSocketConstants_ShouldBeKnownValues()
    {
        AudioChannelVars.WebSocketProtocol.Should().Be("WEBSOCKET_PROTOCOL");
        AudioChannelVars.WebSocketGuid.Should().Be("WEBSOCKET_GUID");
        AudioChannelVars.WebSocketUri.Should().Be("WEBSOCKET_URI");
    }

    [Fact]
    public void AudioFormats_ShouldHaveCorrectValues()
    {
        AudioChannelVars.Slin16.Should().Be("slin16");
        AudioChannelVars.Slin8.Should().Be("slin");
        AudioChannelVars.Slin48.Should().Be("slin48");
        AudioChannelVars.Ulaw.Should().Be("ulaw");
        AudioChannelVars.Alaw.Should().Be("alaw");
        AudioChannelVars.Opus.Should().Be("opus");
        AudioChannelVars.G729.Should().Be("g729");
    }
}
