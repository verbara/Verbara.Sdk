using System.Diagnostics.Metrics;
using Asterisk.Sdk.Resilience.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests;

public sealed class ResilienceMetricsTests
{
    [Fact]
    public void Meter_Name_ShouldBeAsteriskSdkResilience()
    {
        ResilienceMetrics.MeterName.Should().Be("Asterisk.Sdk.Resilience");
        ResilienceMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Resilience");
    }

    [Fact]
    public async Task RetryAttemptsCounter_ShouldEmit_WhenPolicyRetries()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.Zero)
            .WithTimeProvider(clock)
            .Build();

        var captured = new List<(long value, string key)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceMetrics.MeterName &&
                instrument.Name == "retry.attempts")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        var key = $"retry-{Guid.NewGuid()}";
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? keyTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "key")
                    keyTag = tag.Value?.ToString();
            }
            // Filter to this test's unique key so we don't pick up emissions from other tests
            // running in parallel that share the process-level static meter.
            if (keyTag == key)
            {
                lock (captured) captured.Add((value, keyTag));
            }
        });
        listener.Start();

        var act = async () => await policy.ExecuteAsync<int>(
            key,
            _ => throw new InvalidOperationException(),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        captured.Should().HaveCount(2, "with maxAttempts=3, 2 retries are counted before final throw");
        captured.Should().OnlyContain(x => x.key == key);
    }

    [Fact]
    public async Task CircuitStateGauge_ShouldReflectCurrentState()
    {
        var clock = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeProvider(clock)
            .Build();

        var key = $"gauge-{Guid.NewGuid()}";

        // Trip the circuit.
        for (var i = 0; i < 2; i++)
        {
            var act = async () => await policy.ExecuteAsync<int>(
                key,
                _ => throw new InvalidOperationException(),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // Collect the gauge values for our key.
        var observed = new Dictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceMetrics.MeterName &&
                instrument.Name == "circuit.state")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && tag.Value is string tagKey)
                {
                    lock (observed) observed[tagKey] = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().ContainKey(key);
        observed[key].Should().Be((int)ResilienceMetrics.CircuitStateValue.Open);

        // Advance past open duration and probe successfully → gauge should flip to Closed.
        clock.Advance(TimeSpan.FromSeconds(31));
        await policy.ExecuteAsync(
            key,
            _ => ValueTask.FromResult(1),
            CancellationToken.None);

        observed.Clear();
        listener.RecordObservableInstruments();

        observed.Should().ContainKey(key);
        observed[key].Should().Be((int)ResilienceMetrics.CircuitStateValue.Closed);
    }
}
