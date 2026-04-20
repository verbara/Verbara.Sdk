using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests;

public sealed class CircuitBreakerStateTests
{
    [Fact]
    public void ShouldAllow_ShouldReturnTrue_WhenClosed()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();

        state.ShouldAllow(TimeSpan.FromSeconds(30), clock).Should().BeTrue();
        state.IsOpen.Should().BeFalse();
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void RecordFailure_ShouldOpenCircuit_WhenThresholdReached()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();
        const int threshold = 3;

        state.RecordFailure(threshold, clock);
        state.RecordFailure(threshold, clock);
        state.IsOpen.Should().BeFalse("circuit must remain closed below threshold");

        state.RecordFailure(threshold, clock);
        state.IsOpen.Should().BeTrue("circuit opens at exactly threshold");
        state.ConsecutiveFailures.Should().Be(3);
        state.OpenedAt.Should().NotBeNull();
    }

    [Fact]
    public void ShouldAllow_ShouldReturnFalse_WhenOpenAndWithinDuration()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();
        var openDuration = TimeSpan.FromSeconds(30);

        for (var i = 0; i < 3; i++)
            state.RecordFailure(3, clock);

        clock.Advance(TimeSpan.FromSeconds(5));

        state.ShouldAllow(openDuration, clock).Should().BeFalse();
        state.IsOpen.Should().BeTrue("ShouldAllow while within open window must not reset state");
    }

    [Fact]
    public void ShouldAllow_ShouldReturnTrue_WhenOpenDurationElapsed()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();
        var openDuration = TimeSpan.FromSeconds(30);

        for (var i = 0; i < 3; i++)
            state.RecordFailure(3, clock);
        state.IsOpen.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(31));

        state.ShouldAllow(openDuration, clock).Should().BeTrue("auto half-open probe after open duration elapsed");
        state.IsOpen.Should().BeFalse("state must reset to closed on half-open transition");
        state.ConsecutiveFailures.Should().Be(0);
        state.OpenedAt.Should().BeNull();
    }

    [Fact]
    public void RecordSuccess_ShouldResetFailuresAndCloseCircuit()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();

        for (var i = 0; i < 3; i++)
            state.RecordFailure(3, clock);
        state.IsOpen.Should().BeTrue();

        state.RecordSuccess();

        state.IsOpen.Should().BeFalse();
        state.ConsecutiveFailures.Should().Be(0);
        state.OpenedAt.Should().BeNull();
    }

    [Fact]
    public void RecordFailure_ShouldBeThreadSafe_UnderConcurrency()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();

        Parallel.For(0, 100, _ =>
        {
            for (var i = 0; i < 100; i++)
            {
                // Use a threshold larger than total increments so the circuit stays closed —
                // we're counting increments here, not state transitions.
                state.RecordFailure(int.MaxValue, clock);
            }
        });

        state.ConsecutiveFailures.Should().Be(10_000);
    }

    [Fact]
    public void OpenedAt_ShouldReflectInjectedTimeProvider()
    {
        var state = new CircuitBreakerState("node1");
        var start = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        for (var i = 0; i < 3; i++)
            state.RecordFailure(3, clock);

        state.OpenedAt.Should().Be(start, "OpenedAt must come from the injected TimeProvider, not DateTimeOffset.UtcNow");
    }

    [Fact]
    public void RecordFailure_ShouldNotReopen_WhenAlreadyOpen()
    {
        var state = new CircuitBreakerState("node1");
        var start = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        for (var i = 0; i < 3; i++)
            state.RecordFailure(3, clock);
        var firstOpenedAt = state.OpenedAt;

        clock.Advance(TimeSpan.FromSeconds(5));
        state.RecordFailure(3, clock);
        state.RecordFailure(3, clock);

        state.OpenedAt.Should().Be(firstOpenedAt,
            "subsequent failures while open must not overwrite the original OpenedAt timestamp");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenKeyIsNull()
    {
        var act = () => new CircuitBreakerState(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RecordFailure_ShouldThrow_WhenThresholdIsZero()
    {
        var state = new CircuitBreakerState("node1");
        var clock = new FakeTimeProvider();

        var act = () => state.RecordFailure(0, clock);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
