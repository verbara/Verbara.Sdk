using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// An agent state change carries the instance identity of the session notification that caused it, beside its loss
/// mark, end to end over the SDK's <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/>: a known identity when the
/// cause names one, null when the cause's instance gave none.
/// </summary>
public sealed class SessionInstanceIdentityAgentTrackerTests
{
    private const string Caller = "1700000800.1";
    private const string CallerChannel = "PJSIP/pstn-00000801";
    private const string Member = "PJSIP/agent-1001";
    private const string MemberLeg = "1700000800.2";
    private const string MemberChannel = "PJSIP/agent-1001-00000802";
    private const string Queue = BootingAsterisk.QueueName;

    [Fact]
    public async Task OnCall_ShouldCarryTheConnectsIdentity_WhenTheCauseNamesAnInstance()
    {
        var (connected, change) = await ConnectAnAgentAsync(systemName: "pbx-one");

        using (new AssertionScope())
        {
            connected.SystemName.Should().Be("pbx-one", "premise: the call-connected notification names pbx-one");
            change.Epoch.Should().Be(connected.Epoch, "premise: the change copies its cause's loss mark");
            change.SystemName.Should().Be("pbx-one",
                "the agent went on call because of the call-connected notification, so its change carries that identity");
            change.Identity.Should().Be(connected.Identity, "the change's identity is its cause's, by value");
        }
    }

    [Fact]
    public async Task OnCall_ShouldCarryANullIdentity_WhenTheCausesInstanceGaveNone()
    {
        var (connected, change) = await ConnectAnAgentAsync(systemName: "");

        using (new AssertionScope())
        {
            connected.Epoch.Should().NotBeNull("premise: the call connected over the SDK's connection");
            connected.Identity.Should().BeNull("premise: the instance gave no identity, so the cause names none");
            change.Epoch.Should().Be(connected.Epoch, "premise: the change copies its cause's loss mark");
            change.Identity.Should().BeNull("an unknown cause gives an unknown agent identity");
        }
    }

    /// <summary>
    /// A queue call taken by the server's known agent, over a peer whose <c>CoreSettings</c> reports
    /// <paramref name="systemName"/>; returns the call-connected notification and the agent's change to on-call, each
    /// read at receipt.
    /// </summary>
    private static async Task<(IdentityReading Connected, IdentityReading Change)> ConnectAnAgentAsync(string systemName)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SystemName = systemName };
        await using var run = await Run.StartAsync(peer);
        var clock = new FakeTimeProvider();
        await using var manager = EpochRig.NewManager(clock);
        var recorder = IdentityRecorder.On(manager);
        using var tracker = new AgentSessionTracker(manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        var onCall = new TaskCompletionSource<IdentityReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = tracker.StateChanges.Subscribe(new StateChangeObserver(change =>
        {
            if (change.NewState == AgentSessionState.OnCall)
                onCall.TrySetResult(new IdentityReading(change, change.Epoch, InstanceIdentityNames.IdentityOf(change),
                    InstanceIdentityNames.SystemNameOf(change)));
        }));
        manager.AttachToServer(run.Server, "srv-identity");

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await recorder.WaitAsync<CallStartedEvent>();
        await peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await recorder.WaitAsync<CallQueuedEvent>();
        await peer.SendEventAsync("AgentConnect", AgentConnect());
        var connected = await recorder.WaitAsync<CallConnectedEvent>();
        var change = await onCall.Task.WaitAsync(Run.Bound);

        var cause = (CallConnectedEvent)connected.Notification;
        var stateChange = (AgentSessionStateChanged)change.Notification;
        cause.AgentId.Should().NotBeNull("premise: the member is an agent the server knows, so the connect names it");
        stateChange.AgentId.Should().Be(cause.AgentId, "premise: the change is the connected agent's");
        stateChange.SessionId.Should().Be(cause.SessionId, "premise: the change is caused by the call's connect");
        return (connected, change);
    }

    /// <summary>The <c>AgentConnect</c> of the server's known agent answering the caller from the queue.</summary>
    private static KeyValuePair<string, string>[] AgentConnect() =>
    [
        new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
        new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
        new("Queue", Queue), new("Interface", Member), new("MemberName", "Agent " + BootingAsterisk.AgentId),
        new("Agent", BootingAsterisk.AgentId), new("HoldTime", "0"), new("RingTime", "1"),
    ];

    private sealed class StateChangeObserver(Action<AgentSessionStateChanged> next) : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) => next(value);

        public void OnError(Exception error)
        {
            // The tracker never faults its subscribers in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the tracker is disposed; nothing to do.
        }
    }
}
