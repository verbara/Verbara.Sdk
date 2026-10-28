using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// What the queue table's reports cost in heap bytes: a member's status (from <c>QueueMemberStatus</c> or a
/// <c>DeviceStateChange</c>), a <c>VarSet</c> of <c>QUEUESTATUS</c>, a <c>QueueCallerAbandon</c>, and every other
/// notification the table raises: queue params, a member added or removed, a caller joining, leaving or reported
/// leaving, and a completed snapshot. Bytes, not time: the allocation counter is exact and per thread, and every
/// object counted escapes into the outbox, so no JIT tier removes it. A caller's join and leave record metrics; the
/// classes that listen to them run in a collection no other class runs beside, so no listener runs on the measuring
/// thread.
/// </summary>
public sealed class QueueManagerAllocationTests
{
    private const int Members = 1_000;
    private const int WarmUpRounds = 3;
    private const string Queue = "sales";

    // One queued entry, one drain frame and the two execution-context copies the drain's frame switch makes, with
    // slack for that copy's size across runtime versions. A capturing lambda per report lands above it.
    private const int ObservedEventBound = 200;

    private static readonly string[] Interfaces = Enumerable.Range(0, Members)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"PJSIP/agent-{i}"))
        .ToArray();

    private static readonly string[] UniqueIds = Enumerable.Range(0, Members)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"1792103773.{i}"))
        .ToArray();

    private static readonly string[] Channels = Enumerable.Range(0, Members)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"PJSIP/caller-{i:x8}"))
        .ToArray();

    private static readonly string[] QueueNames = Enumerable.Range(0, Members)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"queue-{i}"))
        .ToArray();

    private static readonly EventLossEpoch LossEpoch = new(Reconnects: 0, AmiSession: 1, DroppedBefore: 0);

    private int _observed;

    [Fact]
    public void OnMemberStatusChanged_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = WithMembers();

        var allocated = Measure(() =>
        {
            foreach (var iface in Interfaces)
                manager.OnMemberStatusChanged(Queue, iface, (int)QueueMemberState.DeviceInUse);
        });

        allocated.Should().Be(0, "a member status nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnMemberStatusChanged_ShouldAllocateAtMostOneEntryAndOneFrame_WhenOneHandlerIsSubscribed()
    {
        var manager = WithMembers();
        manager.MemberStatusChanged += CountMember;

        var allocated = MeasureObserved(() =>
        {
            foreach (var iface in Interfaces)
                manager.OnMemberStatusChanged(Queue, iface, (int)QueueMemberState.DeviceInUse);
        });

        allocated.Should().BeLessThanOrEqualTo((long)ObservedEventBound * Members,
            "an observed member status costs one queued entry and its drain, nothing captured per report");
    }

    // The device's queues are read from the reverse index as a snapshot, which allocates whether or not anything is
    // raised. The control manager holds the same index with the members gone from the queue, so it walks the same
    // snapshot and reaches no notification: the difference is what the notification costs.
    [Fact]
    public void OnDeviceStateChanged_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = WithMembers();
        var control = WithIndexedMembersGone();

        var allocated = Measure(() =>
        {
            foreach (var iface in Interfaces)
                manager.OnDeviceStateChanged(iface, "INUSE");
        });
        var baseline = Measure(() =>
        {
            foreach (var iface in Interfaces)
                control.OnDeviceStateChanged(iface, "INUSE");
        });

        (allocated - baseline).Should().Be(0, "a device's member status nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldAllocateAtMostOneEntryAndOneFrameForTheEvent_WhenOneHandlerIsSubscribed()
    {
        var manager = WithMembers();
        manager.MemberStatusChanged += CountMember;
        var control = WithIndexedMembersGone();

        var allocated = MeasureObserved(() =>
        {
            foreach (var iface in Interfaces)
                manager.OnDeviceStateChanged(iface, "INUSE");
        });
        var baseline = Measure(() =>
        {
            foreach (var iface in Interfaces)
                control.OnDeviceStateChanged(iface, "INUSE");
        });

        (allocated - baseline).Should().BeLessThanOrEqualTo((long)ObservedEventBound * Members,
            "an observed device status costs one queued entry and its drain, nothing captured per report");
    }

    [Fact]
    public void OnCallerQueueStatus_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);

        var allocated = Measure(() =>
        {
            for (var i = 0; i < Members; i++)
                manager.OnCallerQueueStatus(UniqueIds[i], Channels[i], "TIMEOUT");
        });

        allocated.Should().Be(0, "a QUEUESTATUS nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnCallerQueueStatus_ShouldAllocateAtMostOneEntryAndOneFrame_WhenOneHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);
        manager.CallerQueueStatus += CountStatus;

        var allocated = MeasureObserved(() =>
        {
            for (var i = 0; i < Members; i++)
                manager.OnCallerQueueStatus(UniqueIds[i], Channels[i], "TIMEOUT");
        });

        allocated.Should().BeLessThanOrEqualTo((long)ObservedEventBound * Members,
            "an observed QUEUESTATUS costs one queued entry and its drain, its three values carried without a closure");
    }

    [Fact]
    public void OnCallerAbandonReported_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);

        var allocated = Measure(() =>
        {
            foreach (var uniqueId in UniqueIds)
                manager.OnCallerAbandonReported(uniqueId, Queue);
        });

        allocated.Should().Be(0, "an abandon nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnCallerAbandonReported_ShouldAllocateAtMostOneEntryAndOneFrame_WhenOneHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);
        manager.CallerAbandonReported += CountAbandon;

        var allocated = MeasureObserved(() =>
        {
            foreach (var uniqueId in UniqueIds)
                manager.OnCallerAbandonReported(uniqueId, Queue);
        });

        allocated.Should().BeLessThanOrEqualTo((long)ObservedEventBound * Members,
            "an observed abandon costs one queued entry and its drain, nothing captured per report");
    }

    // A path that allocates for its own bookkeeping (a new member or caller entry, a capturing queue factory, a
    // snapshot's completion) is measured against a control manager doing the same work with a handler subscribed,
    // less the price of the notifications that handler received, read on the abandon path, which allocates nothing
    // else. What is left is what the event costs when nobody hears it.
    [Fact]
    public void OnQueueParams_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = WithQueues();
        var control = WithQueues();
        control.QueueUpdated += CountQueue;

        var allocated = Measure(() => Params(manager));
        var observed = MeasureObserved(() => Params(control));

        (allocated - (observed - NotificationPrice())).Should().Be(0, "queue params nobody is subscribed to queue nothing");
    }

    // Each round adds again the members the queue holds: every add replaces the member and announces the new one.
    [Fact]
    public void OnMemberAdded_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = WithMembers();
        var control = WithMembers();
        control.MemberAdded += CountMember;

        var allocated = Measure(() => AddMembers(manager));
        var observed = MeasureObserved(() => AddMembers(control));

        (allocated - (observed - NotificationPrice())).Should().Be(0, "a member added nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnMemberRemoved_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = WithMembers();

        var allocated = MeasureAfter(() => AddMembers(manager), () =>
        {
            foreach (var iface in Interfaces)
                manager.OnMemberRemoved(Queue, iface);
        });

        manager.GetByName(Queue)!.Members.Should().BeEmpty("every report in the round removed its member");
        allocated.Should().Be(0, "a member removed nobody is subscribed to queues nothing");
    }

    // A join allocates the caller's entry; the leave in the same round takes it out again, so every join is an
    // insertion. Only CallerJoined is subscribed on the control, so the leaves cost the two managers the same.
    [Fact]
    public void OnCallerJoined_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);
        var control = new QueueManager(NullLogger.Instance);
        control.CallerJoined += CountCaller;

        var allocated = Measure(() => JoinAndLeave(manager));
        var observed = MeasureObserved(() => JoinAndLeave(control));

        (allocated - (observed - NotificationPrice())).Should().Be(0, "a caller's join nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnCallerLeft_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);

        var allocated = MeasureAfter(() => JoinCallers(manager), () =>
        {
            foreach (var channel in Channels)
                manager.OnCallerLeft(Queue, channel);
        });

        manager.GetByName(Queue)!.Entries.Should().BeEmpty("every report in the round removed its caller");
        allocated.Should().Be(0, "a caller's leave nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnCallerLeaveReported_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);

        var allocated = Measure(() =>
        {
            for (var i = 0; i < Members; i++)
                manager.OnCallerLeaveReported(UniqueIds[i], Queue, Channels[i], LossEpoch);
        });

        allocated.Should().Be(0, "a reported leave nobody is subscribed to queues nothing, not even its report");
    }

    // Each snapshot opens a window and copies what it listed into its completion, whether or not anyone hears it.
    [Fact]
    public void CompleteSnapshot_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = new QueueManager(NullLogger.Instance);
        var control = new QueueManager(NullLogger.Instance);
        control.QueueSnapshotCompleted += CountCompletion;

        var allocated = Measure(() => CompleteSnapshots(manager));
        var observed = MeasureObserved(() => CompleteSnapshots(control));

        (allocated - (observed - NotificationPrice())).Should().Be(0, "a completed snapshot nobody is subscribed to queues nothing");
    }

    private void CountMember(string queue, AsteriskQueueMember member) => _observed++;

    private void CountStatus(string uniqueId, string channel, string status) => _observed++;

    private void CountAbandon(string uniqueId, string queue) => _observed++;

    private void CountQueue(AsteriskQueue queue) => _observed++;

    private void CountCaller(string queue, AsteriskQueueEntry entry) => _observed++;

    private void CountCompletion(QueueSnapshotCompletion completion) => _observed++;

    // What one round of reports costs a manager when each one queues one notification to a subscribed handler. The
    // abandon path allocates nothing else (OnCallerAbandonReported_ShouldAllocateNothing_WhenNoHandlerIsSubscribed).
    private long NotificationPrice()
    {
        var manager = new QueueManager(NullLogger.Instance);
        manager.CallerAbandonReported += CountAbandon;
        return MeasureObserved(() =>
        {
            foreach (var uniqueId in UniqueIds)
                manager.OnCallerAbandonReported(uniqueId, Queue);
        });
    }

    private static void Params(QueueManager manager)
    {
        foreach (var name in QueueNames)
            manager.OnQueueParams(name, max: 0, "ringall", calls: 1, holdTime: 2, talkTime: 3, completed: 4, abandoned: 5);
    }

    private static void AddMembers(QueueManager manager)
    {
        foreach (var iface in Interfaces)
            manager.OnMemberAdded(Queue, iface, iface, penalty: 0, paused: false, (int)QueueMemberState.DeviceNotInUse);
    }

    private static void JoinCallers(QueueManager manager)
    {
        for (var i = 0; i < Members; i++)
            manager.OnCallerJoined(Queue, Channels[i], callerId: null, position: 1);
    }

    private static void JoinAndLeave(QueueManager manager)
    {
        foreach (var channel in Channels)
        {
            manager.OnCallerJoined(Queue, channel, callerId: null, position: 1);
            manager.OnCallerLeft(Queue, channel);
        }
    }

    private static void CompleteSnapshots(QueueManager manager)
    {
        for (var i = 0; i < Members; i++)
        {
            using var window = manager.OpenSnapshotWindow();
            manager.CompleteSnapshot(window);
        }
    }

    private static QueueManager WithQueues()
    {
        var manager = new QueueManager(NullLogger.Instance);
        Params(manager);
        return manager;
    }

    private static QueueManager WithMembers()
    {
        var manager = new QueueManager(NullLogger.Instance);
        foreach (var iface in Interfaces)
            manager.OnMemberAdded(Queue, iface, iface, penalty: 0, paused: false, (int)QueueMemberState.DeviceNotInUse);
        return manager;
    }

    // The reverse index still lists every device in the queue, but the queue holds none of them.
    private static QueueManager WithIndexedMembersGone()
    {
        var manager = WithMembers();
        var queue = manager.GetByName(Queue)!;
        foreach (var iface in Interfaces)
            queue.Members.TryRemove(iface, out _);
        manager.GetQueuesForMember(Interfaces[0]).Should().ContainSingle("the control walks the same index");
        return manager;
    }

    // Measures one round and checks it reached the handler once per report.
    private long MeasureObserved(Action round)
    {
        var observedBefore = 0;
        var allocated = Measure(() =>
        {
            observedBefore = _observed;
            round();
        });
        (_observed - observedBefore).Should().Be(Members, "every report reaches the subscribed handler");
        return allocated;
    }

    // As Measure, with the state each round consumes put back, unmeasured, before it.
    private static long MeasureAfter(Action setUp, Action round)
    {
        for (var i = 0; i < WarmUpRounds; i++)
        {
            setUp();
            round();
        }

        setUp();
        var before = GC.GetAllocatedBytesForCurrentThread();
        round();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Warm-up rounds first, so tiering stubs and one-time initialisation fall before the measured round.
    private static long Measure(Action round)
    {
        for (var i = 0; i < WarmUpRounds; i++)
            round();

        var before = GC.GetAllocatedBytesForCurrentThread();
        round();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
