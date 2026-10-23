using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Queues;

namespace Verbara.Sdk.Live.Tests;

/// <summary>
/// A subscriber that hands work to another thread from inside a Live event handler and waits for it, where that work
/// calls back into the same manager: the shape of a handler that marshals onto a single-threaded context, or awaits
/// synchronously a task that touches the manager. The wait must end. If the event is raised while the manager holds a
/// lock that the other thread's call takes, the two threads wait on each other: the handler's wait is bounded, so the
/// test fails in seconds, and the lock is released when the handler gives up, so the other thread finishes and the run
/// never hangs.
/// </summary>
public sealed class LiveEventHandlerOffloadTests
{
    private const string Queue = "q-sales";
    private const string Room = "room-1";

    private readonly QueueManager _queues = new(NullLogger.Instance);
    private readonly MeetMeManager _rooms = new(NullLogger.Instance);
    private readonly BridgeManager _bridges = new(NullLogger.Instance);
    private readonly HandlerOffloadProbe _probe = new();

    [Fact]
    public void OnMemberAdded_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadRemovesAnotherMember()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-2", "Agent 2", 0, false, 1);
        _queues.MemberAdded += (_, member) =>
        {
            if (member.Interface == "PJSIP/agent-1")
                _probe.Offload(() => _queues.OnMemberRemoved(Queue, "PJSIP/agent-2"));
        };

        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a MemberAdded handler waiting on a member removal must not deadlock");
    }

    [Fact]
    public void OnMemberAdded_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesACallerLeave()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1);
        _queues.MemberAdded += (_, _) => _probe.Offload(() => _queues.OnCallerLeft(Queue, "PJSIP/caller-1"));

        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a MemberAdded handler waiting on a caller leave must not deadlock");
    }

    [Fact]
    public void OnMemberRemoved_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadAddsAMember()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        _queues.MemberRemoved += (_, _) =>
            _probe.Offload(() => _queues.OnMemberAdded(Queue, "PJSIP/agent-2", "Agent 2", 0, false, 1));

        _queues.OnMemberRemoved(Queue, "PJSIP/agent-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a MemberRemoved handler waiting on a member add must not deadlock");
    }

    [Fact]
    public void OnMemberStatusChanged_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadPausesTheMember()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        _queues.MemberStatusChanged += (_, _) =>
            _probe.Offload(() => _queues.OnMemberPaused(Queue, "PJSIP/agent-1", true, "break"));

        _queues.OnMemberStatusChanged(Queue, "PJSIP/agent-1", 2);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a MemberStatusChanged handler waiting on a pause must not deadlock");
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldLetTheStatusHandlerWaitForAnotherThread_WhenThatThreadPausesTheMember()
    {
        _queues.OnMemberAdded(Queue, "PJSIP/agent-1", "Agent 1", 0, false, 1);
        _queues.MemberStatusChanged += (_, _) =>
            _probe.Offload(() => _queues.OnMemberPaused(Queue, "PJSIP/agent-1", true, "break"));

        _queues.OnDeviceStateChanged("PJSIP/agent-1", "INUSE");

        _probe.FinishedInsideTheHandler().Should().BeTrue(
            "a MemberStatusChanged handler raised by a device state waiting on a pause must not deadlock");
    }

    [Fact]
    public void OnCallerJoined_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesAnotherJoin()
    {
        _queues.CallerJoined += (_, entry) =>
        {
            if (entry.Channel == "PJSIP/caller-1")
                _probe.Offload(() => _queues.OnCallerJoined(Queue, "PJSIP/caller-2", "200", 2));
        };

        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a CallerJoined handler waiting on another join must not deadlock");
    }

    [Fact]
    public void OnCallerJoined_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesTheCallersLeave()
    {
        _queues.CallerJoined += (_, _) => _probe.Offload(() => _queues.OnCallerLeft(Queue, "PJSIP/caller-1"));

        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a CallerJoined handler waiting on that caller's leave must not deadlock");
    }

    [Fact]
    public void OnCallerLeft_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesAJoin()
    {
        _queues.OnCallerJoined(Queue, "PJSIP/caller-1", "100", 1);
        _queues.CallerLeft += (_, _) => _probe.Offload(() => _queues.OnCallerJoined(Queue, "PJSIP/caller-2", "200", 2));

        _queues.OnCallerLeft(Queue, "PJSIP/caller-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a CallerLeft handler waiting on a join must not deadlock");
    }

    [Fact]
    public void OnUserJoined_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesAnotherLeave()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/b-1");
        _rooms.UserJoined += (_, user) =>
        {
            if (user.Channel == "PJSIP/a-1")
                _probe.Offload(() => _rooms.OnUserLeft(Room, "PJSIP/b-1"));
        };

        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a UserJoined handler waiting on another participant's leave must not deadlock");
    }

    [Fact]
    public void OnUserLeft_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesAJoin()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");
        _rooms.OnUserJoined(Room, 0, "PJSIP/b-1");
        _rooms.UserLeft += (_, _) => _probe.Offload(() => _rooms.OnUserJoined(Room, 0, "PJSIP/c-1"));

        _rooms.OnUserLeft(Room, "PJSIP/a-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a UserLeft handler waiting on a join must not deadlock");
    }

    [Fact]
    public void OnUserMuted_ShouldLetTheStateHandlerWaitForAnotherThread_WhenThatThreadHandlesAnotherLeave()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");
        _rooms.OnUserJoined(Room, 0, "PJSIP/b-1");
        _rooms.UserStateChanged += (_, _) => _probe.Offload(() => _rooms.OnUserLeft(Room, "PJSIP/b-1"));

        _rooms.OnUserMuted(Room, "PJSIP/a-1", true);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a UserStateChanged handler waiting on a leave must not deadlock");
    }

    [Fact]
    public void OnConferenceEnded_ShouldLetTheLeaveHandlerWaitForAnotherThread_WhenThatThreadClearsTheManager()
    {
        _rooms.OnUserJoined(Room, 0, "PJSIP/a-1");
        _rooms.UserLeft += (_, _) => _probe.Offload(_rooms.Clear);

        _rooms.OnConferenceEnded(Room);

        _probe.FinishedInsideTheHandler().Should().BeTrue("a UserLeft handler raised by a room's end waiting on Clear must not deadlock");
    }

    [Fact]
    public void OnChannelEntered_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadHandlesAnotherLeave()
    {
        _bridges.OnBridgeCreated("br-1", "basic", "simple_bridge", "test", null);
        _bridges.OnChannelEntered("br-1", "ch-2");
        _bridges.ChannelEntered += (_, uniqueId) =>
        {
            if (uniqueId == "ch-1")
                _probe.Offload(() => _bridges.OnChannelLeft("br-1", "ch-2"));
        };

        _bridges.OnChannelEntered("br-1", "ch-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a ChannelEntered handler waiting on a leave must not deadlock");
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldLetItsHandlerWaitForAnotherThread_WhenThatThreadCreatesABridge()
    {
        _bridges.OnBridgeCreated("br-1", "basic", "simple_bridge", "test", null);
        _bridges.BridgeDestroyed += _ =>
            _probe.Offload(() => _bridges.OnBridgeCreated("br-2", "basic", "simple_bridge", "test", null));

        _bridges.OnBridgeDestroyed("br-1");

        _probe.FinishedInsideTheHandler().Should().BeTrue("a BridgeDestroyed handler waiting on a creation must not deadlock");
    }
}

/// <summary>
/// Runs one call on its own thread from inside an event handler and waits for it there, boundedly. Only the first
/// raise offloads, so a call that raises the same event again does not recurse.
/// </summary>
internal sealed class HandlerOffloadProbe
{
    /// <summary>How long the handler waits: far longer than any of the calls takes when nothing blocks it.</summary>
    internal static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(2);

    /// <summary>How long the test waits, once the raise returned and released its locks, for the call to finish.</summary>
    internal static readonly TimeSpan Drain = TimeSpan.FromSeconds(10);

    // A promise-style task: waiting on it never runs anything inline on the waiting thread.
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _offloaded;
    private Exception? _failure;
    private bool? _finishedInsideHandler;

    internal void Offload(Action call)
    {
        if (Interlocked.Exchange(ref _offloaded, 1) != 0)
            return;

        var thread = new Thread(() =>
        {
            try
            {
                call();
            }
#pragma warning disable CA1031 // Reported by the assertion below, on the test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _failure = ex;
            }
            finally
            {
                _finished.SetResult();
            }
        })
        {
            IsBackground = true,
            Name = "handler-offload",
        };
        thread.Start();
        _finishedInsideHandler = _finished.Task.Wait(HandlerWait);
    }

    /// <summary>
    /// Whether the offloaded call finished while the handler waited for it, once the preconditions hold: the event was
    /// raised and offloaded, the call finished after the raise returned at the latest, and it did not throw.
    /// </summary>
    internal bool FinishedInsideTheHandler()
    {
        _finishedInsideHandler.Should().NotBeNull("the event under test must have been raised and offloaded");
        _finished.Task.Wait(Drain).Should().BeTrue("the offloaded call must finish once the raise has returned");
        _failure.Should().BeNull("the offloaded call must not throw");
        return _finishedInsideHandler == true;
    }
}
