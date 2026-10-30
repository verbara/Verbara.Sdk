using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Live.Tests.MeetMe;

/// <summary>
/// What a participant's join, leave, mute or talking change costs the conference table in heap bytes. Bytes, not
/// time: the allocation counter is per thread and exact once two readings of the same round agree (a cost paid in every
/// round is caught; one paid in fewer than half of them is not what these tests measure), and every object
/// counted escapes into the outbox, so no JIT tier removes it. These paths record no metric, so no listener can run
/// on the measuring thread.
/// </summary>
public sealed class MeetMeManagerAllocationTests
{
    private const int Participants = 1_000;
    private const int WarmUpRounds = 3;
    private const int MaxReadings = 20;
    private const string Room = "100";

    // One queued entry, one drain frame and the two execution-context copies the drain's frame switch makes, with
    // slack for that copy's size across runtime versions. A capturing lambda per change lands above it.
    private const int ObservedEventBound = 200;

    private static readonly string[] ChannelNames = Enumerable.Range(0, Participants)
        .Select(i => string.Create(CultureInfo.InvariantCulture, $"Local/conf@probe-{i:x8};2"))
        .ToArray();

    private const string Remaining = "Local/conf@probe-remaining;2";

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

        var allocated = MeasureObserved(() => Flip(manager, field));

        allocated.Should().BeLessThanOrEqualTo((long)ObservedEventBound * Participants,
            $"an observed {field} change costs one queued entry and its drain, nothing captured per change");
    }

    // A join allocates the participant it stores, whether or not anyone hears it. It is measured against a control
    // manager doing the same joins with a handler subscribed, less the price of the notifications that handler
    // received, read on the mute path, which allocates nothing else. Each round joins again the participants the room
    // holds, so every join replaces one and announces the new one.
    [Fact]
    public void OnUserJoined_ShouldAllocateNothingForTheEvent_WhenNoHandlerIsSubscribed()
    {
        var manager = WithParticipants();
        var control = WithParticipants();
        control.UserJoined += Count;

        var allocated = Measure(() => Join(manager));
        var observed = MeasureObserved(() => Join(control));

        (allocated - (observed - NotificationPrice())).Should().Be(0, "a join nobody is subscribed to queues nothing");
    }

    // One participant stays, so the room outlives every round and only the participants' leaves are measured.
    [Fact]
    public void OnUserLeft_ShouldAllocateNothing_WhenNoHandlerIsSubscribed()
    {
        var manager = WithParticipants();
        manager.OnUserJoined(Room, 0, Remaining);

        var allocated = MeasureAfter(() => Join(manager), () =>
        {
            foreach (var channel in ChannelNames)
                manager.OnUserLeft(Room, channel);
        });

        manager.GetRoom(Room)!.Users.Keys.Should().Equal([Remaining], "every report in the round removed its participant");
        allocated.Should().Be(0, "a leave nobody is subscribed to queues nothing");
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

    // What one round of reports costs a manager when each one queues one notification to a subscribed handler. The
    // mute path allocates nothing else (OnUserStateChange_ShouldAllocateNothing_WhenNoHandlerIsSubscribed).
    private long NotificationPrice()
    {
        var manager = WithParticipants();
        manager.UserStateChanged += Count;
        return MeasureObserved(() => Flip(manager, Field.Muted));
    }

    private static void Join(MeetMeManager manager)
    {
        foreach (var channel in ChannelNames)
            manager.OnUserJoined(Room, 0, channel);
    }

    // Measures one round and checks it reached the handler once per report.
    private long MeasureObserved(Action round)
    {
        var observedBefore = 0;
        var allocated = Measure(() =>
        {
            observedBefore = _observed;
            round();
        });
        (_observed - observedBefore).Should().Be(Participants, "every report reaches the subscribed handler");
        return allocated;
    }

    private static MeetMeManager WithParticipants()
    {
        var manager = new MeetMeManager(NullLogger.Instance);
        Join(manager);
        return manager;
    }

    // As Measure, with the state each round consumes put back, unmeasured, before it.
    private static long MeasureAfter(Action setUp, Action round)
    {
        for (var i = 0; i < WarmUpRounds; i++)
        {
            setUp();
            round();
        }

        // A round costs the same bytes every time, but a reading taken while a background collection runs can be a few
        // bytes to a few kilobytes off, either way. Two readings in a row that agree are what the round cost.
        long? previous = null;
        for (var reading = 0; reading < MaxReadings; reading++)
        {
            setUp();
            var before = GC.GetAllocatedBytesForCurrentThread();
            round();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated == previous)
                return allocated;
            previous = allocated;
        }

        throw new Xunit.Sdk.XunitException(string.Create(CultureInfo.InvariantCulture,
            $"no two of {MaxReadings} readings of the same round agreed; the last read {previous} bytes"));
    }

    // Warm-up rounds first, so tiering stubs and one-time initialisation fall before the measured rounds.
    private static long Measure(Action round) => MeasureAfter(static () => { }, round);
}
