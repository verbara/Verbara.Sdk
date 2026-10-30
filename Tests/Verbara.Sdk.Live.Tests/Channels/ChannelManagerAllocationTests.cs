using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// What a change costs the channel table in heap bytes. Bytes, not time: the allocation counter is exact and
/// per thread, so these bounds hold on any machine, and every object counted escapes (into the outbox's list,
/// its execution-context frame or an entry's owner), so no JIT tier removes it.
/// </summary>
public sealed class ChannelManagerAllocationTests
{
    private const int Channels = 1_000;
    private const int WarmUpRounds = 3;

    // One queued entry, one drain frame and the two execution-context copies the drain's frame switch makes
    // (176 bytes on .NET 10), with slack for that copy's size across runtime versions. A capturing lambda per
    // change (the smallest regression of this kind) lands above it.
    private const int ObservedEventBound = 200;

    private static readonly string[] UniqueIds = Enumerable.Range(0, Channels)
        .Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i}.1"))
        .ToArray();

    private static readonly string[] UnknownIds = Enumerable.Range(0, Channels)
        .Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"missing-{i}.1"))
        .ToArray();

    public enum Change
    {
        DialBegin,
        DialEnd,
        Hold,
        Unhold,
    }

    private int _observed;

    [Fact]
    public void OnNewState_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = Preloaded();

        var allocated = Measure(() =>
        {
            foreach (var id in UniqueIds)
                manager.OnNewState(id, ChannelState.Up);
        });

        allocated.Should().Be(0, "a state change nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnNewState_ShouldAllocateNothing_WhenTheChannelIsUnknown()
    {
        var manager = Preloaded();
        manager.ChannelStateChanged += Count;

        var allocated = Measure(() =>
        {
            foreach (var id in UnknownIds)
                manager.OnNewState(id, ChannelState.Up);
        });

        allocated.Should().Be(0, "a state change for a channel the table does not hold changes nothing and reports nothing");
        _observed.Should().Be(0);
    }

    [Theory]
    [InlineData(Change.DialBegin)]
    [InlineData(Change.DialEnd)]
    [InlineData(Change.Hold)]
    [InlineData(Change.Unhold)]
    public void OnChange_ShouldAllocateNothing_WhenNoHandlerIsSubscribed(Change change)
    {
        var manager = Preloaded();

        var allocated = Measure(() =>
        {
            foreach (var id in UniqueIds)
                Apply(manager, change, id);
        });

        allocated.Should().Be(0, $"a {change} nobody is subscribed to queues nothing");
    }

    [Fact]
    public void OnNewState_ShouldAllocateAtMostOneEntryAndOneFrame_WhenOneHandlerIsSubscribed()
    {
        var manager = Preloaded();
        manager.ChannelStateChanged += Count;

        var observedBefore = 0;
        var allocated = Measure(() =>
        {
            observedBefore = _observed;
            foreach (var id in UniqueIds)
                manager.OnNewState(id, ChannelState.Up);
        });

        (_observed - observedBefore).Should().Be(Channels, "every change reaches the subscribed handler");
        allocated.Should().BeLessThanOrEqualTo(
            (long)ObservedEventBound * Channels,
            "an observed change costs one queued entry and its drain, nothing captured per change");
    }

    private void Count(AsteriskChannel channel) => _observed++;

    private static ChannelManager Preloaded()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        foreach (var id in UniqueIds)
            manager.OnNewChannel(id, "PJSIP/" + id, ChannelState.Ring);
        return manager;
    }

    private static void Apply(ChannelManager manager, Change change, string id)
    {
        switch (change)
        {
            case Change.DialBegin:
                manager.OnDialBegin(id, "dest", "PJSIP/dest", dialString: null);
                break;
            case Change.DialEnd:
                manager.OnDialEnd(id, "ANSWER");
                break;
            case Change.Hold:
                manager.OnHold(id, "default");
                break;
            case Change.Unhold:
                manager.OnUnhold(id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    // Warm-up rounds first, so tiering stubs and one-time initialisation fall before the measured round.
    private static long Measure(Action round)
    {
        for (var i = 0; i < WarmUpRounds; i++)
            round();

        var before = GC.GetAllocatedBytesForCurrentThread();
        round();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
