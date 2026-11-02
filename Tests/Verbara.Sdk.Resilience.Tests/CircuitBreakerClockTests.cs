using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Resilience.Tests;

/// <summary>
/// A clock that throws while a failure is reported leaves the circuit as it was, and that failure stays counted.
/// </summary>
public sealed class CircuitBreakerClockTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);

    [Fact]
    public void RecordFailure_ShouldLeaveTheCircuitClosed_WhenTheClockThrows()
    {
        var state = new CircuitBreakerState("clock");
        var clock = new ThrowOnceClock { ThrowOnNextRead = true };

        var act = () => state.RecordFailure(1, clock);

        act.Should().Throw<ClockFailedException>();
        state.IsOpen.Should().BeFalse("a clock that throws changes nothing");
        state.OpenedAt.Should().BeNull();
        state.ConsecutiveFailures.Should().Be(1, "the failure stays counted");

        state.RecordFailure(1, clock);

        state.IsOpen.Should().BeTrue();
        state.OpenedAt.Should().Be(ThrowOnceClock.Now);
        state.ShouldAllow(OpenWindow, clock).Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_ShouldLeaveAnOpenCircuitAsItWas_WhenTheClockThrows()
    {
        var state = new CircuitBreakerState("clock");
        var clock = new ThrowOnceClock();
        state.RecordFailure(1, clock);
        var openedAt = state.OpenedAt;
        openedAt.Should().NotBeNull();
        clock.ThrowOnNextRead = true;

        var act = () => state.RecordFailure(1, clock);

        act.Should().Throw<ClockFailedException>();
        state.IsOpen.Should().BeTrue();
        state.OpenedAt.Should().Be(openedAt);
        state.ConsecutiveFailures.Should().Be(2, "the failure stays counted");
        state.ShouldAllow(OpenWindow, clock).Should().BeFalse();
    }

    [Fact]
    public void RecordFailure_ShouldOpenAtTheTimeItReadBeforeTheTrip_WhenTheClockWouldThrowOnALaterRead()
    {
        var state = new CircuitBreakerState("clock");
        var clock = new ThrowOnceClock();
        clock.ThrowOnReadAfterNext = true;

        state.RecordFailure(1, clock);

        state.IsOpen.Should().BeTrue();
        state.OpenedAt.Should().Be(ThrowOnceClock.Now, "a trip reads the clock once, before it opens the circuit");
        clock.ThrowOnNextRead.Should().BeTrue("the trip read the clock once, so the read that throws has not happened");
    }

    private sealed class ClockFailedException : Exception;

    private sealed class ThrowOnceClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public bool ThrowOnNextRead { get; set; }

        // Arms ThrowOnNextRead on the next read, so the read after that one throws.
        public bool ThrowOnReadAfterNext { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (ThrowOnReadAfterNext && !ThrowOnNextRead)
            {
                ThrowOnReadAfterNext = false;
                ThrowOnNextRead = true;
                return Now;
            }

            if (!ThrowOnNextRead)
                return Now;

            ThrowOnNextRead = false;
            throw new ClockFailedException();
        }
    }
}
