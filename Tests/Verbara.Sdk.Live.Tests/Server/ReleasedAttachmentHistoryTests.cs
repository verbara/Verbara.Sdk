using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The attachment history of a server id across <c>DetachFromServer(id, DetachedCalls.Release)</c>: the id's generation
/// keeps growing after a release, and a delivery of the released attachment is still refused as stale once the id is
/// attached again, with the same instance or another.
/// </summary>
public sealed class ReleasedAttachmentHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachToServer_ShouldGiveTheNewAttachmentAGreaterGeneration_WhenTheIdReturnsAfterARelease(
        bool sameInstance)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        rig.Attach();
        rig.Add("X");
        var released = rig.Manager.GetByLinkedId("server", "L")!;
        var releasedGeneration = released.AttachmentGeneration;
        releasedGeneration.Should().BeGreaterThan(0, "premise: the call carries its attachment's generation");
        rig.Manager.DetachFromServer("server", DetachedCalls.Release).Should().ContainSingle()
            .Which.Should().BeSameAs(released, "premise: the release lets the call go");

        var returning = sameInstance ? rig.Run.Server : replacement.Server;
        rig.Manager.AttachToServer(returning, "server");
        returning.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "M");
        var next = rig.Manager.GetByLinkedId("server", "M");

        next.Should().NotBeNull("premise: the returning attachment opens a call for its arrival");
        next!.AttachmentGeneration.Should().BeGreaterThan(releasedGeneration,
            "an attach after a release must get a strictly greater generation than the released attachment's");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnChannelRemoved_ShouldIgnoreTheReleasedAttachmentsDelivery_WhenTheIdIsAttachedAgain(
        bool sameInstance)
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        await using var replacement = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        using var gate = new NotificationGate();
        rig.Manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.RemovalFound && id == "X") gate.Hold();
        };
        var workers = new List<Task>();
        var returning = sameInstance ? rig.Run.Server : replacement.Server;
        var late = new CallSession("late-reconstruction", "L", "server", CallDirection.Inbound);
        IReadOnlyList<CallSession> released = [];
        var registered = false;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Hangup("X")));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            rig.Manager.DetachFromServer("server", DetachedCalls.Keep);
            released = rig.Manager.DetachFromServer("server", DetachedCalls.Release);
            rig.Manager.AttachToServer(returning, "server");
            registered = rig.Manager.RegisterReconstructedSession(late);
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
        });
        rig.Manager.StepHook = null;

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            registered,
            WorkersDone = workers.All(w => w.IsCompletedSuccessfully),
            LateHeld = ReferenceEquals(rig.Manager.GetById(late.SessionId), late),
            LateState = late.State,
            LateEndings = rig.Ends(late),
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { call.SessionId },
                registered = true,
                WorkersDone = true,
                LateHeld = true,
                LateState = CallSessionState.Created,
                LateEndings = 0,
            },
            "a delivery of the released attachment is stale once the id is attached again: the call it finds by "
            + "linkedid under the new attachment is neither ended nor notified");
    }
}
