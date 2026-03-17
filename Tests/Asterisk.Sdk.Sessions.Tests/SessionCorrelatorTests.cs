// Tests/Asterisk.Sdk.Sessions.Tests/SessionCorrelatorTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

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
