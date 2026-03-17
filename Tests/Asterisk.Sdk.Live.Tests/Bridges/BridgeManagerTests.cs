using Asterisk.Sdk.Live.Bridges;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Bridges;

public sealed class BridgeManagerTests
{
    private readonly BridgeManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnBridgeCreated_ShouldAddBridge()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", "simple_bridge", "dial", "test");
        _sut.GetById("bridge-1").Should().NotBeNull();
        _sut.GetById("bridge-1")!.BridgeType.Should().Be("basic");
    }

    [Fact]
    public void OnChannelEntered_ShouldAddChannelToBridge()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.Channels.Should().ContainKey("chan-001");
        bridge.NumChannels.Should().Be(1);
    }

    [Fact]
    public void OnChannelEntered_ShouldUpdateReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        _sut.GetBridgeForChannel("chan-001").Should().NotBeNull();
        _sut.GetBridgeForChannel("chan-001")!.BridgeUniqueid.Should().Be("bridge-1");
    }

    [Fact]
    public void OnChannelLeft_ShouldRemoveFromBridgeAndReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.OnChannelLeft("bridge-1", "chan-001");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.Channels.Should().BeEmpty();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldMarkDestroyedAndCleanReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.OnBridgeDestroyed("bridge-1");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.DestroyedAt.Should().NotBeNull();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
        _sut.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void ActiveBridges_ShouldExcludeDestroyed()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnBridgeCreated("bridge-2", "basic", null, null, null);
        _sut.OnBridgeDestroyed("bridge-1");

        _sut.ActiveBridges.Should().HaveCount(1);
    }

    [Fact]
    public void OnBridgeCreated_ShouldFireEvent()
    {
        AsteriskBridge? fired = null;
        _sut.BridgeCreated += b => fired = b;

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnChannelEntered_ShouldFireEvent()
    {
        AsteriskBridge? firedBridge = null;
        string? firedUniqueId = null;
        _sut.ChannelEntered += (b, uid) => { firedBridge = b; firedUniqueId = uid; };

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        firedBridge.Should().NotBeNull();
        firedUniqueId.Should().Be("chan-001");
    }

    [Fact]
    public void Clear_ShouldRemoveAllBridgesAndReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.Clear();

        _sut.GetById("bridge-1").Should().BeNull();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
    }
}
