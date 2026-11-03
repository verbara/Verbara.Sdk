using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// What a <see cref="QueueManager.CallerLeft"/> handler reads from <see cref="AsteriskQueueEntry.LeaveNotObserved"/>, one
/// test per path that takes a caller out of the table. Each reads the value inside the handler, where a consumer does.
/// This assembly is a friend of Live, so these prove the marking; what a consumer can read and assign is proved by the
/// compilations in the DocSnippets tests.
/// </summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueLeaveNotObservedTests
{
    private const string Queue = "q-sales";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    private readonly QueueManager _queues = new(NullLogger.Instance);
    private readonly List<ReadInHandler> _left = [];

    public QueueLeaveNotObservedTests() =>
        _queues.CallerLeft += (queue, entry) => _left.Add(new ReadInHandler(queue, entry.UniqueId, entry.LeaveNotObserved));

    [Fact]
    public void CompleteSnapshot_ShouldLetTheHandlerReadTrue_WhenTheSnapshotOmitsTheCaller()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);

        _queues.CompleteSnapshot(window);

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: true));
    }

    [Fact]
    public void CompleteSnapshot_ShouldLetTheHandlerReadTrue_WhenTheSnapshotNoLongerListsTheQueue()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        using var window = _queues.OpenSnapshotWindow();
        _queues.OnSnapshotQueueParams(window, "q-other", 0, "ringall", 0, 0, 0, 0, 0);

        _queues.CompleteSnapshot(window);

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: true));
    }

    [Fact]
    public void OnChannelRemovedByReconciliation_ShouldLetTheHandlerReadTrue_WhenItRemovesTheCallersChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnChannelRemovedByReconciliation("u1", "PJSIP/a-1");

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: true));
    }

    [Fact]
    public void OnCallerJoined_ShouldLetTheHandlerReadTrue_WhenAnotherUniqueidTakesTheChannelName()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "200", 1, "u2", Epoch);

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: true));
    }

    [Fact]
    public void OnCallerJoined_ShouldLetTheHandlerReadTrue_WhenARekeyedRenameTakesTheChannelName()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined(Queue, "PJSIP/b-1", "200", 2, "u2", Epoch);

        // The caller held as a-1 is reported under the name b-1 is held under: re-keyed onto it, displacing b-1.
        _queues.OnCallerJoined(Queue, "PJSIP/b-1", "100", 1, "u1", Epoch);

        _left.Should().Equal(new ReadInHandler(Queue, "u2", LeaveNotObserved: true));
    }

    [Fact]
    public void OnCallerLeft_ShouldLetTheHandlerReadFalse_WhenAsteriskReportsTheLeave()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerLeft(Queue, "PJSIP/a-1");

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: false));
    }

    [Fact]
    public void RemoveQueue_ShouldMarkTheEntryAConsumerHoldsAndRaiseNoCallerLeft_WhenTheHostRemovesTheQueue()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        var held = _queues.GetByName(Queue)!.Entries["PJSIP/a-1"];

        _queues.RemoveQueue(Queue);

        new { Raised = _left.Count, held.LeaveNotObserved }.Should().BeEquivalentTo(
            new { Raised = 0, LeaveNotObserved = true },
            "RemoveQueue raises no CallerLeft by its contract, so the mark is readable only on an entry already held");
    }

    [Fact]
    public void Clear_ShouldMarkTheEntryAConsumerHolds_WhenTheHostEmptiesTheTable()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        var held = _queues.GetByName(Queue)!.Entries["PJSIP/a-1"];

        _queues.Clear();

        new { Raised = _left.Count, held.LeaveNotObserved }.Should().BeEquivalentTo(
            new { Raised = 0, LeaveNotObserved = true },
            "Clear raises nothing, so the mark is readable only on the instance the table held");
    }

    [Fact]
    public void Clear_ShouldTakeTheEntriesOutOfARetainedQueue_WhenTheHostEmptiesTheTable()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        var retained = _queues.GetByName(Queue)!;

        _queues.Clear();

        new { retained.RemovedFromTable, retained.EntryCount }.Should().BeEquivalentTo(
            new { RemovedFromTable = true, EntryCount = 0 },
            "a join in flight must not land in the orphaned queue, and a leave in flight must not find a marked entry");
    }

    [Fact]
    public void OnCallerLeft_ShouldLeaveTheCallerToReadTrue_WhenTheReportNamesAChannelLiveDoesNotHold()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);

        _queues.OnCallerLeft(Queue, "PJSIP/renamed-1");
        _queues.OnChannelRemovedByReconciliation("u1", "PJSIP/a-1");

        _left.Should().Equal(new ReadInHandler(Queue, "u1", LeaveNotObserved: true));
    }

    [Fact]
    public void OnCallerLeft_ShouldLetTheHandlerReadFalse_WhenALateReportNamesAReusedChannel()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "200", 1, "u2", Epoch);
        _left.Clear();

        // The earlier caller's late report: leaves are matched by channel name, so it removes whoever holds the name.
        _queues.OnCallerLeft(Queue, "PJSIP/a-1");

        _left.Should().Equal(new ReadInHandler(Queue, "u2", LeaveNotObserved: false));
    }

    [Fact]
    public void OnChannelRemovedByReconciliation_ShouldHaveTheMarkFinal_WhenTheLeaveIsPosted()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/a-1", "100", 1, "u1", Epoch);
        var readWhenPosted = new List<bool>();
        _queues.Events.Posted = (_, entry) => readWhenPosted.Add(((AsteriskQueueEntry)entry!).LeaveNotObserved);

        _queues.OnChannelRemovedByReconciliation("u1", "PJSIP/a-1");

        readWhenPosted.Should().Equal([true], "a mark written after the post could reach a handler on another thread late");
    }

    private sealed record ReadInHandler(string Queue, string? UniqueId, bool LeaveNotObserved);
}
