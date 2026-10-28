using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// What a participant's mute or talking change costs the conference table in heap bytes. Bytes, not time: the
/// allocation counter is exact and per thread, and every object counted escapes into the outbox, so no JIT tier
/// removes it. The path records no metric, so no listener can run on the measuring thread.
/// </summary>
public sealed class MeetMeManagerAllocationTests
{
    private const int Participants = 1_000;
    private const int WarmUpRounds = 3;
    private const string Room = "100";

    // One queued entry, one drain frame and the two execution-context copies the drain's frame switch makes, with
    // slack for that copy's size across runtime versions. A capturing lambda per change lands above it.
    private const int ObservedEventBound = 200;

    private static readonly string[] ChannelNames = Enumerable.Range(0, Participants)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"Local/conf@probe-{i:x8};2"))
        .ToArray();

    public enum Field
    {
        Muted,
        Talking,
    }

    private int _observed;

    // Every round flips the value, so every report in it changes the participant and reaches the notification.
    private bool _value;

    [Theory]
    [InlineData(Field.Muted)]
    [InlineData(Field.Talking)]
    public void OnUserStateChange_ShouldAllocateNothing_WhenNoHandlerIsSubscribed(Field field)
    {
        var manager = WithParticipants();

        var allocated = Measure(() => Flip(manager, field));

        HoldingValue(manager, field).Should().Be(Participants, "every report in the round changed its participant");
        allocated.Should().Be(0, $"a {field} change nobody is subscribed to queues nothing");
    }

    [Theory]
    [InlineData(Field.Muted)]
    [InlineData(Field.Talking)]
    public void OnUserStateChange_ShouldAllocateAtMostOneEntryAndOneFrame_WhenOneHandlerIsSubscribed(Field field)
    {
        var manager = WithParticipants();
        manager.UserStateChanged += Count;

        var observedBefore = 0;
        var allocated = Measure(() =>
        {
            observedBefore = _observed;
            Flip(manager, field);
        });

        (_observed - observedBefore).Should().Be(Participants, "every change reaches the subscribed handler");
        allocated.Should().BeLessThanOrEqualTo((long)ObservedEventBound * Participants,
            $"an observed {field} change costs one queued entry and its drain, nothing captured per change");
    }

    private void Count(MeetMeRoom room, MeetMeUser user) => _observed++;

    private void Flip(MeetMeManager manager, Field field)
    {
        _value = !_value;
        foreach (var channel in ChannelNames)
        {
            if (field == Field.Muted)
                manager.OnUserMuted(Room, channel, _value);
            else
                manager.OnUserTalking(Room, channel, _value);
        }
    }

    // Read after the measured round. The round before it set the opposite value, so all of them holding this round's
    // value means every report in it was a change.
    private int HoldingValue(MeetMeManager manager, Field field) =>
        manager.GetRoom(Room)!.Users.Values.Count(user => (field == Field.Muted ? user.Muted : user.Talking) == _value);

    private static MeetMeManager WithParticipants()
    {
        var manager = new MeetMeManager(NullLogger.Instance);
        foreach (var channel in ChannelNames)
            manager.OnUserJoined(Room, 0, channel);
        return manager;
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
