using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public class PJSipActionTests
{
    // PJSipShowAorsAction

    [Fact]
    public void PJSipShowAorsAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowAorsAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowAors");
    }

    [Fact]
    public void PJSipShowAorsAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowAorsAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowAorsAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowAorsAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowAuthsAction

    [Fact]
    public void PJSipShowAuthsAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowAuthsAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowAuths");
    }

    [Fact]
    public void PJSipShowAuthsAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowAuthsAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowAuthsAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowAuthsAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowRegistrationsInboundAction

    [Fact]
    public void PJSipShowRegistrationsInboundAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowRegistrationsInboundAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowRegistrationsInbound");
    }

    [Fact]
    public void PJSipShowRegistrationsInboundAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowRegistrationsInboundAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowRegistrationsInboundAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowRegistrationsInboundAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowRegistrationsOutboundAction

    [Fact]
    public void PJSipShowRegistrationsOutboundAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowRegistrationsOutboundAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowRegistrationsOutbound");
    }

    [Fact]
    public void PJSipShowRegistrationsOutboundAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowRegistrationsOutboundAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowRegistrationsOutboundAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowRegistrationsOutboundAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowResourceListsAction

    [Fact]
    public void PJSipShowResourceListsAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowResourceListsAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowResourceLists");
    }

    [Fact]
    public void PJSipShowResourceListsAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowResourceListsAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowResourceListsAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowResourceListsAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowSubscriptionsInboundAction

    [Fact]
    public void PJSipShowSubscriptionsInboundAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowSubscriptionsInboundAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowSubscriptionsInbound");
    }

    [Fact]
    public void PJSipShowSubscriptionsInboundAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowSubscriptionsInboundAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowSubscriptionsInboundAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowSubscriptionsInboundAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipShowSubscriptionsOutboundAction

    [Fact]
    public void PJSipShowSubscriptionsOutboundAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipShowSubscriptionsOutboundAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPShowSubscriptionsOutbound");
    }

    [Fact]
    public void PJSipShowSubscriptionsOutboundAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipShowSubscriptionsOutboundAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipShowSubscriptionsOutboundAction_ShouldImplementIEventGeneratingAction()
    {
        typeof(PJSipShowSubscriptionsOutboundAction).Should().Implement<IEventGeneratingAction>();
    }

    // PJSipRegisterAction

    [Fact]
    public void PJSipRegisterAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipRegisterAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPRegister");
    }

    [Fact]
    public void PJSipRegisterAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipRegisterAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipRegisterAction_ShouldNotImplementIEventGeneratingAction()
    {
        typeof(PJSipRegisterAction).Should().NotImplement<IEventGeneratingAction>();
    }

    [Fact]
    public void PJSipRegisterAction_ShouldHaveRegistrationProperty_WhenSet()
    {
        var action = new PJSipRegisterAction { Registration = "trunk-outbound" };
        action.Registration.Should().Be("trunk-outbound");
    }

    // PJSipUnregisterAction

    [Fact]
    public void PJSipUnregisterAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipUnregisterAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPUnregister");
    }

    [Fact]
    public void PJSipUnregisterAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipUnregisterAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipUnregisterAction_ShouldNotImplementIEventGeneratingAction()
    {
        typeof(PJSipUnregisterAction).Should().NotImplement<IEventGeneratingAction>();
    }

    [Fact]
    public void PJSipUnregisterAction_ShouldHaveRegistrationProperty_WhenSet()
    {
        var action = new PJSipUnregisterAction { Registration = "trunk-outbound" };
        action.Registration.Should().Be("trunk-outbound");
    }

    // PJSipQualifyAction

    [Fact]
    public void PJSipQualifyAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipQualifyAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPQualify");
    }

    [Fact]
    public void PJSipQualifyAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipQualifyAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipQualifyAction_ShouldNotImplementIEventGeneratingAction()
    {
        typeof(PJSipQualifyAction).Should().NotImplement<IEventGeneratingAction>();
    }

    [Fact]
    public void PJSipQualifyAction_ShouldHaveEndpointProperty_WhenSet()
    {
        var action = new PJSipQualifyAction { Endpoint = "alice" };
        action.Endpoint.Should().Be("alice");
    }

    // PJSipHangupAction

    [Fact]
    public void PJSipHangupAction_ShouldHaveCorrectAsteriskMapping()
    {
        var attr = typeof(PJSipHangupAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PJSIPHangup");
    }

    [Fact]
    public void PJSipHangupAction_ShouldInheritFromManagerAction()
    {
        typeof(PJSipHangupAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PJSipHangupAction_ShouldNotImplementIEventGeneratingAction()
    {
        typeof(PJSipHangupAction).Should().NotImplement<IEventGeneratingAction>();
    }

    [Fact]
    public void PJSipHangupAction_ShouldHaveChannelAndCauseProperties_WhenSet()
    {
        var action = new PJSipHangupAction { Channel = "PJSIP/alice-0001", Cause = 16 };
        action.Channel.Should().Be("PJSIP/alice-0001");
        action.Cause.Should().Be(16);
    }
}
