using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests;

public sealed class BackoffScheduleTests
{
    [Fact]
    public void Compute_ShouldReturnBaseDelay_WhenAttemptIsOne()
    {
        var result = BackoffSchedule.Compute(
            attempt: 1,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(10));

        result.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Compute_ShouldApplyExponentialBackoff_WhenAttemptGrows()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var max = TimeSpan.FromSeconds(10);

        BackoffSchedule.Compute(2, baseDelay, 2.0, max).Should().Be(TimeSpan.FromMilliseconds(200));
        BackoffSchedule.Compute(3, baseDelay, 2.0, max).Should().Be(TimeSpan.FromMilliseconds(400));
        BackoffSchedule.Compute(4, baseDelay, 2.0, max).Should().Be(TimeSpan.FromMilliseconds(800));
        BackoffSchedule.Compute(5, baseDelay, 2.0, max).Should().Be(TimeSpan.FromMilliseconds(1600));
    }

    [Fact]
    public void Compute_ShouldCapAtMaxDelay_WhenExponentialExceedsCap()
    {
        var result = BackoffSchedule.Compute(
            attempt: 10,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(5));

        // 100 * 2^9 = 51200 ms = 51.2s > cap; returns cap.
        result.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Compute_ShouldSupportFractionalMultiplier_WhenMultiplierIsOnePointFive()
    {
        var result = BackoffSchedule.Compute(
            attempt: 3,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 1.5,
            maxDelay: TimeSpan.FromSeconds(10));

        // 100 * 1.5^2 = 225ms
        result.Should().BeCloseTo(TimeSpan.FromMilliseconds(225), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Compute_ShouldThrow_WhenAttemptIsLessThanOne()
    {
        var act = () => BackoffSchedule.Compute(0, TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_ShouldThrow_WhenMultiplierIsLessThanOne()
    {
        var act = () => BackoffSchedule.Compute(1, TimeSpan.FromMilliseconds(100), 0.5, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_ShouldThrow_WhenMaxDelayLessThanBaseDelay()
    {
        var act = () => BackoffSchedule.Compute(1, TimeSpan.FromSeconds(10), 2.0, TimeSpan.FromSeconds(5));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_ShouldNotOverflow_WhenAttemptIsVeryLarge()
    {
        var result = BackoffSchedule.Compute(
            attempt: 1_000,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromMinutes(5));

        result.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ComputeWithJitter_ShouldReturnValueNearBase_WhenJitterFractionIsSmall()
    {
        var jitter = new Random(42);
        var baseDelay = TimeSpan.FromMilliseconds(1000);
        var result = BackoffSchedule.ComputeWithJitter(
            attempt: 1,
            baseDelay: baseDelay,
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(10),
            jitterFraction: 0.2,
            jitter: jitter);

        // Base is 1000ms. Jitter ±10% → [900, 1100] range approx.
        result.TotalMilliseconds.Should().BeInRange(900, 1100);
    }

    [Fact]
    public void ComputeWithJitter_ShouldReturnExactBase_WhenJitterFractionIsZero()
    {
        var jitter = new Random(42);
        var result = BackoffSchedule.ComputeWithJitter(
            attempt: 2,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(10),
            jitterFraction: 0.0,
            jitter: jitter);

        result.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void ComputeWithJitter_ShouldThrow_WhenJitterSourceIsNull()
    {
        var act = () => BackoffSchedule.ComputeWithJitter(
            attempt: 1,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(10),
            jitterFraction: 0.2,
            jitter: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ComputeWithJitter_ShouldCapAtMaxDelay_WhenJitterExceedsCap()
    {
        // Use a jitter source that returns 1.0 → max positive jitter
        var jitter = new Random();
        var result = BackoffSchedule.ComputeWithJitter(
            attempt: 10,
            baseDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2.0,
            maxDelay: TimeSpan.FromSeconds(5),
            jitterFraction: 0.5,
            jitter: jitter);

        // Base at attempt 10 already clamped at 5s; jitter can't push above max.
        result.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }
}
