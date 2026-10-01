using System.Globalization;
using Verbara.Sdk.Live.Bridges;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.Bridges;

/// <summary>
/// A destroyed bridge stays reachable through <see cref="BridgeManager.GetById"/> for ten minutes and is released on
/// the first bridge create or destroy after that. Driven on a manual clock: no sleeps, no timer.
/// </summary>
public sealed class BridgeRetentionTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private readonly ManualTime _clock = new(DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture));
    private readonly BridgeManager _sut;

    public BridgeRetentionTests()
    {
        _sut = new BridgeManager(NullLogger.Instance) { TimeProvider = _clock };
    }

    public enum LaterEvent
    {
        Create,
        Destroy,
    }

    [Fact]
    public void DestroyedRetention_ShouldBeTenMinutes_WhenNotSet()
    {
        new BridgeManager(NullLogger.Instance).DestroyedRetention.Should().Be(Retention);
    }

    [Theory]
    [InlineData(LaterEvent.Create)]
    [InlineData(LaterEvent.Destroy)]
    public void GetById_ShouldReturnNull_WhenDestroyedTenMinutesAgoAndALaterBridgeEventArrives(LaterEvent later)
    {
        _sut.OnBridgeCreated("old", "basic", "simple_bridge", null, null);
        _sut.OnBridgeCreated("other", "basic", "simple_bridge", null, null);
        _sut.OnBridgeDestroyed("old");

        _clock.Now += Retention;
        if (later == LaterEvent.Create)
            _sut.OnBridgeCreated("later", "basic", "simple_bridge", null, null);
        else
            _sut.OnBridgeDestroyed("other");

        _sut.GetById("old").Should().BeNull("a bridge destroyed ten minutes ago is released on the next bridge event");
        _sut.BridgeCount.Should().Be(later == LaterEvent.Create ? 3 : 2, "the total count still includes a released bridge");
        _sut.ActiveBridges.Select(b => b.BridgeUniqueid).Should().NotContain("old");
    }

    [Fact]
    public void GetById_ShouldReturnNull_WhenBridgeDestroyedSubscriberThrowsAndRetentionPasses()
    {
        _sut.BridgeDestroyed += _ => throw new InvalidOperationException("subscriber failed");
        _sut.OnBridgeCreated("pinned", "basic", "simple_bridge", null, null);

        // The event pump swallows a subscriber's exception; here it reaches the caller, which does the same.
        var destroy = () => _sut.OnBridgeDestroyed("pinned");
        destroy.Should().Throw<InvalidOperationException>();

        _clock.Now += Retention;
        _sut.OnBridgeCreated("later", "basic", "simple_bridge", null, null);

        _sut.GetById("pinned").Should().BeNull("a failing subscriber must not keep a destroyed bridge held");
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldCountOnce_WhenRepeatedInsideRetention()
    {
        _sut.OnBridgeCreated("twice", "basic", "simple_bridge", null, null);
        _sut.OnBridgeDestroyed("twice");
        _clock.Now += TimeSpan.FromMinutes(5);
        _sut.OnBridgeDestroyed("twice");

        _sut.BridgeCount.Should().Be(1);
        _sut.ActiveBridges.Should().BeEmpty();
        _sut.GetById("twice").Should().NotBeNull("five minutes is inside the retention");
    }

    [Fact]
    public void GetById_ShouldFindDestroyedBridge_WhenInsideRetentionAndALaterBridgeEventArrives()
    {
        _sut.OnBridgeCreated("recent", "basic", "simple_bridge", null, null);
        _sut.OnBridgeDestroyed("recent");

        _clock.Now += Retention - TimeSpan.FromTicks(1);
        _sut.OnBridgeCreated("later", "basic", "simple_bridge", null, null);

        _sut.GetById("recent").Should().NotBeNull();
        _sut.GetById("recent")!.DestroyedAt.Should().Be(_clock.Now - Retention + TimeSpan.FromTicks(1));
    }

    [Fact]
    public void GetById_ShouldFindBridge_WhenLookedUpFromItsOwnBridgeDestroyedHandler()
    {
        AsteriskBridge? seen = null;
        _sut.BridgeDestroyed += b => seen = _sut.GetById(b.BridgeUniqueid!);
        _sut.OnBridgeCreated("self", "basic", "simple_bridge", null, null);

        _sut.OnBridgeDestroyed("self");

        seen.Should().NotBeNull();
        seen!.DestroyedAt.Should().Be(_clock.Now);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
