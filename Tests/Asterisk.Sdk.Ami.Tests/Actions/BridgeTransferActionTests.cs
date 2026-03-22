using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public class BridgeTransferActionTests
{
    // BridgeDestroyAction
    [Fact]
    public void BridgeDestroyAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeDestroyAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeDestroy");
    }

    [Fact]
    public void BridgeDestroyAction_ShouldInheritFromManagerAction()
    {
        new BridgeDestroyAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeDestroyAction_ShouldNotImplementIEventGeneratingAction()
    {
        new BridgeDestroyAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BridgeDestroyAction_ShouldSetBridgeUniqueid()
    {
        var action = new BridgeDestroyAction { BridgeUniqueid = "bridge-001" };
        action.BridgeUniqueid.Should().Be("bridge-001");
    }

    // BridgeInfoAction
    [Fact]
    public void BridgeInfoAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeInfoAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeInfo");
    }

    [Fact]
    public void BridgeInfoAction_ShouldInheritFromManagerAction()
    {
        new BridgeInfoAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeInfoAction_ShouldImplementIEventGeneratingAction()
    {
        new BridgeInfoAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BridgeInfoAction_ShouldSetBridgeUniqueid()
    {
        var action = new BridgeInfoAction { BridgeUniqueid = "bridge-002" };
        action.BridgeUniqueid.Should().Be("bridge-002");
    }

    // BridgeKickAction
    [Fact]
    public void BridgeKickAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeKickAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeKick");
    }

    [Fact]
    public void BridgeKickAction_ShouldInheritFromManagerAction()
    {
        new BridgeKickAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeKickAction_ShouldNotImplementIEventGeneratingAction()
    {
        new BridgeKickAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BridgeKickAction_ShouldSetProperties()
    {
        var action = new BridgeKickAction { BridgeUniqueid = "bridge-003", Channel = "SIP/2000-0001" };
        action.BridgeUniqueid.Should().Be("bridge-003");
        action.Channel.Should().Be("SIP/2000-0001");
    }

    // BridgeListAction
    [Fact]
    public void BridgeListAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeListAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeList");
    }

    [Fact]
    public void BridgeListAction_ShouldInheritFromManagerAction()
    {
        new BridgeListAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeListAction_ShouldImplementIEventGeneratingAction()
    {
        new BridgeListAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    // BridgeTechnologyListAction
    [Fact]
    public void BridgeTechnologyListAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeTechnologyListAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeTechnologyList");
    }

    [Fact]
    public void BridgeTechnologyListAction_ShouldInheritFromManagerAction()
    {
        new BridgeTechnologyListAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeTechnologyListAction_ShouldImplementIEventGeneratingAction()
    {
        new BridgeTechnologyListAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    // BridgeTechnologySuspendAction
    [Fact]
    public void BridgeTechnologySuspendAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeTechnologySuspendAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeTechnologySuspend");
    }

    [Fact]
    public void BridgeTechnologySuspendAction_ShouldInheritFromManagerAction()
    {
        new BridgeTechnologySuspendAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeTechnologySuspendAction_ShouldNotImplementIEventGeneratingAction()
    {
        new BridgeTechnologySuspendAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BridgeTechnologySuspendAction_ShouldSetBridgeTechnology()
    {
        var action = new BridgeTechnologySuspendAction { BridgeTechnology = "simple_bridge" };
        action.BridgeTechnology.Should().Be("simple_bridge");
    }

    // BridgeTechnologyUnsuspendAction
    [Fact]
    public void BridgeTechnologyUnsuspendAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BridgeTechnologyUnsuspendAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BridgeTechnologyUnsuspend");
    }

    [Fact]
    public void BridgeTechnologyUnsuspendAction_ShouldInheritFromManagerAction()
    {
        new BridgeTechnologyUnsuspendAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BridgeTechnologyUnsuspendAction_ShouldNotImplementIEventGeneratingAction()
    {
        new BridgeTechnologyUnsuspendAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BridgeTechnologyUnsuspendAction_ShouldSetBridgeTechnology()
    {
        var action = new BridgeTechnologyUnsuspendAction { BridgeTechnology = "softmix" };
        action.BridgeTechnology.Should().Be("softmix");
    }

    // BlindTransferAction
    [Fact]
    public void BlindTransferAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(BlindTransferAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("BlindTransfer");
    }

    [Fact]
    public void BlindTransferAction_ShouldInheritFromManagerAction()
    {
        new BlindTransferAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void BlindTransferAction_ShouldNotImplementIEventGeneratingAction()
    {
        new BlindTransferAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void BlindTransferAction_ShouldSetProperties()
    {
        var action = new BlindTransferAction
        {
            Channel = "SIP/2000-0001",
            Context = "default",
            Exten = "300"
        };
        action.Channel.Should().Be("SIP/2000-0001");
        action.Context.Should().Be("default");
        action.Exten.Should().Be("300");
    }

    // CancelAtxferAction
    [Fact]
    public void CancelAtxferAction_ShouldHaveCorrectMapping()
    {
        var attr = typeof(CancelAtxferAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("CancelAtxfer");
    }

    [Fact]
    public void CancelAtxferAction_ShouldInheritFromManagerAction()
    {
        new CancelAtxferAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void CancelAtxferAction_ShouldNotImplementIEventGeneratingAction()
    {
        new CancelAtxferAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void CancelAtxferAction_ShouldSetChannel()
    {
        var action = new CancelAtxferAction { Channel = "SIP/3000-0001" };
        action.Channel.Should().Be("SIP/3000-0001");
    }
}
