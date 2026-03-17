using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Channels;

public sealed class ChannelManagerExtendedTests
{
    private readonly ChannelManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnNewChannel_ShouldStoreLinkedId()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring, linkedId: "linked-1");
        _sut.GetByUniqueId("uid-1")!.LinkedId.Should().Be("linked-1");
    }

    [Fact]
    public void OnDialBegin_ShouldSetDialedChannel()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);
        _sut.OnDialBegin("uid-1", "uid-2", "PJSIP/200-001", "PJSIP/200");
        _sut.GetByUniqueId("uid-1")!.DialedChannel.Should().Be("PJSIP/200-001");
    }

    [Fact]
    public void OnDialBegin_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelDialBegin += c => fired = c;
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);
        _sut.OnDialBegin("uid-1", "uid-2", "PJSIP/200-001", null);
        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnDialEnd_ShouldSetDialStatus()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);
        _sut.OnDialEnd("uid-1", "ANSWER");
        _sut.GetByUniqueId("uid-1")!.DialStatus.Should().Be("ANSWER");
    }

    [Fact]
    public void OnHold_ShouldSetIsOnHold()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", "default");
        var ch = _sut.GetByUniqueId("uid-1")!;
        ch.IsOnHold.Should().BeTrue();
        ch.HoldMusicClass.Should().Be("default");
    }

    [Fact]
    public void OnUnhold_ShouldClearIsOnHold()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", "default");
        _sut.OnUnhold("uid-1");
        _sut.GetByUniqueId("uid-1")!.IsOnHold.Should().BeFalse();
    }

    [Fact]
    public void OnHold_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelHeld += c => fired = c;
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", null);
        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnUnhold_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelUnheld += c => fired = c;
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", null);
        _sut.OnUnhold("uid-1");
        fired.Should().NotBeNull();
    }
}
