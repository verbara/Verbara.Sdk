using System.Reactive.Subjects;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class QueueSessionTrackerTests : IDisposable
{
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
        EmitQueued("session-1", "sales");

        var queue = _sut.GetByQueueName("sales");
        queue.Should().NotBeNull();
        queue!.QueueName.Should().Be("sales");
    }

    [Fact]
    public void OnCallQueued_ShouldIncrementCallsOffered()
    {
        EmitQueued("session-1", "sales");
        EmitQueued("session-2", "sales");

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsOffered.Should().Be(2);
    }

    [Fact]
    public void OnCallQueued_ShouldIncrementCallsWaiting()
    {
        EmitQueued("session-1", "sales");
        EmitQueued("session-2", "sales");

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWaiting.Should().Be(2);
    }

    [Fact]
    public void OnCallConnected_ShouldIncrementCallsAnswered_WhenQueueNamePresent()
    {
        EmitQueued("session-1", "support");
        EmitConnected("session-1", "support", TimeSpan.FromSeconds(10));

        var queue = _sut.GetByQueueName("support");
        queue!.CallsAnswered.Should().Be(1);
    }

    [Fact]
    public void OnCallConnected_ShouldDecrementCallsWaiting()
    {
        EmitQueued("session-1", "support");
        EmitQueued("session-2", "support");
        EmitConnected("session-1", "support", TimeSpan.FromSeconds(5));

        var queue = _sut.GetByQueueName("support");
        queue!.CallsWaiting.Should().Be(1);
    }

    [Fact]
    public void OnCallConnected_ShouldRecordWaitTime()
    {
        EmitQueued("session-1", "support");
        EmitConnected("session-1", "support", TimeSpan.FromSeconds(15));

        EmitQueued("session-2", "support");
        EmitConnected("session-2", "support", TimeSpan.FromSeconds(25));

        var queue = _sut.GetByQueueName("support");
        queue!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(40));
        queue.MaxWaitTime.Should().Be(TimeSpan.FromSeconds(25));
        queue.MinWaitTime.Should().Be(TimeSpan.FromSeconds(15));
        queue.AvgWaitTime.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void OnCallConnected_ShouldIncrementCallsWithinSla_WhenWaitTimeBelowThreshold()
    {
        EmitQueued("session-1", "sales");
        EmitConnected("session-1", "sales", TimeSpan.FromSeconds(10)); // within 20s SLA

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(1);
    }

    [Fact]
    public void OnCallConnected_ShouldNotIncrementSla_WhenWaitTimeAboveThreshold()
    {
        EmitQueued("session-1", "sales");
        EmitConnected("session-1", "sales", TimeSpan.FromSeconds(30)); // exceeds 20s SLA

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(0);
    }

    [Fact]
    public void OnCallConnected_ShouldIncrementSla_WhenWaitTimeEqualsThreshold()
    {
        EmitQueued("session-1", "sales");
        EmitConnected("session-1", "sales", TimeSpan.FromSeconds(20)); // exactly at SLA

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWithinSla.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldIncrementCallsAbandoned_WhenCallerLeftWithoutAnswer()
    {
        EmitQueued("session-1", "sales");
        EmitEnded("session-1");

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsAbandoned.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldDecrementCallsWaiting_WhenAbandoned()
    {
        EmitQueued("session-1", "sales");
        EmitQueued("session-2", "sales");
        EmitEnded("session-1"); // abandoned

        var queue = _sut.GetByQueueName("sales");
        queue!.CallsWaiting.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldNotIncrementAbandoned_WhenCallWasAnswered()
    {
        EmitQueued("session-1", "sales");
        EmitConnected("session-1", "sales", TimeSpan.FromSeconds(5));
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

    [Fact]
    public void ActiveQueues_ShouldReturnAllTrackedQueues()
    {
        EmitQueued("session-1", "sales");
        EmitQueued("session-2", "support");
        EmitQueued("session-3", "billing");

        _sut.ActiveQueues.Should().HaveCount(3);
        _sut.ActiveQueues.Select(q => q.QueueName)
            .Should().BeEquivalentTo("sales", "support", "billing");
    }

    [Fact]
    public void WindowExpiry_ShouldResetCounters_WhenWindowExceeded()
    {
        EmitQueued("session-1", "sales");
        EmitConnected("session-1", "sales", TimeSpan.FromSeconds(10));

        var queue = _sut.GetByQueueName("sales")!;
        queue.CallsOffered.Should().Be(1);
        queue.CallsAnswered.Should().Be(1);

        // Force window expiry by backdating WindowStart
        queue.WindowStart = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31);

        // Next event should trigger reset
        EmitQueued("session-2", "sales");

        queue.CallsOffered.Should().Be(1, "counters reset then incremented by the new event");
        queue.CallsAnswered.Should().Be(0, "answered counter was reset");
        queue.CallsWaiting.Should().Be(1, "one caller currently waiting after reset");
    }

    // --- Helpers ---

    private void EmitQueued(string sessionId, string queueName, int? position = null)
    {
        _events.OnNext(new CallQueuedEvent(sessionId, "server-1",
            DateTimeOffset.UtcNow, queueName, position));
    }

    private void EmitConnected(string sessionId, string? queueName, TimeSpan waitTime)
    {
        _events.OnNext(new CallConnectedEvent(sessionId, "server-1",
            DateTimeOffset.UtcNow, "agent-1", queueName, waitTime));
    }

    private void EmitEnded(string sessionId)
    {
        _events.OnNext(new CallEndedEvent(sessionId, "server-1",
            DateTimeOffset.UtcNow, HangupCause.NormalClearing, TimeSpan.FromSeconds(30), null));
    }
}
