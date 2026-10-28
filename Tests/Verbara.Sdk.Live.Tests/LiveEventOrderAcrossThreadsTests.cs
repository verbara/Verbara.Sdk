using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
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

    // A wait far longer than the outbox's turn wait: when it runs out, the thread under test was blocked behind it.
    private static readonly TimeSpan OtherThreadWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void OnCallerLeaveReported_ShouldThrowItsOwnHandlersFailure_WhenAnotherThreadIsRaisingThisManagersEvents()
    {
        using var inHandler = new ManualResetEventSlim();
        using var reported = new ManualResetEventSlim();
        _queues.MemberAdded += (_, _) =>
        {
            inHandler.Set();
            reported.Wait(OtherThreadWait);
        };
        _queues.CallerLeaveReported += _ => throw new InvalidOperationException("leave-handler");
        Exception? reportFailure = null;
        var dispatch = new Thread(() =>
        {
            inHandler.Wait(OtherThreadWait);
            try
            {
                _queues.OnCallerLeaveReported("u1", Queue, "PJSIP/caller-1", Epoch);
            }
            // Reported by the assertion below.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reportFailure = ex;
            }
            finally
            {
                reported.Set();
            }
        });
        dispatch.Start();

        Exception? loadFailure = null;
        using (var window = _queues.OpenSnapshotWindow())
            loadFailure = Record.Exception(() =>
                _queues.OnSnapshotMember(window, Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1));
        dispatch.Join(OtherThreadWait).Should().BeTrue();

        using var scope = new AssertionScope();
        reportFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("leave-handler",
            "the throwing handler ran for the report, so the report's call must see its failure");
        loadFailure.Should().BeNull("the snapshot row's own handler did not throw");
    }

    [Fact]
    public void OnCallerLeft_ShouldNotThrowAnotherThreadsHandlerFailure_WhenItsHandlerRunsWhileThatThreadRaises()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1, "u1", Epoch);
        using var inHandler = new ManualResetEventSlim();
        using var loaded = new ManualResetEventSlim();
        _queues.CallerLeft += (_, _) =>
        {
            inHandler.Set();
            loaded.Wait(OtherThreadWait);
        };
        _queues.MemberAdded += (_, _) => throw new InvalidOperationException("snapshot-member-handler");
        Exception? loadFailure = null;
        var load = new Thread(() =>
        {
            inHandler.Wait(OtherThreadWait);
            using var window = _queues.OpenSnapshotWindow();
            loadFailure = Record.Exception(() =>
                _queues.OnSnapshotMember(window, Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1));
            loaded.Set();
        });
        load.Start();

        var leftFailure = Record.Exception(() => _queues.OnCallerLeft(Queue, "PJSIP/caller-1"));
        load.Join(OtherThreadWait).Should().BeTrue();

        using var scope = new AssertionScope();
        leftFailure.Should().BeNull("the leave's own handler did not throw");
        loadFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("snapshot-member-handler",
            "a reload row whose handler throws must fail the row's call, not an unrelated one");
    }

    [Fact]
    public void OnCallerLeaveReported_ShouldRaiseItsEventBeforeReturning_WhenAnotherThreadIsRaisingThisManagersEvents()
    {
        var channels = new ChannelManager(NullLogger.Instance);
        var seen = new ConcurrentQueue<string>();
        using var inHandler = new ManualResetEventSlim();
        using var dispatched = new ManualResetEventSlim();
        _queues.MemberAdded += (_, _) =>
        {
            inHandler.Set();
            dispatched.Wait(OtherThreadWait);
        };
        _queues.CallerLeaveReported += report => seen.Enqueue("leave:" + report.UniqueId);
        channels.ChannelAdded += channel => seen.Enqueue("channel:" + channel.UniqueId);
        var dispatch = new Thread(() =>
        {
            inHandler.Wait(OtherThreadWait);
            _queues.OnCallerLeaveReported("u1", Queue, "PJSIP/caller-1", Epoch);
            channels.OnNewChannel("u2", "PJSIP/caller-2", ChannelState.Ring);
            dispatched.Set();
        });
        dispatch.Start();

        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        dispatch.Join(OtherThreadWait).Should().BeTrue();

        seen.Should().Equal(["leave:u1", "channel:u2"],
            "the AMI thread handled the leave before the channel, so a subscriber to both managers must see that order");
    }

    [Fact]
    public void QueueManager_ShouldRaiseEachEntrysAndMembersEventsInOrder_WhenManyThreadsChangeThem()
    {
        var log = new ConcurrentQueue<(object Key, char Kind)>();
        _queues.CallerJoined += (_, entry) => { log.Enqueue((entry, 'J')); YieldSometimes(); };
        _queues.CallerLeft += (_, entry) => log.Enqueue((entry, 'L'));
        _queues.MemberAdded += (_, member) => { log.Enqueue((member, 'A')); YieldSometimes(); };
        _queues.MemberRemoved += (_, member) => log.Enqueue((member, 'R'));
        _queues.MemberStatusChanged += (_, member) => log.Enqueue((member, 'S'));

        RunConcurrently((thread, i) =>
        {
            var channel = "PJSIP/caller-" + (i % 5);
            var iface = "PJSIP/agent-" + (i % 3);
            switch ((thread + i) % 5)
            {
                case 0: _queues.OnCallerJoined(Queue, channel, "1", 1, "u-" + channel, Epoch); break;
                case 1: _queues.OnCallerLeft(Queue, channel); break;
                case 2: _queues.OnMemberAdded(Queue, iface, "agent", 0, false, 1); break;
                case 3: _queues.OnMemberRemoved(Queue, iface); break;
                default: _queues.OnMemberStatusChanged(Queue, iface, 2 + (i % 3)); break;
            }
        });

        OutOfOrder(log, kinds => kinds[0] is 'J'
            ? kinds is "J" or "JL"
            : kinds[0] == 'A' && kinds.IndexOf('A', 1) < 0 && (kinds.IndexOf('R') < 0 || kinds.IndexOf('R') == kinds.Length - 1))
            .Should().BeEmpty("each entry joins once before it leaves, and each member is added once before its last event");
    }

    [Fact]
    public void MeetMeManager_ShouldRaiseEachParticipantsEventsInOrder_WhenManyThreadsChangeThem()
    {
        var log = new ConcurrentQueue<(object Key, char Kind)>();
        _rooms.UserJoined += (_, user) => { log.Enqueue((user, 'J')); YieldSometimes(); };
        _rooms.UserLeft += (_, user) => log.Enqueue((user, 'L'));
        _rooms.UserStateChanged += (_, user) => log.Enqueue((user, 'S'));

        RunConcurrently((thread, i) =>
        {
            var channel = "PJSIP/m-" + (i % 4);
            switch ((thread + i) % 5)
            {
                case 0: _rooms.OnUserJoined(Room, i, channel, "user", muted: false); break;
                case 1: _rooms.OnUserLeft(Room, channel); break;
                case 2: _rooms.OnUserMuted(Room, channel, true); break;
                case 3: _rooms.OnUserMuted(Room, channel, false); break;
                default:
                    if (i % 97 == 0)
                        _rooms.OnConferenceEnded(Room);
                    else
                        _rooms.OnUserTalking(Room, channel, i % 2 == 0);
                    break;
            }
        });

        OutOfOrder(log, kinds => kinds[0] == 'J' && kinds.IndexOf('J', 1) < 0
                                 && (kinds.IndexOf('L') < 0 || kinds.IndexOf('L') == kinds.Length - 1))
            .Should().BeEmpty("each participant joins once, and nothing about it is raised after it left");
    }

    private static void YieldSometimes()
    {
        if (Random.Shared.Next(4) == 0)
            Thread.Yield();
    }

    private static void RunConcurrently(Action<int, int> step)
    {
        const int Threads = 8;
        const int Iterations = 2000;
        using var start = new Barrier(Threads);
        var workers = Enumerable.Range(0, Threads).Select(thread => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < Iterations; i++)
                step(thread, i);
        })).ToList();
        workers.ForEach(worker => worker.Start());
        workers.ForEach(worker => worker.Join());
    }

    private static List<string> OutOfOrder(IEnumerable<(object Key, char Kind)> log, Func<string, bool> inOrder) =>
        log.GroupBy(record => record.Key, ReferenceEqualityComparer.Instance)
            .Select(group => string.Concat(group.Select(record => record.Kind)))
            .Where(kinds => !inOrder(kinds))
            .ToList();

    [Fact]
    public void Drain_ShouldRaiseANotificationItsThreadNeverRaised_AndReportItsFailureInsteadOfThrowingIt()
    {
        var reported = new List<string>();
        var outbox = new LiveEventOutbox(ex => reported.Add(ex.Message), TimeSpan.FromMilliseconds(50));
        var raised = new List<string>();
        var poster = new Thread(() => outbox.Post(() =>
        {
            raised.Add("abandoned");
            throw new InvalidOperationException("abandoned-handler");
        }));
        poster.Start();
        poster.Join();
        outbox.Post(() => raised.Add("mine"));

        var drain = outbox.Drain;

        drain.Should().NotThrow("the failing notification was posted by a thread that never raised it");
        raised.Should().Equal("abandoned", "mine");
        reported.Should().Equal("abandoned-handler");
    }

    // The window is the moment a drain has raised its last notification and has not yet left its frame. The hook holds
    // the drain there while a thread started from its frame (as a handler's task would be) posts and drains, so the
    // interleaving is forced rather than hoped for.
    [Fact]
    public void Drain_ShouldRaiseANotificationBeforeReturning_WhenAThreadStartedFromAFinishingDrainPostsIt()
    {
        var outbox = new LiveEventOutbox(_ => { }, OtherThreadWait);
        var raised = 0;
        var raisedWhenItsDrainReturned = -1;
        var hooked = 0;
        outbox.FrameExiting = () =>
        {
            if (Interlocked.Exchange(ref hooked, 1) != 0)
                return;
            // Started on the finishing drain's thread: the execution context, and the drain's frame with it, flows here.
            var started = new Thread(() =>
            {
                outbox.Post(() => Interlocked.Increment(ref raised));
                outbox.Drain();
                Volatile.Write(ref raisedWhenItsDrainReturned, Volatile.Read(ref raised));
            }) { IsBackground = true, Name = "outbox-frame-exit-poster" };
            started.Start();
            started.Join(OtherThreadWait).Should().BeTrue("the started thread's drain must return");
        };
        outbox.Post(() => { });

        outbox.Drain();

        raisedWhenItsDrainReturned.Should().Be(1,
            "a drain that has nothing left to raise no longer adopts a post, so the posting thread raises it itself");
        Volatile.Read(ref raised).Should().Be(1);
    }

    [Fact]
    public void Drain_ShouldRaiseEveryNotificationAndRethrowTheFirstFailure_WhenAHandlerThrows()
    {
        var outbox = new LiveEventOutbox(_ => { }, LiveEventOutbox.DefaultTurnWait);
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
