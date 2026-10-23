using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// Compatibility controls: the peer writes its old member row, then a live update before QueueStatusComplete, while
/// dispatch is held. Streaming applies the row first; buffered application must retain the live update instead.
/// These wire controls are green on streaming code and do not replace the manager/window mutation regressions.
/// </summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotMemberWireOrderingTests
{
    [Theory]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    public async Task Reload_ShouldKeepTheNewerLiveMemberStateAndIndex_WhenAnOlderRowPrecedesItOnTheWire(
        string mutation, bool expire)
    {
        await using var rig = await QueueAdmissionRun.PrepareAsync();
        rig.Peer.QueueEntries = [];
        var removed = new ConcurrentQueue<string>();
        var statuses = new ConcurrentQueue<(string Interface, QueueMemberState Status)>();
        rig.Run.Server.Queues.MemberRemoved += (_, member) => removed.Enqueue(member.Interface);
        rig.Run.Server.Queues.MemberStatusChanged += (_, member) => statuses.Enqueue((member.Interface, member.Status));
        rig.Peer.BeforeQueueStatusComplete = async () =>
        {
            // This callback is after the old QueueMember row's write and before the completion's write. The AMI
            // reader therefore captures that row before it queues the newer live event in the held pump.
            rig.Peer.BeforeQueueStatusComplete = null;
            switch (mutation)
            {
                case "remove":
                    rig.Peer.QueueMemberIncluded = false;
                    await rig.Peer.SendEventAsync("QueueMemberRemoved", Fields());
                    break;
                case "pause":
                    rig.Peer.QueueMemberPaused = true;
                    await rig.Peer.SendEventAsync("QueueMemberPause", [.. Fields(), new("Paused", "1"), new("PausedReason", "break")]);
                    break;
                case "status":
                    rig.Peer.QueueMemberStatus = 2;
                    await rig.Peer.SendEventAsync("QueueMemberStatus", [.. Fields(), new("Status", "2")]);
                    break;
            }
        };

        var timer = await rig.HoldQueueAsync(liveJoin: false);
        if (expire)
            await rig.ExpireAsync(timer);
        await rig.FinishAsync(liveJoin: false);
        var tail = rig.TailAsync("member-after");
        await rig.SendTailAsync("member-after");
        await tail;
        if (expire)
        {
            await rig.Run.ServerLog.Logged(QueueAdmissionRun.StateLoaded, 3).WaitAsync(Run.Bound);
            await rig.Run.Server.ScheduledReload.WaitAsync(Run.Bound);
        }

        var queue = rig.Run.Server.Queues.GetByName(QueueAdmissionRun.Queue)!;
        var member = queue.Members.GetValueOrDefault(BootingAsterisk.MemberLocation);
        using var scope = new AssertionScope();
        if (mutation == "remove")
        {
            member.Should().BeNull("the older row cannot resurrect the member after its live removal");
            rig.Run.Server.Queues.GetQueuesForMember(BootingAsterisk.MemberLocation).Should().BeEmpty();
            removed.Should().Equal(BootingAsterisk.MemberLocation);
        }
        else
        {
            member.Should().NotBeNull();
            member!.Paused.Should().Be(mutation == "pause");
            member.Status.Should().Be(mutation == "status" ? QueueMemberState.DeviceInUse : QueueMemberState.DeviceNotInUse);
            rig.Run.Server.Queues.GetQueuesForMember(BootingAsterisk.MemberLocation).Should().Equal(QueueAdmissionRun.Queue);
            removed.Should().BeEmpty();
        }
        statuses.Should().Equal(mutation == "status"
            ? [(BootingAsterisk.MemberLocation, QueueMemberState.DeviceInUse)]
            : [], "only the live status change announces state; replay creates no artificial reversal");
        rig.GateTimedOut.Should().BeFalse();
    }

    private static KeyValuePair<string, string>[] Fields() =>
    [
        new("Privilege", "agent,all"), new("Queue", QueueAdmissionRun.Queue),
        new("Interface", BootingAsterisk.MemberLocation), new("Location", BootingAsterisk.MemberLocation),
    ];
}
