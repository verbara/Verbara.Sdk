using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public class SprintCActionTests
{
    // ── C1: Voicemail ────────────────────────────────────────────────────────

    [Fact]
    public void VoicemailRefreshAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(VoicemailRefreshAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("VoicemailRefresh");
    }

    [Fact]
    public void VoicemailRefreshAction_ShouldInheritFromManagerAction()
    {
        new VoicemailRefreshAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void VoicemailRefreshAction_ShouldNotImplementEventGeneratingAction()
    {
        new VoicemailRefreshAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void VoicemailRefreshAction_ShouldHaveExpectedProperties()
    {
        var action = new VoicemailRefreshAction { Mailbox = "1001", Context = "default" };
        action.Mailbox.Should().Be("1001");
        action.Context.Should().Be("default");
    }

    [Fact]
    public void VoicemailUserStatusAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(VoicemailUserStatusAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("VoicemailUserStatus");
    }

    [Fact]
    public void VoicemailUserStatusAction_ShouldImplementEventGeneratingAction()
    {
        new VoicemailUserStatusAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void VoicemailUserStatusAction_ShouldHaveExpectedProperties()
    {
        var action = new VoicemailUserStatusAction { Mailbox = "2001", Context = "voicemail" };
        action.Mailbox.Should().Be("2001");
        action.Context.Should().Be("voicemail");
    }

    // ── C2: Presence ─────────────────────────────────────────────────────────

    [Fact]
    public void PresenceStateAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(PresenceStateAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PresenceState");
    }

    [Fact]
    public void PresenceStateAction_ShouldInheritFromManagerAction()
    {
        new PresenceStateAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void PresenceStateAction_ShouldNotImplementEventGeneratingAction()
    {
        new PresenceStateAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void PresenceStateAction_ShouldHaveProviderProperty()
    {
        var action = new PresenceStateAction { Provider = "CustomPresence:1234" };
        action.Provider.Should().Be("CustomPresence:1234");
    }

    [Fact]
    public void PresenceStateListAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(PresenceStateListAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PresenceStateList");
    }

    [Fact]
    public void PresenceStateListAction_ShouldImplementEventGeneratingAction()
    {
        new PresenceStateListAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    // ── C3: Queue ────────────────────────────────────────────────────────────

    [Fact]
    public void QueueReloadAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(QueueReloadAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("QueueReload");
    }

    [Fact]
    public void QueueReloadAction_ShouldInheritFromManagerAction()
    {
        new QueueReloadAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void QueueReloadAction_ShouldNotImplementEventGeneratingAction()
    {
        new QueueReloadAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void QueueReloadAction_ShouldHaveExpectedProperties()
    {
        var action = new QueueReloadAction
        {
            Queue = "sales",
            Members = "yes",
            Rules = "yes",
            Parameters = "yes"
        };
        action.Queue.Should().Be("sales");
        action.Members.Should().Be("yes");
        action.Rules.Should().Be("yes");
        action.Parameters.Should().Be("yes");
    }

    [Fact]
    public void QueueRuleAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(QueueRuleAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("QueueRule");
    }

    [Fact]
    public void QueueRuleAction_ShouldImplementEventGeneratingAction()
    {
        new QueueRuleAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void QueueRuleAction_ShouldHaveRuleProperty()
    {
        var action = new QueueRuleAction { Rule = "myrule" };
        action.Rule.Should().Be("myrule");
    }

    // ── C4: Database ─────────────────────────────────────────────────────────

    [Fact]
    public void DbGetTreeAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(DbGetTreeAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("DBGetTree");
    }

    [Fact]
    public void DbGetTreeAction_ShouldImplementEventGeneratingAction()
    {
        new DbGetTreeAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void DbGetTreeAction_ShouldHaveExpectedProperties()
    {
        var action = new DbGetTreeAction { Family = "myfamily", Key = "mykey" };
        action.Family.Should().Be("myfamily");
        action.Key.Should().Be("mykey");
    }

    // ── C5: Miscellaneous ────────────────────────────────────────────────────

    [Fact]
    public void CoreShowChannelMapAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(CoreShowChannelMapAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("CoreShowChannelMap");
    }

    [Fact]
    public void CoreShowChannelMapAction_ShouldImplementEventGeneratingAction()
    {
        new CoreShowChannelMapAction().Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void CoreShowChannelMapAction_ShouldHaveChannelProperty()
    {
        var action = new CoreShowChannelMapAction { Channel = "PJSIP/1001-00000001" };
        action.Channel.Should().Be("PJSIP/1001-00000001");
    }

    [Fact]
    public void SendFlashAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(SendFlashAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Flash");
    }

    [Fact]
    public void SendFlashAction_ShouldInheritFromManagerAction()
    {
        new SendFlashAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void SendFlashAction_ShouldNotImplementEventGeneratingAction()
    {
        new SendFlashAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void SendFlashAction_ShouldHaveChannelProperty()
    {
        var action = new SendFlashAction { Channel = "DAHDI/1-1" };
        action.Channel.Should().Be("DAHDI/1-1");
    }

    [Fact]
    public void DialplanExtensionAddAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(DialplanExtensionAddAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("DialplanExtensionAdd");
    }

    [Fact]
    public void DialplanExtensionAddAction_ShouldInheritFromManagerAction()
    {
        new DialplanExtensionAddAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void DialplanExtensionAddAction_ShouldNotImplementEventGeneratingAction()
    {
        new DialplanExtensionAddAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void DialplanExtensionAddAction_ShouldHaveExpectedProperties()
    {
        var action = new DialplanExtensionAddAction
        {
            Context = "default",
            Extension = "1000",
            Priority = 1,
            Application = "Dial",
            ApplicationData = "PJSIP/1000",
            Replace = true
        };
        action.Context.Should().Be("default");
        action.Extension.Should().Be("1000");
        action.Priority.Should().Be(1);
        action.Application.Should().Be("Dial");
        action.ApplicationData.Should().Be("PJSIP/1000");
        action.Replace.Should().BeTrue();
    }

    [Fact]
    public void DialplanExtensionRemoveAction_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(DialplanExtensionRemoveAction)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("DialplanExtensionRemove");
    }

    [Fact]
    public void DialplanExtensionRemoveAction_ShouldInheritFromManagerAction()
    {
        new DialplanExtensionRemoveAction().Should().BeAssignableTo<ManagerAction>();
    }

    [Fact]
    public void DialplanExtensionRemoveAction_ShouldNotImplementEventGeneratingAction()
    {
        new DialplanExtensionRemoveAction().Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void DialplanExtensionRemoveAction_ShouldHaveExpectedProperties()
    {
        var action = new DialplanExtensionRemoveAction
        {
            Context = "default",
            Extension = "1000",
            Priority = 1
        };
        action.Context.Should().Be("default");
        action.Extension.Should().Be("1000");
        action.Priority.Should().Be(1);
    }

    // ── Response Events ──────────────────────────────────────────────────────

    [Fact]
    public void QueueRuleEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(QueueRuleEvent)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("QueueRule");
    }

    [Fact]
    public void QueueRuleListCompleteEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(QueueRuleListCompleteEvent)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("QueueRuleListComplete");
    }

    [Fact]
    public void DbGetTreeResponseEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(DbGetTreeResponseEvent)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("DBGetTreeResponse");
    }

    [Fact]
    public void CoreShowChannelMapCompleteEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(CoreShowChannelMapCompleteEvent)
            .GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("CoreShowChannelMapComplete");
    }
}
