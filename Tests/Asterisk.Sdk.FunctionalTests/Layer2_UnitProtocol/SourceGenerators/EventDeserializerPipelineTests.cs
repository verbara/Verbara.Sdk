namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class EventDeserializerPipelineTests
{
    /// <summary>
    /// Helper: builds an AmiMessage from a dictionary with an Event key.
    /// </summary>
    private static AmiMessage CreateMessage(string eventName, Dictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventName,
        };

        if (extra is not null)
        {
            foreach (var (k, v) in extra)
                fields[k] = v;
        }

        return new AmiMessage(fields);
    }

    [Fact]
    public void Deserialize_ShouldMapStringProperty()
    {
        var msg = CreateMessage("NewChannel", new Dictionary<string, string>
        {
            ["Channel"] = "SIP/test-0001",
            ["ChannelState"] = "6",
            ["ChannelStateDesc"] = "Up",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<NewChannelEvent>();
        var typed = (NewChannelEvent)evt;
        typed.Channel.Should().Be("SIP/test-0001");
        typed.ChannelStateDesc.Should().Be("Up");
    }

    [Fact]
    public void Deserialize_ShouldMapNullableIntProperty()
    {
        var msg = CreateMessage("Hangup", new Dictionary<string, string>
        {
            ["Channel"] = "SIP/test-0001",
            ["Cause"] = "16",
            ["CauseTxt"] = "Normal Clearing",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<HangupEvent>();
        var typed = (HangupEvent)evt;
        typed.Cause.Should().Be(16);
        typed.CauseTxt.Should().Be("Normal Clearing");
    }

    [Fact]
    public void Deserialize_ShouldMapNullableLongProperty()
    {
        var msg = CreateMessage("AgentComplete", new Dictionary<string, string>
        {
            ["Agent"] = "1001",
            ["Channel"] = "SIP/agent-0001",
            ["HoldTime"] = "42",
            ["TalkTime"] = "9999999999",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<AgentCompleteEvent>();
        var typed = (AgentCompleteEvent)evt;
        typed.HoldTime.Should().Be(42L);
        typed.TalkTime.Should().Be(9_999_999_999L);
    }

    [Fact]
    public void Deserialize_ShouldMapNullableBoolProperty()
    {
        var msg = CreateMessage("ConfbridgeTalking", new Dictionary<string, string>
        {
            ["Conference"] = "100",
            ["TalkingStatus"] = "1",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<ConfbridgeTalkingEvent>();
        var typed = (ConfbridgeTalkingEvent)evt;
        typed.TalkingStatus.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_ShouldMapNullableDoubleProperty()
    {
        var msg = CreateMessage("RtcpReceived", new Dictionary<string, string>
        {
            ["Rtt"] = "0.004500",
            ["LastSr"] = "123.456",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<RtcpReceivedEvent>();
        var typed = (RtcpReceivedEvent)evt;
        typed.Rtt.Should().BeApproximately(0.0045, 0.000001);
        typed.LastSr.Should().BeApproximately(123.456, 0.001);
    }

    [Fact]
    public void Deserialize_ShouldHandleTwoLevelInheritance()
    {
        // HangupEvent -> ChannelEventBase -> ManagerEvent
        var msg = CreateMessage("Hangup", new Dictionary<string, string>
        {
            // ChannelEventBase properties
            ["Channel"] = "SIP/peer-0001",
            ["CallerIdNum"] = "5551234",
            ["CallerIdName"] = "Test User",
            ["Context"] = "from-internal",
            ["Exten"] = "100",
            ["Priority"] = "1",
            // HangupEvent own properties
            ["Cause"] = "16",
            ["CauseTxt"] = "Normal Clearing",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<HangupEvent>();
        var typed = (HangupEvent)evt;

        // ChannelEventBase properties (intermediate base)
        typed.Channel.Should().Be("SIP/peer-0001");
        typed.CallerIdNum.Should().Be("5551234");
        typed.CallerIdName.Should().Be("Test User");
        typed.Context.Should().Be("from-internal");
        typed.Exten.Should().Be("100");
        typed.Priority.Should().Be(1);

        // Leaf properties
        typed.Cause.Should().Be(16);
        typed.CauseTxt.Should().Be("Normal Clearing");
    }

    [Fact]
    public void Deserialize_ShouldHandleIntermediateBaseWithMixedTypes()
    {
        // QueueMemberPausedEvent -> QueueMemberEventBase -> ManagerEvent
        // QueueMemberEventBase has string, int?, and bool? properties
        var msg = CreateMessage("QueueMemberPaused", new Dictionary<string, string>
        {
            // QueueMemberEventBase properties (intermediate base with mixed types)
            ["Queue"] = "support",
            ["MemberName"] = "SIP/agent-1001",
            ["Interface"] = "SIP/agent-1001",
            ["Penalty"] = "5",
            ["CallsTaken"] = "42",
            ["Status"] = "1",
            ["Paused"] = "1",
            ["Ringinuse"] = "false",
            ["LastCall"] = "1616161616",
            // Leaf property
            ["Reason"] = "Break",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<QueueMemberPausedEvent>();
        var typed = (QueueMemberPausedEvent)evt;

        // QueueMemberEventBase properties
        typed.Queue.Should().Be("support");
        typed.MemberName.Should().Be("SIP/agent-1001");
        typed.Penalty.Should().Be(5);
        typed.CallsTaken.Should().Be(42);
        typed.Status.Should().Be(1);
        typed.Paused.Should().BeTrue();
        typed.Ringinuse.Should().BeFalse();
        typed.LastCall.Should().Be(1_616_161_616);

        // Leaf property
        typed.Reason.Should().Be("Break");
    }

    [Fact]
    public void Deserialize_CdrEvent_ShouldMapAll18Fields()
    {
        // CdrEvent has 18 AMI-mapped fields (excluding *AsDate computed properties)
        var msg = CreateMessage("Cdr", new Dictionary<string, string>
        {
            ["AccountCode"] = "acct-001",
            ["Src"] = "5551234",
            ["Destination"] = "5559876",
            ["DestinationContext"] = "from-external",
            ["CallerId"] = "\"John\" <5551234>",
            ["Channel"] = "SIP/trunk-0001",
            ["DestinationChannel"] = "SIP/trunk-0002",
            ["LastApplication"] = "Dial",
            ["LastData"] = "SIP/trunk-0002,30",
            ["StartTime"] = "2025-01-15 10:00:00",
            ["AnswerTime"] = "2025-01-15 10:00:05",
            ["EndTime"] = "2025-01-15 10:05:30",
            ["Duration"] = "330",
            ["BillableSeconds"] = "325",
            ["Disposition"] = "ANSWERED",
            ["AmaFlags"] = "DOCUMENTATION",
            ["UserField"] = "custom-data",
            ["Recordfile"] = "/var/spool/asterisk/monitor/test.wav",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<CdrEvent>();
        var typed = (CdrEvent)evt;

        typed.AccountCode.Should().Be("acct-001");
        typed.Src.Should().Be("5551234");
        typed.Destination.Should().Be("5559876");
        typed.DestinationContext.Should().Be("from-external");
        typed.CallerId.Should().Be("\"John\" <5551234>");
        typed.Channel.Should().Be("SIP/trunk-0001");
        typed.DestinationChannel.Should().Be("SIP/trunk-0002");
        typed.LastApplication.Should().Be("Dial");
        typed.LastData.Should().Be("SIP/trunk-0002,30");
        typed.StartTime.Should().Be("2025-01-15 10:00:00");
        typed.AnswerTime.Should().Be("2025-01-15 10:00:05");
        typed.EndTime.Should().Be("2025-01-15 10:05:30");
        typed.Duration.Should().Be(330);
        typed.BillableSeconds.Should().Be(325);
        typed.Disposition.Should().Be("ANSWERED");
        typed.AmaFlags.Should().Be("DOCUMENTATION");
        typed.UserField.Should().Be("custom-data");
        typed.Recordfile.Should().Be("/var/spool/asterisk/monitor/test.wav");
    }
}
