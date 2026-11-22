using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The Asterisk instance identity the session engine seals on every notification, over the SDK's
/// <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/>: read at the AMI login from the <c>CoreSettings</c> response's
/// <c>SystemName</c>, set before the first subscriber sees the notification, the same for one instance attached under two
/// server ids and different for two instances that share a <c>linkedid</c>, null when the instance gives none (an empty
/// <c>SystemName</c>, or a user refused <c>CoreSettings</c>), and never a change of <see cref="SessionDomainEvent.ServerId"/>.
/// </summary>
/// <remarks>
/// Every frame goes over the wire and through the connection's pump (<see cref="Run"/>, <see cref="BootingAsterisk"/>);
/// every wait is bounded by <see cref="Run.Bound"/> and ends on its signal. The identity is read only through
/// <see cref="Verbara.Sdk.Tests.Shared.Sessions.InstanceIdentityNames"/>.
/// </remarks>
public sealed class SessionInstanceIdentityStampTests
{
    private const string Caller = "1700000500.1";
    private const string CallerChannel = "PJSIP/pstn-00000501";
    private const string Member = "PJSIP/agent-2001";
    private const string MemberLeg = "1700000500.2";
    private const string MemberChannel = "PJSIP/agent-2001-00000502";

    [Fact]
    public async Task Events_ShouldCarryTheSameIdentityForEverySubscriber_WhenTheInstanceReportsASystemName()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var first = IdentityRecorder.On(manager);
        var second = IdentityRecorder.On(manager);
        manager.AttachToServer(run.Server, "srv-a");

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await first.WaitAsync<CallStartedEvent>();
        await peer.SendEventAsync("QueueCallerJoin",
            AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, BootingAsterisk.QueueName, 1, 1));
        await first.WaitAsync<CallQueuedEvent>();
        await peer.SendEventAsync("AgentConnect", Frames.AgentConnect());
        await first.WaitAsync<CallConnectedEvent>();
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        await first.WaitAsync<CallEndedEvent>();
        await second.WaitAsync<CallEndedEvent>();

        var seen = first.Seen;
        using (new AssertionScope())
        {
            seen.Should().HaveCountGreaterThanOrEqualTo(4, "premise: the call started, was queued, connected and ended");
            seen.Should().OnlyContain(r => r.Epoch != null, "premise: every notification was raised over the SDK's connection");
            seen.Should().OnlyContain(r => r.SystemName == "pbx-one",
                "every notification carries the identity its instance reported at the login, already when the first "
                + "subscriber receives it");
            second.Seen.Select(r => (r.Notification, r.Identity)).Should().Equal(seen.Select(r => (r.Notification, r.Identity)),
                "the identity is sealed before the first subscriber sees the notification, so every subscriber reads the same");
        }
    }

    [Fact]
    public async Task CallStarted_ShouldCarryDifferentIdentities_WhenTwoInstancesWithDistinctNamesShareALinkedId()
    {
        var peerA = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        var peerB = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-two" };
        await using var runA = await Run.StartAsync(peerA);
        await using var runB = await Run.StartAsync(peerB);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var first = IdentityRecorder.On(manager);
        var second = IdentityRecorder.On(manager);
        manager.AttachToServer(runA.Server, "srv-a");
        manager.AttachToServer(runB.Server, "srv-b");

        // The same uniqueid and linkedid on both instances: what two Asterisk give when their systemname differs only in
        // the name, or when neither prefixes its ids with it.
        await peerA.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await peerB.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var onA = await first.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a");
        var onB = await first.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-b");
        var onASecond = await second.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a");
        var onBSecond = await second.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-b");

        using (new AssertionScope())
        {
            onA.SystemName.Should().Be("pbx-one", "server A's Asterisk reports pbx-one");
            onB.SystemName.Should().Be("pbx-two", "server B's Asterisk reports pbx-two");
            onA.Identity.Should().NotBe(onB.Identity, "two instances with distinct names are told apart under one linkedid");
            onASecond.Identity.Should().Be(onA.Identity, "each notification's identity is the same for every subscriber");
            onBSecond.Identity.Should().Be(onB.Identity, "each notification's identity is the same for every subscriber");
        }
    }

    [Fact]
    public async Task CallStarted_ShouldCarryOneIdentity_WhenOneInstanceIsAttachedUnderTwoServerIds()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
        manager.AttachToServer(run.Server, "srv-a");
        manager.AttachToServer(run.Server, "srv-a2");

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var underA = await recorder.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a");
        var underA2 = await recorder.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a2");

        using (new AssertionScope())
        {
            underA.Notification.ServerId.Should().NotBe(underA2.Notification.ServerId,
                "premise: the two notifications carry the two server ids the host gave");
            underA.SystemName.Should().Be("pbx-one", "the instance reports pbx-one");
            underA2.SystemName.Should().Be("pbx-one", "the identity is the instance's, not the server id's");
            underA.Identity.Should().Be(underA2.Identity, "one instance under two server ids carries one identity");
        }
    }

    [Fact]
    public async Task CallStarted_ShouldKeepTheHostsServerId_WhenItCarriesTheInstanceIdentity()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one" };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
        manager.AttachToServer(run.Server, "pbx-a");

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        using (new AssertionScope())
        {
            started.Notification.ServerId.Should().Be("pbx-a", "ServerId remains the key the host gave the server");
            started.SystemName.Should().Be("pbx-one", "beside it, the identity is the one the instance reported");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Events_ShouldCarryANullIdentity_WhenTheInstanceGivesNone(bool refusesCoreSettings)
    {
        // An Asterisk without systemname answers CoreSettings with an empty SystemName; a user whose read classes allow
        // neither system nor reporting is refused CoreSettings. Either way the login read gives no identity.
        var peer = refusesCoreSettings
            ? new BootingAsterisk { BootedAtLogin = true, SystemName = "pbx-one", RefusesCoreSettings = true }
            : new BootingAsterisk { BootedAtLogin = true, SystemName = "" };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = IdentityRecorder.On(manager);
        manager.AttachToServer(run.Server, "srv-a");

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            peer.Asked("CoreSettings").Should().Be(1,
                "the identity is read from the login's CoreSettings, the session's only one: no further round trip");
            started.Epoch.Should().NotBeNull("premise: the call started over the SDK's connection");
            started.Identity.Should().BeNull("the instance gave no identity at the login: the engine cannot tell");
            ended.Identity.Should().BeNull("nor at the end of the call: null means unknown for the whole session");
        }
    }

    private static class Frames
    {
        public static KeyValuePair<string, string>[] AgentConnect() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", BootingAsterisk.QueueName), new("Interface", Member), new("MemberName", Member),
            new("HoldTime", "0"), new("RingTime", "1"),
        ];
    }
}
