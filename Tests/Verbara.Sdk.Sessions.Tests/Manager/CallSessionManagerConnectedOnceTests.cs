using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A <see cref="CallConnectedEvent"/> is published when a transition into <see cref="CallSessionState.Connected"/>
/// succeeds, and a report that finds the call already connected publishes nothing, whichever handler receives it.
/// </summary>
/// <remarks>Runs on the real manager over <see cref="WrapUpRig"/>'s frames, on <see cref="FakeTimeProvider"/>.</remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerConnectedOnceTests
{
    [Fact]
    public async Task AgentConnected_ShouldPublishNothing_WhenACallOutsideAnyQueueIsAlreadyConnectedByItsCalleesAnswer()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock);
        rig.Deliver(AgentLogin("agent-4", "PJSIP/agent4", "login-4"));
        rig.Deliver(
            NewChannel("n1", CallerChannel("n1"), "Ring", "n1", context: "from-trunk", exten: "600"),
            NewChannel("nm1", MemberChannel("PJSIP/agent4", "nm1"), "Down", "n1", context: "agents"),
            new DialBeginEvent
            {
                EventType = "DialBegin", UniqueId = "n1", Channel = CallerChannel("n1"), DestUniqueid = "nm1",
                DestChannel = MemberChannel("PJSIP/agent4", "nm1"), DialString = "PJSIP/agent4",
            },
            NewState("nm1", "Up"));
        var session = rig.Session("n1");
        var afterAnswer = (session.State, Count: Announcements(rig, session.SessionId));
        clock.Advance(TimeSpan.FromSeconds(1));

        rig.Deliver(AgentConnect("", "n1", CallerChannel("n1"), "n1", "PJSIP/agent4", "nm1",
            MemberChannel("PJSIP/agent4", "nm1"), "agent-4"));

        afterAnswer.Should().Be((CallSessionState.Connected, 1),
            "premise: the callee's answer connects the call and is announced");
        Announcements(rig, session.SessionId).Should().Be(1,
            "the agent's connect finds the call already connected, so it is no new connection and is not announced");
    }

    private static int Announcements(WrapUpRig rig, string sessionId) =>
        rig.Events.OfType<CallConnectedEvent>().Count(e => e.SessionId == sessionId);
}
