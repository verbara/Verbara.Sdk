using Asterisk.Sdk.Live.Bridges;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Bridges;

public sealed class BridgeManagerExtendedTests
{
    private readonly BridgeManager _sut = new(NullLogger.Instance);

    // ── Duplicate bridge ──

    [Fact]
    public void OnBridgeCreated_ShouldNotAdd_WhenDuplicateBridgeId()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", "simple_bridge", "dial", "first");
        _sut.OnBridgeCreated("bridge-1", "mixing", "softmix", "confbridge", "second");

        _sut.BridgeCount.Should().Be(1);
        _sut.GetById("bridge-1")!.BridgeType.Should().Be("basic");
        _sut.GetById("bridge-1")!.Name.Should().Be("first");
    }

    [Fact]
    public void OnBridgeCreated_ShouldNotFireEvent_WhenDuplicate()
    {
        var eventCount = 0;
        _sut.BridgeCreated += _ => eventCount++;

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);

        eventCount.Should().Be(1);
    }

    // ── Unknown bridge edge cases ──

    [Fact]
    public void OnChannelEntered_ShouldNotThrow_WhenBridgeUnknown()
    {
        var fired = false;
        _sut.ChannelEntered += (_, _) => fired = true;

        var act = () => _sut.OnChannelEntered("nonexistent", "chan-001");
        act.Should().NotThrow();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnChannelLeft_ShouldNotThrow_WhenBridgeUnknown()
    {
        var fired = false;
        _sut.ChannelLeft += (_, _) => fired = true;

        var act = () => _sut.OnChannelLeft("nonexistent", "chan-001");
        act.Should().NotThrow();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldNotThrow_WhenBridgeUnknown()
    {
        var fired = false;
        _sut.BridgeDestroyed += _ => fired = true;

        var act = () => _sut.OnBridgeDestroyed("nonexistent");
        act.Should().NotThrow();
        fired.Should().BeFalse();
    }

    // ── Transfer events ──

    [Fact]
    public void OnBlindTransfer_ShouldFireTransferOccurred()
    {
        BridgeTransferInfo? fired = null;
        _sut.TransferOccurred += info => fired = info;

        _sut.OnBlindTransfer("bridge-1", "PJSIP/200-00000001", "300", "default");

        fired.Should().NotBeNull();
        fired!.BridgeId.Should().Be("bridge-1");
        fired.TransferType.Should().Be("Blind");
        fired.TargetChannel.Should().Be("PJSIP/200-00000001");
        fired.SecondBridgeId.Should().BeNull();
    }

    [Fact]
    public void OnAttendedTransfer_ShouldFireTransferOccurred()
    {
        BridgeTransferInfo? fired = null;
        _sut.TransferOccurred += info => fired = info;

        _sut.OnAttendedTransfer("bridge-1", "bridge-2", "Bridge", "Success");

        fired.Should().NotBeNull();
        fired!.BridgeId.Should().Be("bridge-1");
        fired.TransferType.Should().Be("Attended");
        fired.SecondBridgeId.Should().Be("bridge-2");
        fired.DestType.Should().Be("Bridge");
        fired.Result.Should().Be("Success");
    }

    // ── BridgeDestroyed event ──

    [Fact]
    public void OnBridgeDestroyed_ShouldFireBridgeDestroyedEvent()
    {
        AsteriskBridge? fired = null;
        _sut.BridgeDestroyed += b => fired = b;

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnBridgeDestroyed("bridge-1");

        fired.Should().NotBeNull();
        fired!.BridgeUniqueid.Should().Be("bridge-1");
        fired.DestroyedAt.Should().NotBeNull();
    }

    // ── ChannelLeft event ──

    [Fact]
    public void OnChannelLeft_ShouldFireChannelLeftEvent()
    {
        AsteriskBridge? firedBridge = null;
        string? firedUniqueId = null;
        _sut.ChannelLeft += (b, uid) => { firedBridge = b; firedUniqueId = uid; };

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.OnChannelLeft("bridge-1", "chan-001");

        firedBridge.Should().NotBeNull();
        firedBridge!.BridgeUniqueid.Should().Be("bridge-1");
        firedUniqueId.Should().Be("chan-001");
    }
}
