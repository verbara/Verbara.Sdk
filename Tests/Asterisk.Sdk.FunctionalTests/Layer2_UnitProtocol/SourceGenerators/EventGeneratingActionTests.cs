namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class EventGeneratingActionTests
{
    [Fact]
    public void EventGeneratingAction_ShouldBeMarkedCorrectly()
    {
        // OriginateAction and QueueStatusAction implement IEventGeneratingAction
        var originate = new OriginateAction();
        var queueStatus = new QueueStatusAction();

        originate.Should().BeAssignableTo<IEventGeneratingAction>();
        queueStatus.Should().BeAssignableTo<IEventGeneratingAction>();

        // PingAction should NOT be an event-generating action
        var ping = new PingAction();
        ping.Should().NotBeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void ResponseEvent_ShouldDeserializeActionId()
    {
        // QueueMemberEvent inherits from ResponseEvent which has ActionId
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "QueueMember",
            ["ActionID"] = "queue-status-001",
            ["Queue"] = "support",
            ["MemberName"] = "SIP/agent-1001",
            ["Penalty"] = "3",
            ["Paused"] = "0",
        };
        var msg = new AmiMessage(fields);

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<QueueMemberEvent>();
        var typed = (QueueMemberEvent)evt;

        // ActionId is a property on ResponseEvent (intermediate base)
        typed.ActionId.Should().Be("queue-status-001");

        // Verify other properties are also deserialized
        typed.Queue.Should().Be("support");
        typed.MemberName.Should().Be("SIP/agent-1001");
        typed.Penalty.Should().Be(3);
        typed.Paused.Should().BeFalse();
    }

    [Fact]
    public void EventRegistry_ShouldContainAllMappedEvents()
    {
        // Verify that GeneratedEventRegistry can create known event types
        GeneratedEventRegistry.Create("NewChannel").Should().BeOfType<NewChannelEvent>();
        GeneratedEventRegistry.Create("Hangup").Should().BeOfType<HangupEvent>();
        GeneratedEventRegistry.Create("Cdr").Should().BeOfType<CdrEvent>();
        GeneratedEventRegistry.Create("QueueMember").Should().BeOfType<QueueMemberEvent>();
        GeneratedEventRegistry.Create("ConfbridgeTalking").Should().BeOfType<ConfbridgeTalkingEvent>();

        // Case-insensitive lookup (FrozenDictionary uses OrdinalIgnoreCase)
        GeneratedEventRegistry.Create("newchannel").Should().BeOfType<NewChannelEvent>();
        GeneratedEventRegistry.Create("HANGUP").Should().BeOfType<HangupEvent>();

        // Unknown events return null
        GeneratedEventRegistry.Create("CompletelyFakeEvent").Should().BeNull();
        GeneratedEventRegistry.Create("").Should().BeNull();

        // Registry should have a significant number of events (215+)
        GeneratedEventRegistry.Count.Should().BeGreaterThanOrEqualTo(200);
    }
}
