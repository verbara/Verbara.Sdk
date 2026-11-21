using Verbara.Sdk.Ari.Outbound;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ari.Tests.Outbound;

/// <summary>
/// The guard the outbound listener and connection subscribe in an observer's place, on the one path the listener never
/// raises today: an error at the end of the stream. The other paths are pinned through the real listener in
/// <see cref="AriOutboundSubscriberIsolationTests"/>.
/// </summary>
public sealed class OutboundObserverGuardTests
{
    [Fact]
    public void OnError_ShouldReportTheFailureAsAnErrorAndNotRethrow_WhenTheObserverThrowsOnTheError()
    {
        // Arrange
        var reports = new List<(Exception Failure, string Notification, AriEvent? Value)>();
        var failure = new InvalidOperationException("the observer failed on the error");
        var guard = new OutboundObserverGuard<AriEvent>(
            new ThrowingOnErrorObserver(failure),
            (ex, notification, value) => reports.Add((ex, notification, value)));

        // Act
        var thrown = Record.Exception(() => guard.OnError(new TimeoutException("the stream failed")));

        // Assert
        using (new AssertionScope())
        {
            thrown.Should().BeNull("nothing an observer throws reaches the publisher");
            reports.Should().ContainSingle();
            reports[0].Failure.Should().BeSameAs(failure, "the report carries the observer's own exception");
            reports[0].Notification.Should().Be("error");
            reports[0].Value.Should().BeNull("an error carries no value");
        }
    }

    [Fact]
    public void OnError_ShouldForwardTheErrorAndReportNothing_WhenTheObserverDoesNotThrow()
    {
        // Arrange
        Exception? received = null;
        var reports = 0;
        var guard = new OutboundObserverGuard<AriEvent>(
            new RecordingObserver(ex => received = ex),
            (_, _, _) => reports++);
        var error = new TimeoutException("the stream failed");

        // Act
        guard.OnError(error);

        // Assert
        using (new AssertionScope())
        {
            received.Should().BeSameAs(error);
            reports.Should().Be(0);
        }
    }

    [Fact]
    public void Subscribe_ShouldThrowArgumentNullException_WhenTheObserverIsNull()
    {
        // Arrange
        using var subject = new System.Reactive.Subjects.Subject<AriEvent>();
        var stream = new OutboundGuardedStream<AriEvent>(subject, static (_, _, _) => { });

        // Act
        var act = () => stream.Subscribe(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class ThrowingOnErrorObserver(Exception failure) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) { }

        public void OnError(Exception error) => throw failure;

        public void OnCompleted() { }
    }

    private sealed class RecordingObserver(Action<Exception> onError) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) { }

        public void OnError(Exception error) => onError(error);

        public void OnCompleted() { }
    }
}
