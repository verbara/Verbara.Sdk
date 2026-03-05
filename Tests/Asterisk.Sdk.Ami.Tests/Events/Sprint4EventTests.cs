using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public class Sprint4EventTests
{
    // --- PJSIP detail events ---

    [Fact]
    public void IdentifyDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(IdentifyDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("IdentifyDetail");
    }

    [Fact]
    public void IdentifyDetailEvent_ShouldHaveProperties()
    {
        var evt = new IdentifyDetailEvent
        {
            ObjectType = "identify",
            ObjectName = "2000_identify",
            Endpoint = "2000",
            Match = "10.0.0.0/24"
        };
        evt.ObjectType.Should().Be("identify");
        evt.Endpoint.Should().Be("2000");
        evt.Match.Should().Be("10.0.0.0/24");
    }

    [Fact]
    public void InboundRegistrationDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(InboundRegistrationDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("InboundRegistrationDetail");
    }

    [Fact]
    public void OutboundRegistrationDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(OutboundRegistrationDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("OutboundRegistrationDetail");
    }

    [Fact]
    public void InboundSubscriptionDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(InboundSubscriptionDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("InboundSubscriptionDetail");
    }

    [Fact]
    public void OutboundSubscriptionDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(OutboundSubscriptionDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("OutboundSubscriptionDetail");
    }

    [Fact]
    public void AorListEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(AorListEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("AorList");
    }

    [Fact]
    public void AorListCompleteEvent_ShouldHaveListItems()
    {
        var evt = new AorListCompleteEvent { ListItems = 5 };
        evt.ListItems.Should().Be(5);
    }

    [Fact]
    public void AuthListEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(AuthListEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("AuthList");
    }

    [Fact]
    public void AuthListCompleteEvent_ShouldHaveListItems()
    {
        var evt = new AuthListCompleteEvent { ListItems = 3 };
        evt.ListItems.Should().Be(3);
    }

    [Fact]
    public void ResourceListDetailEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(ResourceListDetailEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("ResourceListDetail");
    }

    // --- FAX events ---

    [Fact]
    public void FAXSessionEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(FAXSessionEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("FAXSession");
    }

    [Fact]
    public void FAXSessionEvent_ShouldHaveProperties()
    {
        var evt = new FAXSessionEvent
        {
            Channel = "PJSIP/2000-0001",
            SessionNumber = "1",
            Operation = "send",
            State = "active"
        };
        evt.Channel.Should().Be("PJSIP/2000-0001");
        evt.Operation.Should().Be("send");
    }

    [Fact]
    public void FAXSessionsEntryEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(FAXSessionsEntryEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("FAXSessionsEntry");
    }

    [Fact]
    public void FAXSessionsCompleteEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(FAXSessionsCompleteEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("FAXSessionsComplete");
    }

    [Fact]
    public void FAXStatsEvent_ShouldHaveProperties()
    {
        var evt = new FAXStatsEvent { CompletedFAXes = "10", FailedFAXes = "2" };
        evt.CompletedFAXes.Should().Be("10");
        evt.FailedFAXes.Should().Be("2");
    }

    // --- Bridge info events ---

    [Fact]
    public void BridgeInfoChannelEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(BridgeInfoChannelEvent).Should().BeAssignableTo<ChannelEventBase>();
        var evt = new BridgeInfoChannelEvent { BridgeUniqueid = "abc-123" };
        evt.BridgeUniqueid.Should().Be("abc-123");
    }

    [Fact]
    public void BridgeInfoCompleteEvent_ShouldHaveListItems()
    {
        var evt = new BridgeInfoCompleteEvent { ListItems = 2 };
        evt.ListItems.Should().Be(2);
    }

    // --- MCID ---

    [Fact]
    public void MCIDEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(MCIDEvent).Should().BeAssignableTo<ChannelEventBase>();
        var evt = new MCIDEvent { MCallerIDNum = "2000" };
        evt.MCallerIDNum.Should().Be("2000");
    }

    // --- MWI / Voicemail ---

    [Fact]
    public void MWIGetEvent_ShouldHaveProperties()
    {
        var evt = new MWIGetEvent { Mailbox = "2000@default", OldMessages = 3, NewMessages = 5 };
        evt.Mailbox.Should().Be("2000@default");
        evt.NewMessages.Should().Be(5);
    }

    [Fact]
    public void MWIGetCompleteEvent_ShouldHaveListItems()
    {
        var evt = new MWIGetCompleteEvent { ListItems = 1 };
        evt.ListItems.Should().Be(1);
    }

    [Fact]
    public void MiniVoiceMailEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(MiniVoiceMailEvent).Should().BeAssignableTo<ChannelEventBase>();
        var evt = new MiniVoiceMailEvent { Mailbox = "2000@default", Counter = "3" };
        evt.Mailbox.Should().Be("2000@default");
    }

    [Fact]
    public void VoicemailPasswordChangeEvent_ShouldHaveProperties()
    {
        var evt = new VoicemailPasswordChangeEvent
        {
            Context = "default",
            Mailbox = "2000",
            NewPassword = "1234"
        };
        evt.Context.Should().Be("default");
        evt.NewPassword.Should().Be("1234");
    }

    // --- System events ---

    [Fact]
    public void LoadEvent_ShouldHaveProperties()
    {
        var evt = new LoadEvent { Module = "res_pjsip.so", Status = "Running" };
        evt.Module.Should().Be("res_pjsip.so");
        evt.Status.Should().Be("Running");
    }

    [Fact]
    public void UnloadEvent_ShouldHaveProperties()
    {
        var evt = new UnloadEvent { Module = "res_pjsip.so", Status = "Unloaded" };
        evt.Module.Should().Be("res_pjsip.so");
    }

    // --- DAHDI / Signal events ---

    [Fact]
    public void FlashEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(FlashEvent).Should().BeAssignableTo<ChannelEventBase>();
    }

    [Fact]
    public void WinkEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(WinkEvent).Should().BeAssignableTo<ChannelEventBase>();
    }

    [Fact]
    public void SpanAlarmEvent_ShouldHaveProperties()
    {
        var evt = new SpanAlarmEvent { Span = 1, Alarm = "Red Alarm" };
        evt.Span.Should().Be(1);
        evt.Alarm.Should().Be("Red Alarm");
    }

    [Fact]
    public void SpanAlarmClearEvent_ShouldHaveSpan()
    {
        var evt = new SpanAlarmClearEvent { Span = 2 };
        evt.Span.Should().Be(2);
    }

    // --- Asterisk 20+ events ---

    [Fact]
    public void DeadlockStartEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(DeadlockStartEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("DeadlockStart");
    }

    [Fact]
    public void CoreShowChannelMapCompleteEvent_ShouldHaveListItems()
    {
        var evt = new CoreShowChannelMapCompleteEvent { ListItems = 10 };
        evt.ListItems.Should().Be(10);
    }

    // --- AOC events ---

    [Fact]
    public void AocDEvent_ShouldHaveHyphenatedMapping()
    {
        var attr = typeof(AocDEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("AOC-D");
    }

    [Fact]
    public void AocDEvent_ShouldHaveProperties()
    {
        var evt = new AocDEvent
        {
            Charge = "Currency",
            Currency = "USD",
            CurrencyAmount = "150"
        };
        evt.Charge.Should().Be("Currency");
        evt.CurrencyAmount.Should().Be("150");
    }

    [Fact]
    public void AocEEvent_ShouldHaveHyphenatedMapping()
    {
        var attr = typeof(AocEEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("AOC-E");
    }

    [Fact]
    public void AocSEvent_ShouldHaveHyphenatedMapping()
    {
        var attr = typeof(AocSEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("AOC-S");
    }
}
