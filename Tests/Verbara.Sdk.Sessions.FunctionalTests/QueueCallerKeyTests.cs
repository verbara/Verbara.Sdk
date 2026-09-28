using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// app_queue's connect finds the queue caller's call by the caller's own channel (<c>Uniqueid</c>), not
/// by the call's <c>Linkedid</c>. The two differ when the queue caller is not the call's first channel.
/// </summary>
/// <remarks>
/// The frames are a call Asterisk 22.9.0 sent live: an AMI <c>Originate</c> of
/// <c>Local/qp@dialer</c>, whose <c>;2</c> half runs <c>Queue(q-pjsip)</c>, answered there by an endpoint
/// member. The <c>;2</c> half is the queue caller, and it carries the <c>;1</c> half's <c>Linkedid</c>,
/// which is the key the call was opened under. So app_queue's <c>AgentConnect</c> names the caller
/// <c>Uniqueid 1790566855.1</c> and the call <c>Linkedid 1790566855.0</c>. Every frame the observer
/// dispatches is kept, in capture order, with the captured values; the bridge id is replaced by a
/// same-length fill, as in the suite's recordings. The originate's own two dial frames name no calling
/// channel, and the server skips them.
/// </remarks>
public sealed class QueueCallerKeyTests
{
    [Fact]
    public async Task QueueVisit_ShouldBeAnnouncedAndCountedAnswered_WhenTheQueueCallerIsNotTheCallsFirstChannel()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(LocalHalfQueuedAndAnswered());

        rig.Connected.Should().ContainSingle(
                "app_queue connected the Local channel's second half, the queue caller, once; its call is keyed by "
                + "the first half's Linkedid, and the connect names the caller by its own Uniqueid")
            .Which.QueueName.Should().Be("q-pjsip");
        rig.Counts("q-pjsip").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0),
            "app_queue connected the visit, and the call has ended");
    }

    private static ManagerEvent[] LocalHalfQueuedAndAnswered()
    {
        const string one = "1790566855.0", oneCh = "Local/qp@dialer-00000000;1";
        const string two = "1790566855.1", twoCh = "Local/qp@dialer-00000000;2";
        const string m = "1790566855.2", mCh = "PJSIP/agent1-00000000";
        const string bridge = "11111111-1111-1111-1111-111111111111";

        return [
            NewChannel(one, oneCh, "0", one, "<unknown>", "dialer", "qp"),
            NewChannel(two, twoCh, "4", one, "<unknown>", "dialer", "qp"),
            new DialBeginEvent { EventType = "DialBegin", DestUniqueid = one, DestChannel = oneCh, DialString = "qp@dialer" },
            Join("q-pjsip", twoCh, two, "5550099", linkedId: one),
            NewChannel(m, mCh, "0", one, "<unknown>", "from-agents", "s"),
            DialBegin(two, twoCh, m, mCh, "PJSIP/agent1"),
            NewState(m, "5"),
            NewState(m, "6"),
            DialEnd(two, twoCh, m, mCh, "ANSWER"),
            Leave("q-pjsip", twoCh, two, linkedId: one),
            AgentConnect("q-pjsip", two, twoCh, one, "PJSIP/agent1", m, mCh, holdTime: 2),
            NewState(two, "6"),
            NewState(one, "6"),
            new DialEndEvent { EventType = "DialEnd", DestUniqueid = one, DestChannel = oneCh, DialStatus = "ANSWER" },
            BridgeCreate(bridge),
            BridgeEnter(bridge, m),
            BridgeEnter(bridge, two),
            Hangup(one, 0),
            AgentComplete(two, twoCh, "PJSIP/agent1", holdTime: 2, talkTime: 9),
            BridgeLeave(bridge, two),
            BridgeLeave(bridge, m),
            BridgeDestroy(bridge),
            Hangup(two, 16),
            Hangup(m, 16),
        ];
    }
}
