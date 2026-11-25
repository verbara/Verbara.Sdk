using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Whose hang-up cause decides the ending of a connected call that is over while it waits in a queue: the channel that
/// waits there, which is not always the call's first channel. An agent's phone dials a customer, the customer answers,
/// the agent blind-transfers the customer into a queue and hangs up, and the customer hangs up while waiting.
/// </summary>
/// <remarks>
/// Runs on the real manager over <see cref="WrapUpRig"/>'s frames, on <see cref="FakeTimeProvider"/>. The frames are
/// synthetic, in the order Asterisk sends them for a blind transfer (<see cref="WrapUpFrames"/>): no capture holds a
/// call that an agent originated and transferred into a queue. In every captured queue shape the call's first channel
/// is the one that waits, so the two readings agree there.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerQueuedCallerCauseTests
{
    private const string AgentLeg = "a1";
    private const string CustomerLeg = "b1";
    private const string AgentChannel = "PJSIP/agent1-00000001";
    private const string CustomerChannel = "PJSIP/trunk-00000002";

    [Theory]
    [InlineData(HangupCause.NormalClearing, HangupCause.UserBusy, CallSessionState.Failed)]
    [InlineData(HangupCause.UserBusy, HangupCause.NormalClearing, CallSessionState.Completed)]
    public async Task CallSession_ShouldEndOnTheWaitingCustomersCause_WhenAnAgentsCallTransferredIntoAQueueEndsWhileWaiting(
        HangupCause agentCause, HangupCause customerCause, CallSessionState expected)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(
            NewChannel(AgentLeg, AgentChannel, "Ring", AgentLeg, context: "from-internal", exten: "5550100"),
            NewChannel(CustomerLeg, CustomerChannel, "Down", AgentLeg, context: "from-internal"),
            new DialBeginEvent
            {
                EventType = "DialBegin", UniqueId = AgentLeg, Channel = AgentChannel, DestUniqueid = CustomerLeg,
                DestChannel = CustomerChannel, DialString = "trunk/5550100",
            },
            NewState(CustomerLeg, "Up"),
            NewState(AgentLeg, "Up"),
            BridgeCreate(Bridge(AgentLeg)),
            BridgeEnter(Bridge(AgentLeg), CustomerLeg),
            BridgeEnter(Bridge(AgentLeg), AgentLeg),
            BlindTransfer(Bridge(AgentLeg), CustomerLeg, CustomerChannel, "queues", "support"),
            BridgeLeave(Bridge(AgentLeg), AgentLeg),
            BridgeLeave(Bridge(AgentLeg), CustomerLeg),
            HangupWith(AgentLeg, agentCause),
            Join("support", CustomerChannel, CustomerLeg, AgentLeg));
        var session = rig.Session(AgentLeg);
        var premise = (session.State, session.Participants[0].UniqueId);

        rig.Deliver(HangupWith(CustomerLeg, customerCause));

        using var scope = new AssertionScope();
        premise.Should().Be((CallSessionState.Queued, AgentLeg),
            "premise: the call's first channel is the agent's, and the customer waits in the queue");
        rig.Events.OfType<CallConnectedEvent>().Where(e => e.SessionId == session.SessionId).Should()
            .ContainSingle("premise: the customer's answer connected the call once");
        session.State.Should().Be(expected, "the cause of the channel that waits in the queue decides the ending");
        session.HangupCause.Should().Be(customerCause, "the call's cause is the waiting customer's own, not the agent's");
        rig.Events.OfType<CallEndedEvent>().Where(e => e.SessionId == session.SessionId).Should()
            .ContainSingle("the call ends once").Which.Cause.Should().Be(customerCause,
                "the ending carries the waiting customer's own cause");
    }

    private static HangupEvent HangupWith(string uniqueId, HangupCause cause) =>
        new() { EventType = "Hangup", UniqueId = uniqueId, Cause = (int)cause };
}
