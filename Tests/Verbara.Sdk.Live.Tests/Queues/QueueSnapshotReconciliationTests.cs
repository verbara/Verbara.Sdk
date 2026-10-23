using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// How a completed <c>QueueStatus</c> snapshot reconciles <see cref="QueueManager"/>'s table, driven through the window a
/// load reads it with: what it keeps because the snapshot could not have listed it (a caller who joined after the
/// request, a member added after it), what it removes (a member it no longer lists), and how a caller is recognised
/// as held across a rename or a reused channel name.
/// </summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotReconciliationTests
{
    private const string Queue = "q-sales";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);
    private static readonly string[] KeptMembers = ["PJSIP/agent-1", "PJSIP/agent-3"];
    private static readonly string[] RenamedKeys = ["PJSIP/new-1"];
    private static readonly string[] JoinedDuringRemoval = ["PJSIP/b-1"];

    private readonly QueueManager _queues = new(NullLogger.Instance);

    [Fact]
    public void CompleteSnapshot_ShouldKeepACallerWhoRejoinedLive_WhenAnOlderSnapshotOmitsIt()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        // Its leave was lost; Asterisk reports it joining again after the snapshot was asked for.
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        using var counters = new LiveCounters();

        _queues.CompleteSnapshot(window);

        new { Held = _queues.GetByName(Queue)!.Entries.ContainsKey("PJSIP/a-1"), counters.Left }.Should().BeEquivalentTo(
            new { Held = true, Left = 0L },
            "the live join after the request is newer than the snapshot, so the snapshot's silence removes nothing");
    }

    [Fact]
    public void CompleteSnapshot_ShouldKeepACallerWhoJoinsWhileTheSnapshotRemovesTheirQueue_WhenTheSnapshotOmitsTheQueue()
    {
        // The completed snapshot omits q-gone and removes its one caller. A live join into q-gone lands between that
        // removal and the queue's: here inside CallerLeft, which the removal raises, as the event pump's join would on
        // another thread.
        _queues.OnCallerJoined("q-gone", "PJSIP/a-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        using var counters = new LiveCounters();
        _queues.CallerLeft += (queue, entry) =>
        {
            if (queue == "q-gone" && entry.Channel == "PJSIP/a-1")
                _queues.OnCallerJoined("q-gone", "PJSIP/b-1", "200", 1, "u2", Epoch);
        };

        _queues.CompleteSnapshot(window);

        var held = _queues.GetByName("q-gone")?.Entries.Keys.ToArray() ?? [];
        new { Held = held, counters.Joined, counters.Left }.Should().BeEquivalentTo(
            new { Held = JoinedDuringRemoval, Joined = 1L, Left = 1L },
            "the caller who joined while the snapshot removed the queue is held in the table, not lost in a queue object "
            + "the table no longer holds, where its leave would never be counted and joined − left would stay one too high");
    }

    [Fact]
    public void CompleteSnapshot_ShouldKeepAQueueThatHoldsACallerWhoJoinedAfterTheRequest_WhenTheSnapshotOmitsTheQueue()
    {
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnCallerJoined("q-new", "PJSIP/b-1", "200", 1, "u2", Epoch);
        using var counters = new LiveCounters();

        _queues.CompleteSnapshot(window);

        new { Held = _queues.GetByName("q-new")?.EntryCount ?? 0, counters.Left }.Should().BeEquivalentTo(
            new { Held = 1, Left = 0L },
            "a queue the snapshot could not have listed, holding a caller who joined after the request, stays");
    }

    [Fact]
    public void CompleteSnapshot_ShouldRemoveAMemberItNoLongerListsAndKeepOneAddedWhileItWasRead_WhenTheQueueIsListed()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        _queues.OnMemberAdded(Queue, "PJSIP/agent-2", "Agent 2", 0, false, 1);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        _queues.OnSnapshotMember(window, Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        _queues.OnMemberAdded(Queue, "PJSIP/agent-3", "Agent 3", 0, false, 1);
        var removedEvents = 0;
        _queues.MemberRemoved += (_, _) => removedEvents++;

        _queues.CompleteSnapshot(window);

        new
        {
            Members = _queues.GetByName(Queue)!.Members.Keys.Order().ToArray(),
            Agent2Queues = _queues.GetQueuesForMember("PJSIP/agent-2").ToArray(),
            RemovedEvents = removedEvents,
        }.Should().BeEquivalentTo(
            new
            {
                Members = KeptMembers,
                Agent2Queues = Array.Empty<string>(),
                RemovedEvents = 0,
            },
            "the member the snapshot no longer lists is removed, with no event and out of the member index; the one "
            + "added while the snapshot was read is kept");
    }

    [Fact]
    public void CompleteSnapshot_ShouldRemoveAQueueAndItsMembers_WhenTheSnapshotOmitsTheQueue()
    {
        _queues.OnMemberAdded("q-gone", "PJSIP/agent-9", "Agent 9", 0, false, 1);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);

        _queues.CompleteSnapshot(window);

        new
        {
            GoneHeld = _queues.GetByName("q-gone") is not null,
            ListedHeld = _queues.GetByName(Queue) is not null,
            Agent9Queues = _queues.GetQueuesForMember("PJSIP/agent-9").ToArray(),
        }.Should().BeEquivalentTo(
            new { GoneHeld = false, ListedHeld = true, Agent9Queues = Array.Empty<string>() },
            "a queue the completed snapshot does not list is gone, and so is its member from the member index");
    }

    [Fact]
    public void OnSnapshotEntry_ShouldReKeyTheHeldEntryKeepingItsJoinTime_WhenTheCallerIsListedUnderANewChannelName()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/old-1", "300", 2, "u3", Epoch);
        var joinedAt = _queues.GetByName(Queue)!.Entries["PJSIP/old-1"].JoinedAt;
        using var counters = new LiveCounters();
        using var window = _queues.OpenSnapshotWindow();

        _queues.OnSnapshotEntry(window, Queue, "PJSIP/new-1", "u3", "300", 1, 7, Epoch);

        var entries = _queues.GetByName(Queue)!.Entries;
        new
        {
            Keys = entries.Keys.ToArray(),
            entries["PJSIP/new-1"].JoinedAt,
            entries["PJSIP/new-1"].Position,
            counters.Joined,
            counters.Left,
        }.Should().BeEquivalentTo(
            new { Keys = RenamedKeys, JoinedAt = joinedAt, Position = 1, Joined = 0L, Left = 0L },
            "the same caller (same Uniqueid) under its new name is a replacement: re-keyed, join time kept, nothing counted");
    }

    [Fact]
    public void OnCallerJoined_ShouldCountTheEarlierCallerLeftAndAnnounceIt_WhenAnotherCallerArrivesUnderTheSameChannelName()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/c-1", "400", 1, "u4", Epoch);
        var left = new List<AsteriskQueueEntry>();
        _queues.CallerLeft += (_, e) => left.Add(e);
        using var counters = new LiveCounters();

        _queues.OnCallerJoined(Queue, "PJSIP/c-1", "401", 1, "u5", Epoch);

        new
        {
            HeldUniqueId = _queues.GetByName(Queue)!.Entries["PJSIP/c-1"].UniqueId,
            LeftUniqueIds = left.Select(e => e.UniqueId).ToArray(),
            LeftUnobserved = left.All(e => e.LeaveNotObserved),
            counters.Joined,
            counters.Left,
            counters.WaitSamples,
        }.Should().BeEquivalentTo(
            new
            {
                HeldUniqueId = "u5",
                LeftUniqueIds = new List<string> { "u4" },
                LeftUnobserved = true,
                Joined = 1L,
                Left = 1L,
                WaitSamples = 0L,
            },
            "a caller with another Uniqueid on the same channel name replaces a caller who is no longer there: one left "
            + "with no wait sample, marked as unobserved, and one joined");
    }
}
