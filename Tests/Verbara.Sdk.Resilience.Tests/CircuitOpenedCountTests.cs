using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Resilience.Diagnostics;
using Xunit;
using static Verbara.Sdk.Resilience.Tests.CircuitGaugeReadsRealStateTests;

namespace Verbara.Sdk.Resilience.Tests;

/// <summary>
/// The tests that read a policy's clock through a hook never run alongside another test class: a trip tracks its key in the
/// process-wide gauge map, so a class observing the gauge reads that same clock, takes the hook, and leaves the trip's own
/// read with nothing to wait for.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CircuitOpenedCountGroup
{
    public const string Name = "Circuit opened count";
}

/// <summary>
/// One trip is one <c>circuit.opened</c>. A barrier holds every caller inside the call, so all of them read the circuit
/// closed before the first one fails. No success runs and the clock stands still, so each round holds exactly one trip.
/// </summary>
[Collection(CircuitOpenedCountGroup.Name)]
public sealed class CircuitOpenedCountTests
{
    private const int Rounds = 300;
    private static readonly TimeSpan BarrierLimit = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, 8)]
    [InlineData(5, 20)]
    [InlineData(3, 4)]
    public async Task ExecuteAsync_ShouldAddOneToCircuitOpened_WhenCallersFailTogetherAroundOneTrip(int threshold, int callers)
    {
        using var opened = new OpenedCounter();
        var miscounted = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var policy = NewPolicy(new FakeTimeProvider(), threshold);
            var key = NewKey();
            using var barrier = new Barrier(callers);

            await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(() => FailTogetherAsync(policy, key, barrier))));

            policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);
            if (opened.Read(key) != 1)
                miscounted++;
        }

        miscounted.Should().Be(0, "each of {0} rounds holds one trip, with threshold {1} and {2} callers", Rounds, threshold, callers);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAddOneForEachTrip_WhenASuccessClosedTheCircuitBetweenThem()
    {
        using var opened = new OpenedCounter();
        var policy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        var inFlight = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = policy.ExecuteAsync(key, async _ => await inFlight.Task, CancellationToken.None).AsTask();

        await FailAsync(policy, key);
        inFlight.SetResult(1);
        await success;
        await FailAsync(policy, key);

        opened.Read(key).Should().Be(2, "the success closed the first trip, so the second failure is a second trip");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAddOneToCircuitOpened_WhenASuccessClosesTheCircuitBetweenAStaleReadAndTheTrip()
    {
        using var opened = new OpenedCounter();
        var clock = new HookedClock();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        var releaseSuccess = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = policy.ExecuteAsync(key, async _ => await releaseSuccess.Task.ConfigureAwait(false), CancellationToken.None).AsTask();
        var failure = policy.ExecuteAsync(key, async _ => await releaseFailure.Task.ConfigureAwait(false), CancellationToken.None).AsTask();

        await FailAsync(policy, key);
        opened.Read(key).Should().Be(1, "the first failure tripped the circuit while both calls were in flight");

        clock.OnNextRead = () =>
        {
            releaseSuccess.SetResult(1);
            success.WaitAsync(BarrierLimit).GetAwaiter().GetResult();
        };
        releaseFailure.SetException(new TimeoutException("dependency down"));
        var act = async () => await failure;
        await act.Should().ThrowAsync<TimeoutException>();

        opened.Read(key).Should().Be(2, "the failure read the circuit open, and a success closed it before this real second trip");
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);
    }

    [Fact]
    public void ExecuteAsync_ShouldCountATrip_WhenASuccessClosesTheCircuitRightAfterIt()
    {
        using var opened = new OpenedCounter();
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        var down = new TimeoutException("dependency down");
        var offset = 0;
        long counted = 0;
        long uncounted = 0;
        long failed = 0;
        long admitted = 0;
        long rejected = 0;

        // Each round starts closed with no failures, so the failing call always wins the one trip of the round. The success
        // starts a little later each round, sweeping across the failure's path until it lands right after the trip.
        var rounds = CircuitBreakerRaceTests.Lockstep(
            () =>
            {
                try
                {
                    policy.ExecuteAsync<int>(key, _ => throw down, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    failed++;
                }
            },
            () =>
            {
                for (var i = offset++ % SuccessOffsets; i > 0; i--)
                    Thread.SpinWait(1); // fence-allow: SIMULATED-WORK — staggers the success across the failure's path, not a wait
                try
                {
                    policy.ExecuteAsync(key, static _ => ValueTask.FromResult(1), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    admitted++;
                }
                catch (CircuitBreakerOpenException)
                {
                    rejected++;
                }
            },
            () =>
            {
                counted++;
                var read = opened.Read(key);
                if (read != counted)
                {
                    uncounted += counted - read;
                    counted = read;
                }

                if (policy.GetCircuitState(key) != ResilienceMetrics.CircuitStateValue.Closed)
                {
                    clock.Advance(CircuitGaugeReadsRealStateTests.OpenWindow);
                    policy.ExecuteAsync(key, static _ => ValueTask.FromResult(1), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
            });

        rounds.Should().BeGreaterThan(0);
        using (new AssertionScope())
        {
            failed.Should().Be(rounds, "the failing call is admitted every round, since each round starts closed");
            (admitted + rejected).Should().Be(rounds);
            uncounted.Should().Be(0, "each of {0} rounds holds one trip, and a success after it does not erase its count", rounds);
        }
    }

    private const int SuccessOffsets = 256;

    private static async Task FailTogetherAsync(ResiliencePolicy policy, string key, Barrier barrier)
    {
        var act = async () => await policy.ExecuteAsync<int>(
            key,
            ct =>
            {
                barrier.SignalAndWait(BarrierLimit, ct).Should().BeTrue("every caller must be inside the call before any fails");
                throw new TimeoutException("dependency down");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private sealed class HookedClock : TimeProvider
    {
        public Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var hook = OnNextRead;
            OnNextRead = null;
            hook?.Invoke();
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class OpenedCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new();

        public OpenedCounter()
        {
            // Read first: the callback also runs inside ResilienceMetrics' static initializer, where the field is still null.
            var circuitOpened = ResilienceMetrics.CircuitOpened;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, circuitOpened))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "key")
                        _counts.AddOrUpdate((string)tag.Value!, value, (_, sum) => sum + value);
                }
            });
            _listener.Start();
        }

        public long Read(string key) => _counts.TryGetValue(key, out var count) ? count : 0;

        public void Dispose() => _listener.Dispose();
    }
}
