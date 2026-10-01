using System.Collections;
using System.Globalization;
using System.Reflection;
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
        _sut.ActiveBridgeCount.Should().Be(0, "a repeated BridgeDestroy must not decrement twice");
        _sut.ActiveBridges.Should().BeEmpty();
        _sut.GetById("twice").Should().NotBeNull("five minutes is inside the retention");
        QueuedCount().Should().Be(1, "a repeated BridgeDestroy must not queue the bridge twice");
    }

    [Fact]
    public void ActiveBridgeCount_ShouldTrackCreatedMinusDestroyed_WhenBridgesComeAndGo()
    {
        _sut.OnBridgeCreated("a", "basic", "simple_bridge", null, null);
        _sut.OnBridgeCreated("b", "basic", "simple_bridge", null, null);
        _sut.OnBridgeCreated("a", "basic", "simple_bridge", null, null); // duplicate create: ignored
        _sut.ActiveBridgeCount.Should().Be(2);

        _sut.OnBridgeDestroyed("a");
        _sut.OnBridgeDestroyed("unknown");

        _sut.ActiveBridgeCount.Should().Be(1);
        _sut.ActiveBridgeCount.Should().Be(_sut.ActiveBridges.Count());
        _sut.BridgeCount.Should().Be(2);
    }

    [Fact]
    public void Clear_ShouldResetQueueAndBothCounts_WhenBridgesWereDestroyed()
    {
        _sut.OnBridgeCreated("gone", "basic", "simple_bridge", null, null);
        _sut.OnBridgeCreated("live", "basic", "simple_bridge", null, null);
        _sut.OnBridgeDestroyed("gone");

        _sut.Clear();

        _sut.BridgeCount.Should().Be(0);
        _sut.ActiveBridgeCount.Should().Be(0);
        QueuedCount().Should().Be(0);
    }

    [Fact]
    public void GetById_ShouldFindRecreatedBridge_WhenAStaleEntryForTheSameIdExpires()
    {
        // A queue entry left by a bridge destroyed before Clear() would, if it survived, name an id that now maps to a
        // different bridge. Clear() drops the queue; re-creating after a release must also leave the new bridge alone.
        _sut.OnBridgeCreated("reused", "basic", "simple_bridge", null, null);
        _sut.OnBridgeDestroyed("reused");
        _clock.Now += Retention;
        _sut.OnBridgeCreated("tick", "basic", "simple_bridge", null, null);
        _sut.GetById("reused").Should().BeNull();

        _sut.OnBridgeCreated("reused", "basic", "simple_bridge", null, null);
        _clock.Now += Retention;
        _sut.OnBridgeCreated("tick-2", "basic", "simple_bridge", null, null);

        _sut.GetById("reused").Should().NotBeNull("the re-created bridge was never destroyed");
        _sut.GetById("reused")!.DestroyedAt.Should().BeNull();
    }

    [Fact]
    public void GetById_ShouldHoldOnlyTheRetentionWindow_WhenBridgesKeepComingAndGoing()
    {
        // One bridge every 6 s for two hours: at most 10 min / 6 s = 100 destroyed bridges are inside the window,
        // plus the one destroyed exactly at the cutoff, which the next event releases.
        var step = TimeSpan.FromSeconds(6);
        const int n = 1_200;
        for (var i = 0; i < n; i++)
        {
            var id = $"b{i.ToString(CultureInfo.InvariantCulture)}";
            _sut.OnBridgeCreated(id, "basic", "simple_bridge", null, null);
            _sut.OnBridgeDestroyed(id);
            _clock.Now += step;
        }

        var held = Enumerable.Range(0, n).Count(i => _sut.GetById($"b{i.ToString(CultureInfo.InvariantCulture)}") is not null);

        held.Should().Be(100);
        QueuedCount().Should().Be(100);
        _sut.BridgeCount.Should().Be(n);
        _sut.ActiveBridgeCount.Should().Be(0);
    }

    // Reflection (not [UnsafeAccessor]): the queue is private and has no public reader.
    private int QueuedCount()
    {
        var field = typeof(BridgeManager).GetField("_destroyedOrder", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("BridgeManager keeps its destroyed bridges in _destroyedOrder");
        return ((ICollection)field!.GetValue(_sut)!).Count;
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
