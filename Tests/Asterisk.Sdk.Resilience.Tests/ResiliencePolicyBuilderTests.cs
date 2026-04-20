using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests;

public sealed class ResiliencePolicyBuilderTests
{
    [Fact]
    public async Task Build_ShouldThrow_WhenNothingConfigured_ReturnsNoOpOrEmptyPolicy()
    {
        // Empty builder returns a policy that simply invokes the action — no throw, no retry, no circuit.
        var policy = new ResiliencePolicyBuilder().Build();
        policy.Should().NotBeNull();

        var result = await policy.ExecuteAsync(
            "k",
            _ => ValueTask.FromResult(5),
            CancellationToken.None);
        result.Should().Be(5);
    }

    [Fact]
    public async Task WithRetry_ShouldCapAtTenAttempts_WhenHigherValueGiven()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 100, baseDelay: TimeSpan.Zero)
            .WithTimeProvider(clock)
            .Build();

        var attempts = 0;
        var act = async () => await policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException();
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        attempts.Should().Be(ResiliencePolicyBuilder.MaxRetryAttemptsCap,
            "maxAttempts is clamped to MaxRetryAttemptsCap (10) for sanity");
    }

    [Fact]
    public void WithCircuitBreaker_ShouldRequirePositiveThreshold()
    {
        var builder = new ResiliencePolicyBuilder();

        var zeroThreshold = () => builder.WithCircuitBreaker(0, TimeSpan.FromSeconds(30));
        var negativeThreshold = () => builder.WithCircuitBreaker(-1, TimeSpan.FromSeconds(30));
        var zeroDuration = () => builder.WithCircuitBreaker(3, TimeSpan.Zero);
        var negativeDuration = () => builder.WithCircuitBreaker(3, TimeSpan.FromSeconds(-1));

        zeroThreshold.Should().Throw<ArgumentOutOfRangeException>();
        negativeThreshold.Should().Throw<ArgumentOutOfRangeException>();
        zeroDuration.Should().Throw<ArgumentOutOfRangeException>();
        negativeDuration.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WithTimeout_ShouldRequirePositiveDuration()
    {
        var builder = new ResiliencePolicyBuilder();

        var zero = () => builder.WithTimeout(TimeSpan.Zero);
        var negative = () => builder.WithTimeout(TimeSpan.FromMilliseconds(-1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WithTimeProvider_ShouldUseInjectedClockForDelays()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromSeconds(10))
            .WithTimeProvider(clock)
            .Build();

        var attempts = 0;
        var task = policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException();
            },
            CancellationToken.None).AsTask();

        // Give the first attempt a chance to run.
        for (var i = 0; i < 10 && attempts < 1; i++)
            await Task.Yield();

        attempts.Should().Be(1);
        task.IsCompleted.Should().BeFalse("Task.Delay is awaiting on the fake clock");

        // Advance the clock past the backoff window (10s baseDelay + 20% jitter headroom).
        clock.Advance(TimeSpan.FromSeconds(30));

        // Let the continuation run.
        for (var i = 0; i < 20 && !task.IsCompleted; i++)
            await Task.Yield();

        await FluentActions.Invoking(async () => await task).Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(2);
    }

    [Fact]
    public void WithRetry_ShouldRequirePositiveMaxAttempts()
    {
        var builder = new ResiliencePolicyBuilder();

        var zero = () => builder.WithRetry(0, TimeSpan.FromMilliseconds(1));
        var negative = () => builder.WithRetry(-1, TimeSpan.FromMilliseconds(1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WithTimeProvider_ShouldThrow_WhenNull()
    {
        var builder = new ResiliencePolicyBuilder();
        var act = () => builder.WithTimeProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
