using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public class ActionPropertyTests
{
    [Fact]
    public void PingAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(PingAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Ping");
    }

    [Fact]
    public void PingAction_ShouldInheritFromManagerAction()
    {
        var action = new PingAction();
        action.Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void OriginateAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(OriginateAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Originate");
    }

    [Fact]
    public void OriginateAction_ShouldImplementEventGeneratingAction()
    {
        var action = new OriginateAction();
        action.Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void OriginateAction_ShouldHaveExpectedProperties()
    {
        var action = new OriginateAction
        {
            Channel = "SIP/2000",
            Context = "default",
            Exten = "100",
            Priority = 1,
            CallerId = "Test <1234>",
            Timeout = 30000,
            Async = true,
            Application = "Dial",
            Data = "SIP/3000"
        };

        action.Channel.Should().Be("SIP/2000");
        action.Context.Should().Be("default");
        action.Exten.Should().Be("100");
        action.Priority.Should().Be(1);
        action.CallerId.Should().Be("Test <1234>");
        action.Timeout.Should().Be(30000);
        action.Async.Should().BeTrue();
        action.Application.Should().Be("Dial");
        action.Data.Should().Be("SIP/3000");
    }

    [Fact]
    public void HangupAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(HangupAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Hangup");
    }

    [Fact]
    public void HangupAction_ShouldHaveChannelAndCauseProperties()
    {
        var action = new HangupAction { Channel = "SIP/2000-0001", Cause = 16 };
        action.Channel.Should().Be("SIP/2000-0001");
        action.Cause.Should().Be(16);
    }

    [Fact]
    public void CommandAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(CommandAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Command");
    }

    [Fact]
    public void CoreSettingsAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(CoreSettingsAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("CoreSettings");
    }

    [Fact]
    public void CoreStatusAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(CoreStatusAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("CoreStatus");
    }

    [Fact]
    public void QueueStatusAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(QueueStatusAction).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("QueueStatus");
    }

    [Fact]
    public void RedirectAction_ShouldHaveExpectedProperties()
    {
        var action = new RedirectAction
        {
            Channel = "SIP/2000-0001",
            Context = "default",
            Exten = "200",
            Priority = 1
        };

        action.Channel.Should().Be("SIP/2000-0001");
        action.Context.Should().Be("default");
        action.Exten.Should().Be("200");
        action.Priority.Should().Be(1);
    }

    [Fact]
    public void SetVarAction_ShouldHaveExpectedProperties()
    {
        var action = new SetVarAction
        {
            Channel = "SIP/2000-0001",
            Variable = "MYVAR",
            Value = "hello"
        };

        action.Channel.Should().Be("SIP/2000-0001");
        action.Variable.Should().Be("MYVAR");
        action.Value.Should().Be("hello");
    }

    [Fact]
    public void AllActions_ShouldInheritFromManagerAction()
    {
        typeof(PingAction).Should().BeAssignableTo<ManagerAction>();
        typeof(OriginateAction).Should().BeAssignableTo<ManagerAction>();
        typeof(HangupAction).Should().BeAssignableTo<ManagerAction>();
        typeof(CommandAction).Should().BeAssignableTo<ManagerAction>();
        typeof(CoreSettingsAction).Should().BeAssignableTo<ManagerAction>();
        typeof(CoreStatusAction).Should().BeAssignableTo<ManagerAction>();
        typeof(RedirectAction).Should().BeAssignableTo<ManagerAction>();
        typeof(SetVarAction).Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void ManagerAction_ActionId_ShouldBeSettable()
    {
        var action = new PingAction { ActionId = "test-123" };
        action.ActionId.Should().Be("test-123");
    }
}
