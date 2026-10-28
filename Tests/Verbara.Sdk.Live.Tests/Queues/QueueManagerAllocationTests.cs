using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// What the queue table's most frequent reports cost in heap bytes: a member's status (from <c>QueueMemberStatus</c>
/// or a <c>DeviceStateChange</c>), a <c>VarSet</c> of <c>QUEUESTATUS</c> and a <c>QueueCallerAbandon</c>. Bytes, not
/// time: the allocation counter is exact and per thread, and every object counted escapes into the outbox, so no JIT
/// tier removes it. None of these paths records a metric, so no listener can run on the measuring thread.
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

    private void CountMember(string queue, AsteriskQueueMember member) => _observed++;

    private void CountStatus(string uniqueId, string channel, string status) => _observed++;

    private void CountAbandon(string uniqueId, string queue) => _observed++;

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
