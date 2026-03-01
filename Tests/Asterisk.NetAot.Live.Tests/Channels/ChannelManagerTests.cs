using Asterisk.NetAot.Abstractions.Enums;
using Asterisk.NetAot.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.NetAot.Live.Tests.Channels;

public class ChannelManagerTests
{
    private readonly ChannelManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnNewChannel_ShouldAddChannel()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Ringing, "5551234", "John");

        _sut.ChannelCount.Should().Be(1);
        var ch = _sut.GetByUniqueId("123.1");
        ch.Should().NotBeNull();
        ch!.Name.Should().Be("PJSIP/2000-001");
        ch.CallerIdNum.Should().Be("5551234");
    }

    [Fact]
    public void OnNewState_ShouldUpdateState()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Ringing);
        _sut.OnNewState("123.1", ChannelState.Up);

        _sut.GetByUniqueId("123.1")!.State.Should().Be(ChannelState.Up);
    }

    [Fact]
    public void OnHangup_ShouldRemoveChannel()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnHangup("123.1", HangupCause.NormalClearing);

        _sut.ActiveChannels.Should().BeEmpty();
    }

    [Fact]
    public void OnRename_ShouldUpdateName()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnRename("123.1", "PJSIP/2000-001<MASQ>");

        _sut.GetByUniqueId("123.1")!.Name.Should().Be("PJSIP/2000-001<MASQ>");
    }

    [Fact]
    public void OnLink_ShouldSetLinkedChannels()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnNewChannel("123.2", "PJSIP/3000-002", ChannelState.Up);
        _sut.OnLink("123.1", "123.2");

        var ch1 = _sut.GetByUniqueId("123.1");
        var ch2 = _sut.GetByUniqueId("123.2");
        ch1!.LinkedChannel.Should().BeSameAs(ch2);
        ch2!.LinkedChannel.Should().BeSameAs(ch1);
    }

    [Fact]
    public void OnUnlink_ShouldClearLinkedChannels()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnNewChannel("123.2", "PJSIP/3000-002", ChannelState.Up);
        _sut.OnLink("123.1", "123.2");
        _sut.OnUnlink("123.1", "123.2");

        _sut.GetByUniqueId("123.1")!.LinkedChannel.Should().BeNull();
        _sut.GetByUniqueId("123.2")!.LinkedChannel.Should().BeNull();
    }

    [Fact]
    public void Events_ShouldFire()
    {
        AsteriskChannel? added = null;
        AsteriskChannel? removed = null;
        _sut.ChannelAdded += ch => added = ch;
        _sut.ChannelRemoved += ch => removed = ch;

        _sut.OnNewChannel("123.1", "PJSIP/2000", ChannelState.Up);
        added.Should().NotBeNull();

        _sut.OnHangup("123.1");
        removed.Should().NotBeNull();
    }

    [Fact]
    public void GetByName_ShouldReturnChannel()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);

        _sut.GetByName("PJSIP/2000-001").Should().NotBeNull();
        _sut.GetByName("PJSIP/2000-001")!.UniqueId.Should().Be("123.1");
    }

    [Fact]
    public void GetByName_ShouldTrackRenames()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnRename("123.1", "PJSIP/2000-001<MASQ>");

        _sut.GetByName("PJSIP/2000-001").Should().BeNull();
        _sut.GetByName("PJSIP/2000-001<MASQ>").Should().NotBeNull();
    }

    [Fact]
    public void GetByName_ShouldReturnNull_AfterHangup()
    {
        _sut.OnNewChannel("123.1", "PJSIP/2000-001", ChannelState.Up);
        _sut.OnHangup("123.1");

        _sut.GetByName("PJSIP/2000-001").Should().BeNull();
    }
}
