// Tests/Verbara.Sdk.Sessions.Tests/SessionCorrelatorTests.cs
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class SessionCorrelatorTests
{
    private readonly SessionCorrelator _sut = new(new SessionOptions());

    [Fact]
    public void InferDirection_ShouldReturnInbound_WhenContextMatchesTrunk()
    {
        _sut.InferDirection("from-trunk", "s").Should().Be(CallDirection.Inbound);
    }

    [Fact]
    public void InferDirection_ShouldReturnOutbound_WhenContextMatchesInternal()
    {
        _sut.InferDirection("from-internal", "100").Should().Be(CallDirection.Outbound);
    }

    [Fact]
    public void InferDirection_ShouldReturnInbound_WhenContextUnknown()
    {
        _sut.InferDirection("custom-context", "100").Should().Be(CallDirection.Inbound);
    }

    [Fact]
    public void IsLocalChannel_ShouldReturnTrue_ForLocalPrefix()
    {
        SessionCorrelator.IsLocalChannel("Local/100@default-00000001;1").Should().BeTrue();
    }

    [Fact]
    public void IsLocalChannel_ShouldReturnFalse_ForPjsip()
    {
        SessionCorrelator.IsLocalChannel("PJSIP/100-00000001").Should().BeFalse();
    }

    [Theory]
    [InlineData("Snoop/1794794992.7-00000001")]
    [InlineData("snoop/1794794992.7-00000001")]
    public void IsSnoopChannel_ShouldReturnTrue_WhenNameHasSnoopPrefix(string channelName)
    {
        SessionCorrelator.IsSnoopChannel(channelName).Should().BeTrue(
            "Asterisk names an ARI/app_snoop tap Snoop/<target uniqueid>-<n>, and the technology prefix is matched ignoring case");
    }

    [Theory]
    [InlineData("PJSIP/100-00000001")]
    [InlineData("Local/100@default-00000001;1")]
    [InlineData("SnoopX/1794794992.7-00000001")]
    [InlineData("")]
    public void IsSnoopChannel_ShouldReturnFalse_WhenNameIsNotASnoop(string channelName)
    {
        SessionCorrelator.IsSnoopChannel(channelName).Should().BeFalse(
            "only the exact Snoop/ technology prefix marks a snoop; other technologies, a longer name and an empty name do not");
    }

    [Fact]
    public void ExtractTechnology_ShouldParsePjsip()
    {
        SessionCorrelator.ExtractTechnology("PJSIP/100-00000001").Should().Be("PJSIP");
    }

    [Fact]
    public void ExtractTechnology_ShouldParseLocal()
    {
        SessionCorrelator.ExtractTechnology("Local/100@default-00000001;1").Should().Be("Local");
    }
}
