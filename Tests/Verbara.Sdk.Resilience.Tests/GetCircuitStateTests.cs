using FluentAssertions;
using Verbara.Sdk.Resilience.Diagnostics;
using Xunit;
using static Verbara.Sdk.Resilience.Tests.CircuitGaugeReadsRealStateTests;

namespace Verbara.Sdk.Resilience.Tests;

public sealed class GetCircuitStateTests
{
    [Fact]
    public void GetCircuitState_ShouldReadClosed_WhenThePolicyHasNeverSeenTheKey()
    {
        var policy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();

        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Closed);

        ReadGauge(key).Should().BeNull();
    }

    [Fact]
    public void GetCircuitState_ShouldReadClosed_WhenThePolicyHasNoCircuitBreaker()
    {
        ResiliencePolicy.NoOp.GetCircuitState(NewKey()).Should().Be(ResilienceMetrics.CircuitStateValue.Closed);
    }

    [Fact]
    public async Task GetCircuitState_ShouldAgreeWithTheGauge_WhenAKeyTripsIdlesAndRecovers()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();

        await FailAsync(policy, key);
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);
        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.Open);

        clock.Advance(OpenWindow);
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.HalfOpen);
        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.HalfOpen);

        await SucceedAsync(policy, key);
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Closed);
        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.Closed);
    }

    [Fact]
    public async Task GetCircuitState_ShouldIsolateKeysAndPolicies_WhenOneKeyOfOnePolicyTripped()
    {
        var tripped = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var healthy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        var otherKey = NewKey();
        await SucceedAsync(healthy, key);
        await FailAsync(tripped, key);

        tripped.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);
        tripped.GetCircuitState(otherKey).Should().Be(ResilienceMetrics.CircuitStateValue.Closed);
        healthy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Closed);
    }

    [Fact]
    public void GetCircuitState_ShouldThrowArgumentNullException_WhenKeyIsNull()
    {
        var withBreaker = () => NewPolicy(new FakeTimeProvider(), threshold: 1).GetCircuitState(null!);
        var withoutBreaker = () => ResiliencePolicy.NoOp.GetCircuitState(null!);

        withBreaker.Should().Throw<ArgumentNullException>().WithParameterName("key");
        withoutBreaker.Should().Throw<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public async Task GetCircuitState_ShouldReadHalfOpenAndLeaveTheCallAdmitted_WhenReadPastItsOpenWindow()
    {
        var clock = new FakeTimeProvider();
        var policy = NewPolicy(clock, threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);
        clock.Advance(OpenWindow);

        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.HalfOpen);
        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.HalfOpen);
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.HalfOpen);
        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.HalfOpen);

        await SucceedAsync(policy, key);
    }

    [Fact]
    public async Task GetCircuitState_ShouldReadOpenAndLeaveTheCallRejected_WhenReadWithinItsOpenWindow()
    {
        var policy = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        await FailAsync(policy, key);

        policy.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);

        var next = async () => await policy.ExecuteAsync(key, _ => ValueTask.FromResult(1), CancellationToken.None);
        await next.Should().ThrowAsync<CircuitBreakerOpenException>();
    }

    [Fact]
    public async Task GetCircuitState_ShouldReadEachPolicysOwnState_WhenTwoPoliciesShareAKey()
    {
        var first = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var last = NewPolicy(new FakeTimeProvider(), threshold: 1);
        var key = NewKey();
        await FailAsync(first, key);

        await SucceedAsync(last, key);

        ReadGauge(key).Should().Be((int)ResilienceMetrics.CircuitStateValue.Closed);
        first.GetCircuitState(key).Should().Be(ResilienceMetrics.CircuitStateValue.Open);
    }
}
