using System.IO.Pipelines;
using System.Text;
using Asterisk.NetAot.Ami.Internal;
using FluentAssertions;

namespace Asterisk.NetAot.Ami.Tests.Internal;

/// <summary>
/// Integration tests simulating full AMI flows:
/// Action -> Response, Action -> Response + Events, Protocol identifier, etc.
/// Uses paired pipes to simulate client and server sides.
/// </summary>
public class AmiFlowIntegrationTests
{
    [Fact]
    public async Task FullFlow_ProtocolIdentifier_Then_LoginResponse()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        // Simulate Asterisk sending protocol identifier + login response
        var data = "Asterisk Call Manager/6.0.0\r\n"
            + "Response: Success\r\n"
            + "ActionID: login_1\r\n"
            + "Message: Authentication accepted\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        // Read protocol identifier
        var ident = await reader.ReadMessageAsync();
        ident.Should().NotBeNull();
        ident!.IsProtocolIdentifier.Should().BeTrue();
        ident.ProtocolIdentifier.Should().Contain("6.0.0");

        // Read login response
        var login = await reader.ReadMessageAsync();
        login.Should().NotBeNull();
        login!.IsResponse.Should().BeTrue();
        login.ResponseStatus.Should().Be("Success");
        login["Message"].Should().Be("Authentication accepted");
    }

    [Fact]
    public async Task FullFlow_ChallengeLogin_Sequence()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Asterisk Call Manager/6.0.0\r\n"
            + "Response: Success\r\nActionID: ch_1\r\nChallenge: abc123def\r\n\r\n"
            + "Response: Success\r\nActionID: login_1\r\nMessage: Authentication accepted\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        var ident = await reader.ReadMessageAsync();
        ident!.IsProtocolIdentifier.Should().BeTrue();

        var challenge = await reader.ReadMessageAsync();
        challenge!.IsResponse.Should().BeTrue();
        challenge["Challenge"].Should().Be("abc123def");

        var login = await reader.ReadMessageAsync();
        login!.ResponseStatus.Should().Be("Success");
    }

    [Fact]
    public async Task FullFlow_SendAction_ReceiveResponse_ThenEvents()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        // Simulate: QueueStatus response followed by events
        var data = "Response: Success\r\nActionID: qs_1\r\nMessage: Queue status will follow\r\n\r\n"
            + "Event: QueueParams\r\nActionID: qs_1\r\nQueue: sales\r\nMax: 0\r\nStrategy: ringall\r\n\r\n"
            + "Event: QueueMember\r\nActionID: qs_1\r\nQueue: sales\r\nName: Agent/1001\r\nStatus: 1\r\n\r\n"
            + "Event: QueueStatusComplete\r\nActionID: qs_1\r\nEventList: Complete\r\nListItems: 2\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        // Response
        var resp = await reader.ReadMessageAsync();
        resp!.IsResponse.Should().BeTrue();
        resp.ActionId.Should().Be("qs_1");

        // Event 1: QueueParams
        var evt1 = await reader.ReadMessageAsync();
        evt1!.IsEvent.Should().BeTrue();
        evt1.EventType.Should().Be("QueueParams");
        evt1["Queue"].Should().Be("sales");
        evt1["Strategy"].Should().Be("ringall");

        // Event 2: QueueMember
        var evt2 = await reader.ReadMessageAsync();
        evt2!.IsEvent.Should().BeTrue();
        evt2.EventType.Should().Be("QueueMember");
        evt2["Name"].Should().Be("Agent/1001");

        // Event 3: QueueStatusComplete
        var evt3 = await reader.ReadMessageAsync();
        evt3!.IsEvent.Should().BeTrue();
        evt3.EventType.Should().Be("QueueStatusComplete");
        evt3["ListItems"].Should().Be("2");
    }

    [Fact]
    public async Task FullFlow_CommandAction_ResponseFollows()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Response: Follows\r\n"
            + "ActionID: cmd_1\r\n"
            + "Privilege: Command\r\n"
            + "Asterisk 20.3.0 on Linux x86_64\r\n"
            + "Built by root @ build-host on 2024-01-15\r\n"
            + "--END COMMAND--\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        var msg = await reader.ReadMessageAsync();
        msg!.IsResponse.Should().BeTrue();
        msg.ResponseStatus.Should().Be("Follows");
        msg.ActionId.Should().Be("cmd_1");
        msg.CommandOutput.Should().Contain("Asterisk 20.3.0");
        msg.CommandOutput.Should().Contain("Built by root");
    }

    [Fact]
    public async Task FullFlow_WriterThenReader_RoundTrip()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var reader = new AmiProtocolReader(pipe.Reader);

        // Write an action
        await writer.WriteActionAsync("Originate", "orig_42",
        [
            new("Channel", "PJSIP/2000"),
            new("Context", "from-internal"),
            new("Exten", "1234"),
            new("Priority", "1"),
            new("Async", "true")
        ]);
        await pipe.Writer.CompleteAsync();

        // Read it back
        var msg = await reader.ReadMessageAsync();
        msg.Should().NotBeNull();
        msg!["Action"].Should().Be("Originate");
        msg["ActionID"].Should().Be("orig_42");
        msg["Channel"].Should().Be("PJSIP/2000");
        msg["Context"].Should().Be("from-internal");
        msg["Exten"].Should().Be("1234");
        msg["Priority"].Should().Be("1");
        msg["Async"].Should().Be("true");
    }

    [Fact]
    public async Task LegacyEventAdapter_ShouldCreateDialEventFromDialBegin()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "DialBegin",
            ["Uniqueid"] = "123.1",
            ["Privilege"] = "call,all"
        };
        var msg = new AmiMessage(fields);

        var legacy = LegacyEventAdapter.CreateLegacyEvent(msg);

        legacy.Should().NotBeNull();
        legacy!.EventType.Should().Be("Dial");
        legacy.UniqueId.Should().Be("123.1");
    }

    [Fact]
    public async Task LegacyEventAdapter_ShouldReturnNull_ForNonLegacyEvent()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "Hangup"
        };
        var msg = new AmiMessage(fields);

        var legacy = LegacyEventAdapter.CreateLegacyEvent(msg);
        legacy.Should().BeNull();
    }

    [Fact]
    public async Task ActiveBridgeTracker_ShouldTrackBridgeLifecycle()
    {
        var tracker = new ActiveBridgeTracker();

        tracker.OnBridgeCreated("br-001", "basic", "simple_bridge");
        tracker.ActiveBridges.Should().HaveCount(1);

        tracker.OnChannelEntered("br-001", "PJSIP/2000-001");
        tracker.OnChannelEntered("br-001", "PJSIP/3000-002");
        tracker.GetBridge("br-001")!.Channels.Should().HaveCount(2);

        tracker.OnChannelLeft("br-001", "PJSIP/2000-001");
        tracker.GetBridge("br-001")!.Channels.Should().HaveCount(1);

        tracker.OnBridgeDestroyed("br-001");
        tracker.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public async Task MeetmeCompatibility_ShouldMapConfbridgeToMeetMe()
    {
        MeetmeCompatibility.GetMeetMeEquivalent("ConfbridgeJoin").Should().Be("MeetMeJoin");
        MeetmeCompatibility.GetMeetMeEquivalent("ConfbridgeLeave").Should().Be("MeetMeLeave");
        MeetmeCompatibility.GetMeetMeEquivalent("ConfbridgeEnd").Should().Be("MeetMeEnd");
        MeetmeCompatibility.GetMeetMeEquivalent("ConfbridgeTalking").Should().Be("MeetMeTalking");
        MeetmeCompatibility.GetMeetMeEquivalent("SomeOtherEvent").Should().BeNull();
    }
}
