using System.Reactive.Subjects;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class QueueSessionTrackerTests : IDisposable
{
    /// <summary>
    /// The instant every event's explicit timestamp is laid out from. Its value is arbitrary: the
    /// tracker measures a visit's wait between the timestamps its events carry, and no test reads the
    /// wall clock to do it.
    /// </summary>
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly QueueSessionTracker _sut;
    private readonly SessionOptions _options;

    public QueueSessionTrackerTests()
    {
        var manager = Substitute.For<ICallSessionManager>();
        manager.Events.Returns(_events);

        _options = new SessionOptions
        {
            QueueMetricsWindow = TimeSpan.FromMinutes(30),
            SlaThreshold = TimeSpan.FromSeconds(20)
        };

        _sut = new QueueSessionTracker(manager, Options.Create(_options));
    }

    public void Dispose()
    {
        _sut.Dispose();
        _events.Dispose();
    }

    [Fact]
    public void GetByQueueName_ShouldReturnNull_WhenQueueNotTracked()
    {
        _sut.GetByQueueName("nonexistent").Should().BeNull();
    }

    [Fact]
    public void OnCallQueued_ShouldCreateQueueSession_WhenNewQueue()
    {
        EmitQueued("session-1", "sales", T0);

        var queue = _sut.GetByQueueName("sales");
        queue.Should().NotBeNull();
        queue!.QueueName.Should().Be("sales");
    }

    [Fact]
    public void OnCallQueued_ShouldIncrementCallsOffered()
    {
        EmitQueued("session-1", "sales", T0);
        EmitQueued("session-2", "sales", T0.AddSeconds(1));

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsOffered.Should().Be(2);
    }

    [Fact]
    public void OnCallQueued_ShouldIncrementCallsWaiting()
    {
        EmitQueued("session-1", "sales", T0);
        EmitQueued("session-2", "sales", T0.AddSeconds(1));

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWaiting.Should().Be(2);
    }

    [Fact]
    public void OnCallConnected_ShouldIncrementCallsAnswered_WhenQueueNamePresent()
    {
        EmitQueued("session-1", "support", T0);
        EmitConnected("session-1", "support", T0.AddSeconds(10), waitSinceCreated: TimeSpan.FromSeconds(10));

        var queue = _sut.GetByQueueName("support");
        queue!.CallsAnswered.Should().Be(1);
    }

    [Fact]
    public void OnCallConnected_ShouldDecrementCallsWaiting()
    {
        EmitQueued("session-1", "support", T0);
        EmitQueued("session-2", "support", T0.AddSeconds(1));
        EmitConnected("session-1", "support", T0.AddSeconds(5), waitSinceCreated: TimeSpan.FromSeconds(5));

        var queue = _sut.GetByQueueName("support");
        queue!.CallsWaiting.Should().Be(1);
    }

    /// <summary>
    /// Two visits of 15 s and 25 s. Each call spent time before it joined the queue, so the wait since
    /// it was created, which the event carries, is deliberately larger than its visit: the queue's
    /// figures are the visits'.
    /// </summary>
    [Fact]
    public void OnCallConnected_ShouldRecordWaitTime()
    {
        EmitQueued("session-1", "support", T0);
        EmitConnected("session-1", "support", T0.AddSeconds(15), waitSinceCreated: TimeSpan.FromSeconds(21));

        EmitQueued("session-2", "support", T0.AddSeconds(30));
        EmitConnected("session-2", "support", T0.AddSeconds(55), waitSinceCreated: TimeSpan.FromSeconds(33));

        var queue = _sut.GetByQueueName("support");
        queue!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(40), "the visits are 15 s and 25 s");
        queue.MaxWaitTime.Should().Be(TimeSpan.FromSeconds(25));
        queue.MinWaitTime.Should().Be(TimeSpan.FromSeconds(15));
        queue.AvgWaitTime.Should().Be(TimeSpan.FromSeconds(20));
    }

    /// <summary>A visit of 10 s, within the 20 s threshold, for a call created 25 s before it was connected.</summary>
    [Fact]
    public void OnCallConnected_ShouldIncrementCallsWithinSla_WhenWaitTimeBelowThreshold()
    {
        EmitQueued("session-1", "sales", T0);
        EmitConnected("session-1", "sales", T0.AddSeconds(10), waitSinceCreated: TimeSpan.FromSeconds(25));

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(1);
    }

    /// <summary>
    /// A visit of 30 s, over the 20 s threshold. The event's since-created wait is set to 10 s, under
    /// the threshold, so the verdict shows which of the two the tracker judged.
    /// </summary>
    [Fact]
    public void OnCallConnected_ShouldNotIncrementSla_WhenWaitTimeAboveThreshold()
    {
        EmitQueued("session-1", "sales", T0);
        EmitConnected("session-1", "sales", T0.AddSeconds(30), waitSinceCreated: TimeSpan.FromSeconds(10));

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(0);
    }

    /// <summary>A visit of exactly 20 s, the threshold, for a call created 30 s before it was connected.</summary>
    [Fact]
    public void OnCallConnected_ShouldIncrementSla_WhenWaitTimeEqualsThreshold()
    {
        EmitQueued("session-1", "sales", T0);
        EmitConnected("session-1", "sales", T0.AddSeconds(20), waitSinceCreated: TimeSpan.FromSeconds(30));

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldIncrementCallsAbandoned_WhenCallerLeftWithoutAnswer()
    {
        EmitQueued("session-1", "sales", T0);
        EmitEnded("session-1");

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsAbandoned.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldDecrementCallsWaiting_WhenAbandoned()
    {
        EmitQueued("session-1", "sales", T0);
        EmitQueued("session-2", "sales", T0.AddSeconds(1));
        EmitEnded("session-1"); // abandoned

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWaiting.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldNotIncrementAbandoned_WhenCallWasAnswered()
    {
        EmitQueued("session-1", "sales", T0);
        EmitConnected("session-1", "sales", T0.AddSeconds(5), waitSinceCreated: TimeSpan.FromSeconds(5));
        EmitEnded("session-1"); // normal end after answer

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsAbandoned.Should().Be(0);
    }

    [Fact]
    public void OnCallConnected_ShouldIgnore_WhenQueueNameIsNull()
    {
        // Direct call without queue — should not create any queue session
        _events.OnNext(new CallConnectedEvent("session-1", "server-1",
            DateTimeOffset.UtcNow, "agent-1", null, TimeSpan.FromSeconds(5)));

        _sut.ActiveQueues.Should().BeEmpty();
    }

    /// <summary>
    /// A call that spent 5.5 s since it was created, 2 s of them in the queue: the queue records the
    /// 2 s visit, not the IVR before it.
    /// </summary>
    [Fact]
    public void OnCallConnected_ShouldRecordTheVisitsWait_WhenAnIvrRanBeforeTheQueue()
    {
        EmitQueued("session-1", "support", T0);
        EmitConnected("session-1", "support", T0.AddSeconds(2), waitSinceCreated: TimeSpan.FromSeconds(5.5));

        var queue = _sut.GetByQueueName("support")!;
        queue.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the only visit is 2 s long");
        queue.MaxWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the only visit is 2 s long");
        queue.MinWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the only visit is 2 s long");
    }

    /// <summary>
    /// A call waits 6 s in a first queue, is timed out of it and joins a second queue, which connects
    /// it 2 s later: the second queue's wait runs from its own join, not from the first queue's.
    /// </summary>
    [Fact]
    public void OnCallConnected_ShouldRecordTheWaitFromTheSecondQueuesJoin_WhenTheCallOverflowedIntoIt()
    {
        EmitQueued("session-1", "first", T0);
        EmitQueued("session-1", "second", T0.AddSeconds(6));
        EmitConnected("session-1", "second", T0.AddSeconds(8), waitSinceCreated: TimeSpan.FromSeconds(8));

        var second = _sut.GetByQueueName("second")!;
        second.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the second queue connected the call 2 s after it joined");
        second.MaxWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the second queue connected the call 2 s after it joined");
        second.MinWaitTime.Should().Be(TimeSpan.FromSeconds(2), "the second queue connected the call 2 s after it joined");
    }

    [Fact]
    public void ActiveQueues_ShouldReturnAllTrackedQueues()
    {
        EmitQueued("session-1", "sales", T0);
        EmitQueued("session-2", "support", T0.AddSeconds(1));
        EmitQueued("session-3", "billing", T0.AddSeconds(2));

        _sut.ActiveQueues.Should().HaveCount(3);
        _sut.ActiveQueues.Select(q => q.QueueName)
            .Should().BeEquivalentTo("sales", "support", "billing");
    }

    [Fact]
    public void WindowExpiry_ShouldResetCounters_WhenWindowExceeded()
    {
        EmitQueued("session-1", "sales", T0);
        EmitConnected("session-1", "sales", T0.AddSeconds(10), waitSinceCreated: TimeSpan.FromSeconds(10));

        var queue = _sut.GetByQueueName("sales")!;
        queue.CallsOffered.Should().Be(1);
        queue.CallsAnswered.Should().Be(1);

        // Force window expiry by backdating WindowStart
        queue.WindowStart = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31);

        // Next event should trigger reset
        EmitQueued("session-2", "sales", T0.AddSeconds(20));

        queue.CallsOffered.Should().Be(1, "counters reset then incremented by the new event");
        queue.CallsAnswered.Should().Be(0, "answered counter was reset");
        queue.CallsWaiting.Should().Be(1, "one caller currently waiting after reset");
    }

    // --- Helpers ---

    /// <summary>The call joins <paramref name="queueName"/> at <paramref name="at"/>, the event's timestamp.</summary>
    private void EmitQueued(string sessionId, string queueName, DateTimeOffset at)
    {
        _events.OnNext(new CallQueuedEvent(sessionId, "server-1", at, queueName, null));
    }

    /// <summary>
    /// <paramref name="queueName"/> connects the call at <paramref name="at"/>, the event's timestamp.
    /// <paramref name="waitSinceCreated"/> is the event's own <see cref="CallConnectedEvent.WaitTime"/>:
    /// the wait since the call was created, which is not the queue visit's.
    /// </summary>
    private void EmitConnected(string sessionId, string? queueName, DateTimeOffset at, TimeSpan waitSinceCreated)
    {
        _events.OnNext(new CallConnectedEvent(sessionId, "server-1", at, "agent-1", queueName, waitSinceCreated));
    }

    private void EmitEnded(string sessionId)
    {
        _events.OnNext(new CallEndedEvent(sessionId, "server-1",
            DateTimeOffset.UtcNow, HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));
    }
}
