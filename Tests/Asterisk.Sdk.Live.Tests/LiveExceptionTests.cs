using FluentAssertions;

namespace Asterisk.Sdk.Live.Tests;

public sealed class LiveExceptionTests
{
    [Fact]
    public void LiveException_ShouldStoreMessage()
    {
        var ex = new LiveException("test error");

        ex.Message.Should().Be("test error");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void LiveException_ShouldStoreInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new LiveException("outer", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void AmiCommunicationException_ShouldBeLiveException()
    {
        var ex = new AmiCommunicationException("ami error");

        ex.Should().BeAssignableTo<LiveException>();
        ex.Message.Should().Be("ami error");
    }

    [Fact]
    public void AmiCommunicationException_ShouldStoreInnerException()
    {
        var inner = new TimeoutException("timeout");
        var ex = new AmiCommunicationException("comm error", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ChannelNotFoundException_ShouldIncludeChannelName()
    {
        var ex = new ChannelNotFoundException("PJSIP/2000-001");

        ex.Message.Should().Contain("PJSIP/2000-001");
        ex.Should().BeAssignableTo<LiveException>();
    }

    [Fact]
    public void InterfaceNotFoundException_ShouldIncludeInterfaceName()
    {
        var ex = new InterfaceNotFoundException("PJSIP/2000");

        ex.Message.Should().Contain("PJSIP/2000");
        ex.Should().BeAssignableTo<LiveException>();
    }

    [Fact]
    public void InvalidPenaltyException_ShouldIncludePenaltyValue()
    {
        var ex = new InvalidPenaltyException(-1);

        ex.Message.Should().Contain("-1");
        ex.Should().BeAssignableTo<LiveException>();
    }
}
