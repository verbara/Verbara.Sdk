using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public class ActiveBridgeTrackerTests
{
    private readonly ActiveBridgeTracker _tracker = new();

    [Fact]
    public void GetBridge_ShouldReturnNull_WhenBridgeDoesNotExist()
    {
        _tracker.GetBridge("nonexistent").Should().BeNull();
    }

    [Fact]
    public void ActiveBridges_ShouldBeEmpty_Initially()
    {
        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void OnBridgeCreated_ShouldAddBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        var bridge = _tracker.GetBridge("bridge-1");
        bridge.Should().NotBeNull();
        bridge!.BridgeUniqueId.Should().Be("bridge-1");
        bridge.BridgeType.Should().Be("mixing");
        bridge.BridgeTechnology.Should().Be("simple_bridge");
    }

    [Fact]
    public void OnBridgeCreated_ShouldAddBridgeToActiveBridges()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.ActiveBridges.Should().HaveCount(1);
        _tracker.ActiveBridges.Should().Contain(b => b.BridgeUniqueId == "bridge-1");
    }

    [Fact]
    public void OnBridgeCreated_ShouldInitializeWithEmptyChannels()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.GetBridge("bridge-1")!.Channels.Should().BeEmpty();
    }

    [Fact]
    public void OnBridgeCreated_ShouldHandleNullTypeAndTechnology()
    {
        _tracker.OnBridgeCreated("bridge-1", null, null);

        var bridge = _tracker.GetBridge("bridge-1");
        bridge.Should().NotBeNull();
        bridge!.BridgeType.Should().BeNull();
        bridge.BridgeTechnology.Should().BeNull();
    }

    [Fact]
    public void OnBridgeCreated_ShouldReplaceBridge_WhenSameIdExists()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        _tracker.OnBridgeCreated("bridge-1", "holding", "native_rtp");

        var bridge = _tracker.GetBridge("bridge-1");
        bridge!.BridgeType.Should().Be("holding");
        bridge.BridgeTechnology.Should().Be("native_rtp");
        _tracker.ActiveBridges.Should().HaveCount(1);
    }

    [Fact]
    public void OnChannelEntered_ShouldAddChannelToBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");

        _tracker.GetBridge("bridge-1")!.Channels.Should().Contain("SIP/100-0000001");
    }

    [Fact]
    public void OnChannelEntered_ShouldAddMultipleChannels()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");
        _tracker.OnChannelEntered("bridge-1", "SIP/200-0000002");

        var channels = _tracker.GetBridge("bridge-1")!.Channels;
        channels.Should().HaveCount(2);
        channels.Should().Contain("SIP/100-0000001");
        channels.Should().Contain("SIP/200-0000002");
    }

    [Fact]
    public void OnChannelEntered_ShouldNotDuplicateChannel()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");
        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");

        _tracker.GetBridge("bridge-1")!.Channels.Should().HaveCount(1);
    }

    [Fact]
    public void OnChannelEntered_ShouldDoNothing_WhenBridgeDoesNotExist()
    {
        // Should not throw
        _tracker.OnChannelEntered("nonexistent", "SIP/100-0000001");

        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void OnChannelLeft_ShouldRemoveChannelFromBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");
        _tracker.OnChannelEntered("bridge-1", "SIP/200-0000002");

        _tracker.OnChannelLeft("bridge-1", "SIP/100-0000001");

        var channels = _tracker.GetBridge("bridge-1")!.Channels;
        channels.Should().HaveCount(1);
        channels.Should().NotContain("SIP/100-0000001");
        channels.Should().Contain("SIP/200-0000002");
    }

    [Fact]
    public void OnChannelLeft_ShouldDoNothing_WhenChannelNotInBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        // Should not throw
        _tracker.OnChannelLeft("bridge-1", "SIP/999-0000099");

        _tracker.GetBridge("bridge-1")!.Channels.Should().BeEmpty();
    }

    [Fact]
    public void OnChannelLeft_ShouldDoNothing_WhenBridgeDoesNotExist()
    {
        // Should not throw
        _tracker.OnChannelLeft("nonexistent", "SIP/100-0000001");

        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldRemoveBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");

        _tracker.OnBridgeDestroyed("bridge-1");

        _tracker.GetBridge("bridge-1").Should().BeNull();
        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldDoNothing_WhenBridgeDoesNotExist()
    {
        // Should not throw
        _tracker.OnBridgeDestroyed("nonexistent");

        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldOnlyRemoveSpecifiedBridge()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        _tracker.OnBridgeCreated("bridge-2", "holding", "native_rtp");

        _tracker.OnBridgeDestroyed("bridge-1");

        _tracker.GetBridge("bridge-1").Should().BeNull();
        _tracker.GetBridge("bridge-2").Should().NotBeNull();
        _tracker.ActiveBridges.Should().HaveCount(1);
    }

    [Fact]
    public void Clear_ShouldRemoveAllBridges()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        _tracker.OnBridgeCreated("bridge-2", "holding", "native_rtp");
        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");

        _tracker.Clear();

        _tracker.ActiveBridges.Should().BeEmpty();
        _tracker.GetBridge("bridge-1").Should().BeNull();
        _tracker.GetBridge("bridge-2").Should().BeNull();
    }

    [Fact]
    public void FullLifecycle_ShouldTrackBridgeFromCreateToDestroy()
    {
        // Create
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        _tracker.ActiveBridges.Should().HaveCount(1);

        // Channels enter
        _tracker.OnChannelEntered("bridge-1", "SIP/100-0000001");
        _tracker.OnChannelEntered("bridge-1", "SIP/200-0000002");
        _tracker.GetBridge("bridge-1")!.Channels.Should().HaveCount(2);

        // One channel leaves
        _tracker.OnChannelLeft("bridge-1", "SIP/100-0000001");
        _tracker.GetBridge("bridge-1")!.Channels.Should().HaveCount(1);

        // Bridge destroyed
        _tracker.OnBridgeDestroyed("bridge-1");
        _tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void ActiveBridges_ShouldReturnSnapshot_NotLiveReference()
    {
        _tracker.OnBridgeCreated("bridge-1", "mixing", "simple_bridge");
        var snapshot = _tracker.ActiveBridges;

        _tracker.OnBridgeCreated("bridge-2", "holding", "native_rtp");

        // The snapshot should not reflect the new bridge
        snapshot.Should().HaveCount(1);
        _tracker.ActiveBridges.Should().HaveCount(2);
    }
}
