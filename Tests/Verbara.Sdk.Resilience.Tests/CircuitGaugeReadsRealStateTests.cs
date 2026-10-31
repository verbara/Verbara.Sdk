using System.Diagnostics.Metrics;
using Verbara.Sdk.Resilience.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Resilience.Tests;

/// <summary>
/// What a reader of the <c>circuit.state</c> gauge is promised: the state the breaker is in when
/// it is observed, not the last state it announced. The gauge is process-wide, so every test uses
/// its own <see cref="Guid"/> key; the clock is per policy, so tests run in parallel on it.
/// </summary>
public sealed class CircuitGaugeReadsRealStateTests
{
    internal static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);

    private const int Closed = (int)ResilienceMetrics.CircuitStateValue.Closed;
    private const int HalfOpen = (int)ResilienceMetrics.CircuitStateValue.HalfOpen;
    private const int Open = (int)ResilienceMetrics.CircuitStateValue.Open;

    [Fact]
    public async Task CircuitStateGauge_ShouldReadHalfOpen_WhenTrippedCircuitIdlesPastItsOpenWindow()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);

        clock.Advance(TimeSpan.FromMinutes(6));

        ReadGauge(key).Should().Be(HalfOpen);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReadHalfOpen_WhenExactlyItsOpenWindowHasElapsed()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);

        clock.Advance(OpenWindow);

        ReadGauge(key).Should().Be(HalfOpen);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReadOpen_WhenOneTickRemainsOfItsOpenWindow()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);

        clock.Advance(OpenWindow - TimeSpan.FromTicks(1));

        ReadGauge(key).Should().Be(Open);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReadClosed_WhenAProbeFailedAndNothingElseRan()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 3);
        var key = NewKey();
        await TripAsync(policy, key, threshold: 3);
        clock.Advance(OpenWindow);

        await FailAsync(policy, key);

        ReadGauge(key).Should().Be(Closed);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReadClosed_WhenAProbeSucceeded()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);
        clock.Advance(OpenWindow);

        await SucceedAsync(policy, key);

        ReadGauge(key).Should().Be(Closed);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReadOpenThenHalfOpen_WhenAFailedProbeTrippedThresholdOneAgain()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);
        clock.Advance(OpenWindow);

        await FailAsync(policy, key);

        ReadGauge(key).Should().Be(Open);
        clock.Advance(OpenWindow - TimeSpan.FromTicks(1));
        ReadGauge(key).Should().Be(Open);
        clock.Advance(TimeSpan.FromTicks(1));
        ReadGauge(key).Should().Be(HalfOpen);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReportNothing_WhenAKeyHasOnlyFailedBelowItsThreshold()
    {
        var policy = NewPolicy(new FakeTimeProvider(), threshold: 3);
        var key = NewKey();

        await FailAsync(policy, key);

        ReadGauge(key).Should().BeNull();
    }

    [Fact]
    public void CircuitStateGauge_ShouldReadTheWrittenValue_WhenOnlySetCircuitStateWroteTheKey()
    {
        var key = NewKey();

        ResilienceMetrics.SetCircuitState(key, ResilienceMetrics.CircuitStateValue.Open);

        ReadGauge(key).Should().Be(Open);
        ReadGauge(key).Should().Be(Open);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldFollowThePolicy_WhenAPolicyRunsAWrittenKey()
    {
        var policy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        ResilienceMetrics.SetCircuitState(key, ResilienceMetrics.CircuitStateValue.Open);

        await SucceedAsync(policy, key);

        ReadGauge(key).Should().Be(Closed);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldFollowTheRejectingPolicy_WhenAnotherPolicyReportedTheKeyLast()
    {
        var tripped = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var healthy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        await FailAsync(tripped, key);
        await SucceedAsync(healthy, key);
        ReadGauge(key).Should().Be(Closed);

        var act = async () => await tripped.ExecuteAsync(key, _ => ValueTask.FromResult(1), CancellationToken.None);

        await act.Should().ThrowAsync<CircuitBreakerOpenException>();
        ReadGauge(key).Should().Be(Open);
    }

    internal static string NewKey() => $"gauge-{Guid.NewGuid()}";

    internal static ResiliencePolicy NewPolicy(TimeProvider clock, int threshold) =>
        new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold, OpenWindow)
            .WithTimeProvider(clock)
            .Build();

    internal static async Task FailAsync(ResiliencePolicy policy, string key)
    {
        var act = async () => await policy.ExecuteAsync<int>(
            key,
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    internal static async Task SucceedAsync(ResiliencePolicy policy, string key)
    {
        var result = await policy.ExecuteAsync(key, _ => ValueTask.FromResult(1), CancellationToken.None);

        result.Should().Be(1);
    }

    internal static async Task TripAsync(ResiliencePolicy policy, string key, int threshold)
    {
        for (var i = 0; i < threshold; i++)
        {
            await FailAsync(policy, key);
        }
    }

    /// <summary>
    /// One observation of the gauge for <paramref name="key"/>, or <see langword="null"/> when it
    /// reports nothing for that key. Reading the static gauge first is what publishes the
    /// instrument, so a test that has not reached <see cref="ResilienceMetrics"/> yet still
    /// measures it.
    /// </summary>
    internal static int? ReadGauge(string key)
    {
        var gauge = ResilienceMetrics.CircuitStateGauge;
        int? observed = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, gauge))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && (string?)tag.Value == key)
                {
                    observed = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }
}
