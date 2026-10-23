using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests;

/// <summary>
/// The managers raise their events after releasing their locks, in the order of the transitions: a handler that waits
/// for another thread calling into the same manager gets that thread's events after its own, never before.
/// </summary>
public sealed class LiveEventOrderAcrossThreadsTests
{
    private const string Queue = "q-sales";
    private const string Room = "room-1";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);

    private readonly QueueManager _queues = new(NullLogger.Instance);
    private readonly MeetMeManager _rooms = new(NullLogger.Instance);
    private readonly HandlerOffloadProbe _probe = new();

    [Fact]
    public void NotifySnapshotEntry_ShouldRaiseTheJoinBeforeTheLeave_WhenItsHandlerWaitsForAThreadReportingThatLeave()
    {
        using var window = _queues.OpenSnapshotWindow();
        var reported = _queues.AdmitSnapshotEntry(window, Queue, "PJSIP/caller-1", "u1", "100", 1, 5, Epoch,
            DateTimeOffset.UtcNow, announce: false)!;
        var seen = new List<string>();
        _queues.CallerJoined += (_, _) =>
            _probe.Offload(() => _queues.OnCallerLeaveReported("u1", Queue, "PJSIP/caller-1", Epoch));
        // Subscribed after the waiting handler: a leave raised while that handler waits would reach this one first.
        _queues.CallerJoined += (_, entry) => seen.Add("joined:" + entry.UniqueId);
        _queues.CallerLeaveReported += report => seen.Add("left:" + report.UniqueId);

        _queues.NotifySnapshotEntry(window, Queue, reported);

        using var scope = new AssertionScope();
        _probe.FinishedInsideTheHandler().Should().BeTrue("the leave's thread must not wait for the join's handler");
        seen.Should().Equal(["joined:u1", "left:u1"],
            "the join was admitted before the leave was recorded, so the session layer must see it first");
    }

    [Fact]
    public void OnUserJoined_ShouldRaiseTheJoinBeforeTheLeave_WhenItsHandlerWaitsForAThreadHandlingThatLeave()
    {
        var seen = new List<string>();
        _rooms.UserJoined += (_, _) => _probe.Offload(() => _rooms.OnUserLeft(Room, "PJSIP/a-1"));
        // Subscribed after the waiting handler: a leave raised while that handler waits would reach this one first.
        _rooms.UserJoined += (_, user) => seen.Add("joined:" + user.Channel);
        _rooms.UserLeft += (_, user) => seen.Add("left:" + user.Channel);

        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");

        using var scope = new AssertionScope();
        _probe.FinishedInsideTheHandler().Should().BeTrue("the leave's thread must not wait for the join's handler");
        seen.Should().Equal(["joined:PJSIP/a-1", "left:PJSIP/a-1"],
            "a subscriber must not end up holding a participant that left");
        _rooms.GetRoom(Room).Should().BeNull();
    }

    [Fact]
    public void Drain_ShouldRaiseEveryNotificationAndRethrowTheFirstFailure_WhenAHandlerThrows()
    {
        var outbox = new LiveEventOutbox();
        var raised = new List<int>();
        outbox.Post(() => throw new InvalidOperationException("first"));
        outbox.Post(() => raised.Add(2));
        outbox.Post(() => throw new InvalidOperationException("third"));

        var drain = outbox.Drain;

        drain.Should().Throw<InvalidOperationException>().WithMessage("first");
        raised.Should().Equal(2);
        outbox.Post(() => raised.Add(4));
        outbox.Drain();
        raised.Should().Equal(2, 4);
    }
}
