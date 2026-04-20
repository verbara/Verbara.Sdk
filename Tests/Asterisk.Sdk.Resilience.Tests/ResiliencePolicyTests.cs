using System.Diagnostics.Metrics;
using Asterisk.Sdk.Resilience.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests;

public sealed class ResiliencePolicyTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldReturnResult_WhenActionSucceeds()
    {
        var policy = new ResiliencePolicyBuilder().Build();

        var result = await policy.ExecuteAsync(
            "key1",
            _ => ValueTask.FromResult(42),
            CancellationToken.None);

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowCircuitBreakerOpenException_WhenCircuitOpen()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeProvider(clock)
            .Build();

        for (var i = 0; i < 2; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                "k",
                _ => throw new InvalidOperationException("boom"),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // Third call — circuit should now be open.
        var blocked = async () => await policy.ExecuteAsync(
            "k",
            _ => ValueTask.FromResult(1),
            CancellationToken.None);

        (await blocked.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be("k");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetry_WhenActionThrowsTransient()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.Zero)
            .WithTimeProvider(clock)
            .Build();

        var attempts = 0;
        var result = await policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("transient");
                return ValueTask.FromResult(99);
            },
            CancellationToken.None);

        result.Should().Be(99);
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldExhaustRetries_AndRethrowLastException()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.Zero)
            .WithTimeProvider(clock)
            .Build();

        var attempts = 0;
        var act = async () => await policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException($"fail-{attempts}");
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("fail-3");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldApplyExponentialBackoff_BetweenRetries()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 4, baseDelay: TimeSpan.FromSeconds(1))
            .WithTimeProvider(clock)
            .Build();

        var attemptTimes = new List<DateTimeOffset>();

        var task = policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attemptTimes.Add(clock.GetUtcNow());
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None).AsTask();

        // Drive the fake clock in small increments so each Task.Delay fires as soon as its
        // scheduled due time is reached (not several multiples later).
        for (var i = 0; i < 1000 && !task.IsCompleted; i++)
        {
            await Task.Yield();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        await FluentActions.Invoking(async () => await task).Should().ThrowAsync<InvalidOperationException>();

        attemptTimes.Should().HaveCount(4, "maxAttempts = 4 means 4 attempts before final throw");
        // Base delay = 1s → attempt2 ≈ 1s after attempt1, attempt3 ≈ 2s after attempt2 (±20% jitter).
        var gap1 = attemptTimes[1] - attemptTimes[0];
        var gap2 = attemptTimes[2] - attemptTimes[1];
        gap2.Should().BeGreaterThan(gap1, "exponential backoff must grow between successive retries");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTimeout_WhenActionExceedsConfiguredTimeout()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        var act = async () => await policy.ExecuteAsync<int>(
            "k",
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return 1;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotCatchOperationCanceledException()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.Zero)
            .Build();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempts = 0;
        var act = async () => await policy.ExecuteAsync<int>(
            "k",
            _ =>
            {
                attempts++;
                return ValueTask.FromResult(1);
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(0, "caller cancellation must short-circuit before the action runs");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementRetryCounter_OnEachAttempt()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.Zero)
            .WithTimeProvider(clock)
            .Build();

        var retryCount = 0L;
        var key = $"k-{Guid.NewGuid()}";
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceMetrics.MeterName &&
                instrument.Name == "retry.attempts")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && (tag.Value as string) == key)
                    Interlocked.Add(ref retryCount, value);
            }
        });
        listener.Start();

        var act = async () => await policy.ExecuteAsync<int>(
            key,
            _ => throw new InvalidOperationException(),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // With maxAttempts=3, there are 2 retries counted (attempts 1 and 2 each record a retry before the 3rd).
        retryCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementCircuitOpenedCounter_WhenTripping()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeProvider(clock)
            .Build();

        var opened = 0L;
        var key = $"k-{Guid.NewGuid()}";
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceMetrics.MeterName &&
                instrument.Name == "circuit.opened")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && (tag.Value as string) == key)
                    Interlocked.Add(ref opened, value);
            }
        });
        listener.Start();

        for (var i = 0; i < 2; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                key,
                _ => throw new InvalidOperationException(),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        opened.Should().Be(1, "circuit.opened must fire exactly once on the closed→open transition");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementTimeoutCounter_WhenTimingOut()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        var timeouts = 0L;
        var key = $"k-{Guid.NewGuid()}";
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceMetrics.MeterName &&
                instrument.Name == "timeout.fired")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && (tag.Value as string) == key)
                    Interlocked.Add(ref timeouts, value);
            }
        });
        listener.Start();
        var act = async () => await policy.ExecuteAsync<int>(
            key,
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return 1;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();

        timeouts.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIsolateCircuitsPerKey()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeProvider(clock)
            .Build();

        // Trip circuit for key "A".
        for (var i = 0; i < 2; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                "A",
                _ => throw new InvalidOperationException(),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // Key "B" must still succeed — circuits are isolated per key.
        var result = await policy.ExecuteAsync(
            "B",
            _ => ValueTask.FromResult(7),
            CancellationToken.None);
        result.Should().Be(7);

        // And key "A" should still be rejecting.
        var a = async () => await policy.ExecuteAsync(
            "A",
            _ => ValueTask.FromResult(0),
            CancellationToken.None);
        await a.Should().ThrowAsync<CircuitBreakerOpenException>();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCloseCircuit_AfterSuccessfulCallFollowingOpen()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeProvider(clock)
            .Build();

        var key = $"k-{Guid.NewGuid()}";
        for (var i = 0; i < 2; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                key,
                _ => throw new InvalidOperationException(),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // Advance past open duration so the next call is admitted as half-open probe.
        clock.Advance(TimeSpan.FromSeconds(31));

        var result = await policy.ExecuteAsync(
            key,
            _ => ValueTask.FromResult(1),
            CancellationToken.None);
        result.Should().Be(1);

        // Additional failure count must be reset — verify by tripping again from zero.
        for (var i = 0; i < 1; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                key,
                _ => throw new InvalidOperationException(),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        // After only 1 failure (threshold=2), circuit should still allow.
        var second = await policy.ExecuteAsync(
            key,
            _ => ValueTask.FromResult(42),
            CancellationToken.None);
        second.Should().Be(42);
    }

    [Fact]
    public async Task NoOp_ShouldInvokeActionDirectly_WithoutPolicies()
    {
        var invocations = 0;
        var result = await ResiliencePolicy.NoOp.ExecuteAsync<int>(
            "k",
            _ =>
            {
                invocations++;
                return ValueTask.FromResult(123);
            },
            CancellationToken.None);

        result.Should().Be(123);
        invocations.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRethrow_WhenExceptionIsNotRetriable()
    {
        // Policy retries ALL exceptions except OperationCanceledException (per spec).
        // This test asserts the contract: a non-OCE exception with retry disabled is simply rethrown.
        var policy = new ResiliencePolicyBuilder().Build();

        var act = async () => await policy.ExecuteAsync<int>(
            "k",
            _ => throw new InvalidOperationException("non-retriable"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("non-retriable");
    }
}
