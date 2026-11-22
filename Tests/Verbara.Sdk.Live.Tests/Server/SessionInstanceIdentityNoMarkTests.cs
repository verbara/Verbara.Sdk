using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A notification with no loss mark names no instance: over a connection that is not the SDK's (whose Asterisk still
/// reports a name on every <c>CoreSettings</c>, before and after the connection's reconnected notice), and for a wrap-up
/// raised after its server was detached. Such a notification names no AMI session, so there is no session to read an
/// identity for.
/// </summary>
/// <remarks>
/// Every frame goes over the wire and through the real connection's pump; the foreign connection is
/// <see cref="PlainAmiConnectionWrapper"/>, which forwards every member of the interface to it. Every wait is bounded by
/// <see cref="Run.Bound"/> and ends on its signal.
/// </remarks>
public sealed class SessionInstanceIdentityNoMarkTests
{
    private const string ServerId = "srv-identity";
    private const string Caller = "1700000700.1";
    private const string CallerChannel = "PJSIP/pstn-00000701";
    private const string Member = "PJSIP/agent-2001";
    private const string MemberLeg = "1700000700.2";
    private const string MemberChannel = "PJSIP/agent-2001-00000702";
    private const string Queue = BootingAsterisk.QueueName;

    [Fact]
    public async Task Events_ShouldCarryANullIdentity_WhenTheConnectionIsNotAmiConnectionAndReconnects()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        // The reload after the reconnect still lists the call, so it ends on its Hangup.
        var second = new BootingAsterisk
        {
            BootedAtLogin = true,
            SystemName = "pbx-one",
            StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)],
        };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        await using var server = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection),
            NullLogger<VerbaraServer>.Instance) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
        manager.AttachToServer(server, ServerId);

        await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();
        first.CloseSession();
        run.ReleaseSecondPeer();
        await run.Reconnected.WaitAsync(Run.Bound);
        await second.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().BeNull("premise: a connection that is not the SDK's gives the engine no loss mark");
            ended.Epoch.Should().BeNull("premise: nor after its reconnected notice");
            started.Identity.Should().BeNull(
                "a notification with no loss mark names no AMI session, so it names no instance, whatever the Asterisk reports");
            ended.Identity.Should().BeNull(
                "a reconnected notice of a foreign connection is no trigger to read one: nothing ties a notification to a session");
        }
    }

    [Fact]
    public async Task CallWrapUp_ShouldCarryANullIdentity_WhenItsServerWasDetached()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        await using var run = await Run.StartAsync(peer);
        var wrapUpClock = new FakeTimeProvider();
        await using var manager = EpochRig.NewManager(wrapUpClock);
        var recorder = IdentityRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await peer.SendEventAsync("AgentConnect", Frames.AgentConnect());
        await peer.SendEventAsync("AgentComplete", Frames.AgentComplete());
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        manager.DetachFromServer(ServerId);
        wrapUpClock.Advance(new SessionOptions().WrapUpDuration);
        var wrapUp = await recorder.WaitAsync<CallWrapUpEvent>();

        using (new AssertionScope())
        {
            ended.Epoch.Should().NotBeNull("premise: the call ended while its server was attached over the SDK's connection");
            ended.SystemName.Should().Be("pbx-one", "premise: while attached, the call's notifications named pbx-one");
            wrapUp.Epoch.Should().BeNull("premise: no attachment of the detached server raised the wrap-up");
            wrapUp.Identity.Should().BeNull(
                "the wrap-up carries no loss mark, so it names no AMI session and no instance, never the call's last one");
        }
    }

    private static class Frames
    {
        public static KeyValuePair<string, string>[] AgentConnect() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", Queue), new("Interface", Member), new("MemberName", Member), new("HoldTime", "0"),
            new("RingTime", "1"),
        ];

        public static KeyValuePair<string, string>[] AgentComplete() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", Queue), new("Interface", Member), new("MemberName", Member), new("HoldTime", "0"),
            new("TalkTime", "12"), new("Reason", "agent"),
        ];
    }
}
