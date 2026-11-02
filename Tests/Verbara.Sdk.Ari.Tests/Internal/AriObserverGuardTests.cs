using System.Reactive.Subjects;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Ari.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Ari.Tests.Internal;

/// <summary>
/// The guard on the arm a client cannot reach: nothing raises <c>OnError</c> on an ARI client's stream, and the guard
/// still has to keep what an observer throws from it away from the next observer.
/// </summary>
[Collection(AriEventsDroppedGroup.Name)]
public sealed class AriObserverGuardTests
{
    [Fact]
    public void OnError_ShouldReachTheNextObserverAndNameTheError_WhenAnObserverThrows()
    {
        var failure = new InvalidOperationException("observer failed");
        var logger = new CapturingLogger<AriClient>();
        using var stream = new Subject<AriEvent>();
        var thrower = Substitute.For<IObserver<AriEvent>>();
        thrower.When(o => o.OnError(Arg.Any<Exception>())).Do(_ => throw failure);
        var next = Substitute.For<IObserver<AriEvent>>();
        using var first = stream.Subscribe(new AriObserverGuard(thrower, logger));
        using var second = stream.Subscribe(new AriObserverGuard(next, logger));
        var raised = new InvalidOperationException("the stream failed");

        stream.OnError(raised);

        next.Received(1).OnError(raised);
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Exception.Should().BeSameAs(failure);
        warning.Value("EventType").Should().Be("error");
    }

    [Fact]
    public void OnNext_ShouldLetOutOfMemoryThrough_WhenAnObserverThrowsIt()
    {
        var thrower = Substitute.For<IObserver<AriEvent>>();
        thrower.When(o => o.OnNext(Arg.Any<AriEvent>())).Do(_ => throw OutOfMemory());
        var sut = new AriObserverGuard(thrower, new CapturingLogger<AriClient>());

        var deliver = () => sut.OnNext(new AriEvent { Type = "Probe" });

        deliver.Should().Throw<OutOfMemoryException>("the process cannot go on, so no guard hides it");
    }

    [Theory]
    [InlineData("error")]
    [InlineData("completion")]
    public void TerminalNotification_ShouldLetOutOfMemoryThrough_WhenAnObserverThrowsIt(string notification)
    {
        var thrower = Substitute.For<IObserver<AriEvent>>();
        thrower.When(o => o.OnError(Arg.Any<Exception>())).Do(_ => throw OutOfMemory());
        thrower.When(o => o.OnCompleted()).Do(_ => throw OutOfMemory());
        var sut = new AriObserverGuard(thrower, new CapturingLogger<AriClient>());

        Action deliver = notification == "error" ? () => sut.OnError(new InvalidOperationException()) : sut.OnCompleted;

        deliver.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void OnCompleted_ShouldReachTheNextObserver_WhenTheLoggerThrowsOnTheReport()
    {
        var logger = new FailingLogger();
        using var stream = new Subject<AriEvent>();
        var thrower = Substitute.For<IObserver<AriEvent>>();
        thrower.When(o => o.OnCompleted()).Do(_ => throw new InvalidOperationException("observer failed"));
        var next = Substitute.For<IObserver<AriEvent>>();
        using var first = stream.Subscribe(new AriObserverGuard(thrower, logger));
        using var second = stream.Subscribe(new AriObserverGuard(next, logger));

        var complete = stream.OnCompleted;

        complete.Should().NotThrow("what the report throws goes no further");
        next.Received(1).OnCompleted();
    }

    // The only exception the guard lets through is one only the runtime may throw; a test has to raise it itself.
#pragma warning disable CA1859, CA2201, MA0012
    private static Exception OutOfMemory() => new OutOfMemoryException();
#pragma warning restore CA1859, CA2201, MA0012

    private sealed class FailingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("the log provider failed");
    }
}
