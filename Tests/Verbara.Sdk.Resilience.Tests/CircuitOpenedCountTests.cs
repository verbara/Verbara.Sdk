using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Verbara.Sdk.Resilience.Diagnostics;
using Xunit;
using static Verbara.Sdk.Resilience.Tests.CircuitGaugeReadsRealStateTests;

namespace Verbara.Sdk.Resilience.Tests;

/// <summary>
/// One trip is one <c>circuit.opened</c>. A barrier holds every caller inside the call, so all of them read the circuit
/// closed before the first one fails. No success runs and the clock stands still, so each round holds exactly one trip.
/// </summary>
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
    public async Task ExecuteAsync_ShouldAddOneToCircuitOpened_WhenASuccessLandsBetweenTheTripAndItsCount()
    {
        using var opened = new OpenedCounter();
        var clock = new HookedClock();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        clock.OnNextRead = () => SucceedAsync(policy, key).GetAwaiter().GetResult();

        await FailAsync(policy, key);

        opened.Read(key).Should().Be(1, "the failure won the change to open, whatever closed the circuit after it");
    }

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
