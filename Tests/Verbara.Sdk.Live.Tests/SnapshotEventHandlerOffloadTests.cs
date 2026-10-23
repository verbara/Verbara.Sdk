using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests;

/// <summary>
/// The events a reload's reconciliation raises, with the subscriber of <see cref="LiveEventHandlerOffloadTests"/>: one
/// that waits, from inside the handler, for another thread calling into the same manager. The wait must end; a raise
/// under a lock that call takes fails in seconds and never hangs the run.
/// </summary>
public sealed class SnapshotEventHandlerOffloadTests
{
    private const string Queue = "q-sales";
    private const string Room = "room-1";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    private readonly QueueManager _queues = new(NullLogger.Instance);
    private readonly MeetMeManager _rooms = new(NullLogger.Instance);
    private readonly BridgeManager _bridges = new(NullLogger.Instance);
    private readonly HandlerOffloadProbe _probe = new();

    [Fact]
    public void CompleteSnapshot_ShouldLetTheCallerLeftHandlerWaitForAnotherThread_WhenThatThreadAddsAMember()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        _queues.CallerLeft += (_, _) =>
            _probe.Offload(() => _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1));

        _queues.CompleteSnapshot(window);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a CallerLeft handler for a caller the snapshot omits, waiting on a member add, must not deadlock");
    }

    [Fact]
    public void NotifySnapshotEntry_ShouldLetTheCallerJoinedHandlerWaitForAnotherThread_WhenThatThreadHandlesAJoin()
    {
        using var window = _queues.OpenSnapshotWindow();
        var reported = _queues.AdmitSnapshotEntry(window, Queue, "PJSIP/caller-1", "u1", "100", 1, 5, Epoch,
            DateTimeOffset.UtcNow, announce: false)!;
        _queues.CallerJoined += (_, entry) =>
        {
            if (entry.Channel == "PJSIP/caller-1")
                _probe.Offload(() => _queues.OnCallerJoined(Queue, "PJSIP/caller-2", "200", 2, "u2", Epoch));
        };

        _queues.NotifySnapshotEntry(window, Queue, reported);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a deferred snapshot CallerJoined handler waiting on a live join must not deadlock");
    }

    [Fact]
    public void OnSnapshotMember_ShouldLetTheMemberAddedHandlerWaitForAnotherThread_WhenThatThreadRemovesAnotherMember()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-2", "Agent 2", 0, false, 1);
        using var window = _queues.OpenSnapshotWindow();
        _queues.MemberAdded += (_, member) =>
        {
            if (member.Interface == "PJSIP/agent-1")
                _probe.Offload(() => _queues.OnMemberRemoved(Queue, "PJSIP/agent-2"));
        };

        _queues.OnSnapshotMember(window, Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a MemberAdded handler for a listed member, waiting on a live removal, must not deadlock");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheUserLeftHandlerWaitForAnotherThread_WhenThatThreadHandlesAJoin()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");
        using var window = _rooms.OpenSnapshotWindow();
        _rooms.UserLeft += (_, _) => _probe.Offload(() => _rooms.OnUserJoined(Room, 0, "PJSIP/c-1"));

        _rooms.ReconcileWithSnapshot(window, [Room],
            new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> { [Room] = [] }, removeOmitted: true);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a UserLeft handler for a participant the listing omits, waiting on a join, must not deadlock");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheStateHandlerWaitForAnotherThread_WhenThatThreadHandlesATalkingReport()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");
        _rooms.OnUserJoined(Room, 0, "PJSIP/b-1");
        using var window = _rooms.OpenSnapshotWindow();
        _rooms.UserStateChanged += (_, _) => _probe.Offload(() => _rooms.OnUserTalking(Room, "PJSIP/b-1", true));

        _rooms.ReconcileWithSnapshot(window, [Room],
            new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
            {
                [Room] = [new("PJSIP/a-1", "a", true, false), new("PJSIP/b-1", "b", false, false)],
            }, removeOmitted: true);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a UserStateChanged handler for a refreshed field, waiting on a live talking report, must not deadlock");
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldLetTheBridgeDestroyedHandlerWaitForAnotherThread_WhenThatThreadCreatesABridge()
    {
        _bridges.OnBridgeCreated("br-1", "basic", "simple_bridge", "test", null);
        using var window = _bridges.OpenSnapshotWindow();
        _bridges.BridgeDestroyed += _ =>
            _probe.Offload(() => _bridges.OnBridgeCreated("br-2", "basic", "simple_bridge", "test", null));

        _bridges.ReconcileWithSnapshot(window, [], new Dictionary<string, IReadOnlyCollection<string>>(),
            removeOmitted: true);

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a BridgeDestroyed handler for a bridge the listing omits, waiting on a creation, must not deadlock");
    }
}
