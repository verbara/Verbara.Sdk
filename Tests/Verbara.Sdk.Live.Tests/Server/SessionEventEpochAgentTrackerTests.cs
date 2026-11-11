using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// An agent state change carries the loss mark (<see cref="SessionDomainEvent.Epoch"/>) of the session notification
/// that caused it, end to end over the SDK's <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/>: the agent tracker
/// follows a session engine whose every frame goes over the wire and through the connection's pump.
/// </summary>
public sealed class SessionEventEpochAgentTrackerTests
{
    private const string Caller = "1700000400.1";
    private const string CallerChannel = "PJSIP/pstn-00000041";
    private const string Member = "PJSIP/agent-1001";
    private const string MemberLeg = "1700000400.2";
    private const string MemberChannel = "PJSIP/agent-1001-00000042";
    private const string Queue = BootingAsterisk.QueueName;

    [Fact]
    public async Task StateChanges_ShouldCarryTheCausingNotificationsEpoch_WhenACallIsConnected()
    {
        await using var rig = await EpochRig.StartAsync();
        using var tracker = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, rig.WrapUpClock);
        var onCall = new TaskCompletionSource<AgentSessionStateChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = tracker.StateChanges.Subscribe(new StateChangeObserver(change =>
        {
            if (change.NewState == AgentSessionState.OnCall)
                onCall.TrySetResult(change);
        }));

        await rig.SendAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await rig.First.WaitAsync<CallStartedEvent>();
        await rig.SendAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await rig.First.WaitAsync<CallQueuedEvent>();
        await rig.SendAsync("AgentConnect", AgentConnect());
        var connected = await rig.First.WaitAsync<CallConnectedEvent>();
        var change = await onCall.Task.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            connected.AgentId.Should().NotBeNull("premise: the member is an agent the server knows, so the connect names it");
            connected.Epoch.Should().NotBeNull("premise: the call connected over the SDK's connection");
            change.AgentId.Should().Be(connected.AgentId, "premise: the change is the connected agent's");
            change.SessionId.Should().Be(connected.SessionId, "premise: the change is caused by the call's connect");
            change.Epoch.Should().Be(connected.Epoch,
                "the agent went on call because of the call-connected notification, so its change carries that mark");
        }
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
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
