using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A removal of a released attachment that runs after the release: it ends no call, because once the release let go of
/// the id's latest attachment there is no oracle left to decide one against. The removal is held inside its window by
/// parking it on the manager's step seam (<see cref="StepParker"/>), never by a sleep.
/// </summary>
public sealed class CallSessionManagerReleasedLateRemovalTests
{
    /// <summary>
    /// A hangup parked after its channel lookup, a keep, then a release that does not drain the kept attachment; a
    /// legless call registered afterwards under the same id and linkedid is the one the late removal finds by linkedid.
    /// </summary>
    [Fact]
    public async Task OnChannelRemoved_ShouldEndNoCall_WhenItsAttachmentWasReleasedBeforeTheRemovalResumed()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.Arrival("a", "lr");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", ReleaseRig.CallerLeg("lr"));
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", ReleaseRig.CallerLeg("lr")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        var released = rig.Release("a");
        var late = new CallSession("late-reconstruction", ReleaseRig.LinkedIdOf("lr"), "a", CallDirection.Inbound);
        var registered = rig.Manager.RegisterReconstructedSession(late);
        parker.LetGo();
        parker.WaitUntilResumed(ReleaseRig.Bound);
        var hangupDone = ReleaseRig.Returns(hangup);
        parker.ThrowIfFaulted();

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            registered,
            hangupDone,
            LateHeld = rig.Manager.GetById(late.SessionId) is not null,
            LateState = late.State,
            LateEndings = rig.KindsOf(late.SessionId).Count(k => k == nameof(CallEndedEvent)),
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { call.SessionId },
                registered = true,
                hangupDone = true,
                LateHeld = true,
                LateState = CallSessionState.Created,
                LateEndings = 0,
            },
            "a removal of the released attachment that resumes after the release must decide nothing: the call it "
            + "finds by linkedid stays held, unended and unnotified. " + rig.Describe());

        rig.Release("a").Select(c => c.SessionId).Should().Equal([late.SessionId],
            "a later release lets the call the late removal left held go");
    }
}
