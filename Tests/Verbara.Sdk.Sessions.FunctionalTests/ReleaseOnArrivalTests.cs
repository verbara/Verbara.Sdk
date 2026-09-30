using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that releasing ended calls does not wait for another call to end: it is evaluated when a
/// leg arrives as well as when a call ends, before the arriving leg is correlated, one walk at a time
/// however many servers feed the manager, and never on a timer.
///
/// <para>Release used to run only from a call's ending. A process that kept accepting calls but
/// stopped completing them — every new call still up — therefore stopped releasing what it already
/// held, and a leg arriving with the <c>linkedid</c> of an ended call past retention joined that
/// call, because nothing had evaluated it since it ended.</para>
///
/// <para>Retention is crossed on the manager's clock seam (<see cref="ResidencyRig.MovePastRetention"/>),
/// never by waiting. The last test makes an arrival on one server meet a release walk in progress on
/// another by construction: the ending's walk is parked inside its read of the release clock
/// (<see cref="ManualClock.HoldNextRead"/>), and the arrival is started only then. Every wait in it ends
/// on the event it is for.</para>
/// </summary>
public sealed class ReleaseOnArrivalTests
{
    /// <summary>Ended calls held before the arrival.</summary>
    private const int Ended = 3;

    /// <summary>Bounds each wait on another thread; never reached when the code under test runs.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Release_ShouldFreeEndedCallsPastRetention_WhenOnlyALegArrivesAfterThem()
    {
        await using var rig = new ResidencyRig();
        var ended = rig.Calls("v", Ended);
        rig.MovePastRetention();

        // One leg of a new call arrives, and nothing ends from here on.
        rig.LegJoins("c-n", "n");
        var arrived = rig.Manager.GetByChannelId("c-n");

        new
        {
            EndedById = ended.Count(s => rig.Manager.GetById(s.SessionId) is not null),
            EndedByLinkedId = ended.Count(s => rig.Manager.GetByLinkedId(s.LinkedId) is not null),
            QueueEntries = ended.Sum(s => rig.QueueEntriesFor(s.SessionId)),
            EndedHeldPastRetention = rig.EndedHeldPastRetention(),
            Endings = ended.Sum(s => rig.EndingsFor(s.SessionId)),
            Active = rig.Manager.ActiveSessions.Select(s => s.SessionId).ToList(),
        }.Should().BeEquivalentTo(
            new
            {
                EndedById = 0,
                EndedByLinkedId = 0,
                QueueEntries = 0,
                EndedHeldPastRetention = 0,
                Endings = Ended,
                Active = new[] { arrived?.SessionId },
            },
            $"a process that keeps accepting calls releases what it holds past retention even when no call "
            + $"ends: the arrival alone releases all {Ended} ended calls, and the arriving call is the one "
            + $"left active. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Arrival_ShouldOpenANewCall_WhenItsLegCarriesTheLinkedIdOfAnEndedCallPastRetention()
    {
        await using var rig = new ResidencyRig();
        var ended = rig.Call("v");
        rig.MovePastRetention();

        rig.LegJoins("x-v", "v");
        var leg = rig.Manager.GetByChannelId("x-v");

        new
        {
            LegIsOn = Name(leg, ended),
            LegCallActive = leg is not null && rig.Manager.ActiveSessions.Contains(leg),
            LinkedIdLeadsTo = Name(rig.Manager.GetByLinkedId(ended.LinkedId), ended),
            EndedCallHeld = rig.Manager.GetById(ended.SessionId) is not null,
            EndedCallEndings = rig.EndingsFor(ended.SessionId),
            EndedCallQueueEntries = rig.QueueEntriesFor(ended.SessionId),
        }.Should().BeEquivalentTo(
            new
            {
                LegIsOn = "a new call",
                LegCallActive = true,
                LinkedIdLeadsTo = "a new call",
                EndedCallHeld = false,
                EndedCallEndings = 1,
                EndedCallQueueEntries = 0,
            },
            "release is evaluated before the arriving leg is correlated, so the ended call past retention "
            + "is released first and the leg opens a call of its own instead of joining one that ended "
            + $"and is being let go. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldReleaseNothingAndGrowNothing_WhenNoCallArrivesOrEnds()
    {
        await using var rig = new ResidencyRig();
        var ended = rig.Calls("v", Ended);
        var live = rig.OpenAnsweredCall("live");
        rig.MovePastRetention();

        var before = await HeldAsync(rig, ended, live);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        var after = await HeldAsync(rig, ended, live);

        var expected = new Held(
            RecentCompleted: Ended,
            EndedById: Ended,
            EndedInStore: Ended,
            QueueEntries: Ended,
            LiveActive: true,
            TimersCreated: 0);

        new { Before = before, After = after }.Should().BeEquivalentTo(
            new { Before = expected, After = expected },
            "nothing runs on a timer: with no call arriving or ending, the ended calls past retention stay "
            + "held however much time passes — nothing is released, nothing grows, and the manager asks its "
            + $"clock for no timer. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldWalkOnceAtATimeAndLeaveTheHeldCallsConsistent_WhenAnArrivalOnOneServerMeetsAnEndingOnAnother()
    {
        await using var rig = new ResidencyRig();
        var serverB = rig.AttachServer("residency-srv-b");

        // Both paths run once before the measured pair, so neither thread meets a first call's cost
        // inside the window: the only thing the arrival can wait for there is the walk in progress.
        var endedCalls = new List<CallSession> { rig.Call("warm-a"), rig.Call("warm-b", on: serverB) };
        rig.LegJoins("warm-x", "warm-x", on: serverB);
        endedCalls.Add(rig.Manager.GetByChannelId("warm-x")
            ?? throw new InvalidOperationException($"premise: the warm-up leg opened a call. Measured: {rig.Describe()}"));
        rig.LegLeaves("warm-x", on: serverB);

        var victim = rig.Call("v");
        var ending = rig.OpenAnsweredCall("a");
        endedCalls.Add(victim);
        endedCalls.Add(ending);
        rig.MovePastRetention();

        var hold = rig.Clock.HoldNextRead();
        Exception? endingFault = null;
        Exception? arrivalFault = null;

        // Server A: the call's last leg hangs up, so its ending runs a release walk, which parks inside
        // its read of the release clock. Server B: a leg arrives with the victim's linkedid.
        var endingThread = new Thread(() => Capture(() => rig.HangUp("a"), ex => endingFault = ex)) { IsBackground = true };
        var arrivalThread = new Thread(() => Capture(() => rig.LegJoins("x-v", "v", on: serverB), ex => arrivalFault = ex))
        {
            IsBackground = true,
        };

        string arrival;
        try
        {
            // Nothing on this thread reads the clock until the hold is released (rig.Describe() does):
            // that read would be counted as a second walk.
            endingThread.Start();
            hold.WaitUntilReached().Should().BeTrue(
                "premise: the ending on server A runs a release walk, which reads the release clock");

            arrivalThread.Start();
            arrival = WhatTheArrivalDid(arrivalThread, hold);
        }
        finally
        {
            hold.Release();
        }

        var joined = new { Ending = endingThread.Join(Bound), Arrival = arrivalThread.Join(Bound) };
        var leg = rig.Manager.GetByChannelId("x-v");

        new
        {
            Arrival = arrival,
            ReadsWhileAWalkWasInProgress = hold.ReadsWhileHeld,
            Joined = joined,
            Faults = new[] { endingFault?.Message, arrivalFault?.Message },
            EndedHeld = rig.Manager.GetRecentCompleted(int.MaxValue).Count(),
            QueueEntries = endedCalls.Sum(s => rig.QueueEntriesFor(s.SessionId)),
            EndingsPerCall = endedCalls.Select(s => rig.EndingsFor(s.SessionId)).ToList(),
            LegIsOn = Name(leg, victim),
            LinkedIdLeadsTo = Name(rig.Manager.GetByLinkedId(victim.LinkedId), victim),
            IdLeadsToTheLegsCall = leg is not null && ReferenceEquals(rig.Manager.GetById(leg.SessionId), leg),
            Active = rig.Manager.ActiveSessions.Select(s => s.SessionId).ToList(),
        }.Should().BeEquivalentTo(
            new
            {
                Arrival = WaitedForTheWalk,
                ReadsWhileAWalkWasInProgress = 0,
                Joined = new { Ending = true, Arrival = true },
                Faults = new string?[] { null, null },
                EndedHeld = 0,
                QueueEntries = 0,
                EndingsPerCall = endedCalls.Select(_ => 1).ToList(),
                LegIsOn = "a new call",
                LinkedIdLeadsTo = "a new call",
                IdLeadsToTheLegsCall = true,
                Active = new[] { leg?.SessionId },
            },
            "the release walk is serialized: an arrival on server B waits for the walk an ending on server A "
            + "has in progress, instead of walking the same queue beside it; once both have run, every ended "
            + "call past retention is released and dequeued, each ended once, and the only call held is the "
            + "one the arriving leg opened — reachable by its id, its linkedid and its channel. "
            + $"Measured: {rig.Describe()}");
    }

    private const string WaitedForTheWalk = "waited for the walk in progress";

    /// <summary>
    /// Watches the arrival's thread until it shows what it did about the walk parked on the clock:
    /// read the clock beside it, finished without waiting, or blocked — which, once the arrival has
    /// started, can only be the walk's lock, since the parked walk holds nothing else the arrival
    /// takes. Ends on the first of those; the bound only turns a thread that shows none into a report.
    /// </summary>
    private static string WhatTheArrivalDid(Thread arrivalThread, ReadHold hold)
    {
        using var bound = new CancellationTokenSource(Bound);
        while (true)
        {
            if (hold.ReadsWhileHeld > 0)
                return "read the release clock while the walk was in progress";

            var state = arrivalThread.ThreadState;
            if (state.HasFlag(System.Threading.ThreadState.Stopped))
                return "finished without waiting for the walk";

            if (state.HasFlag(System.Threading.ThreadState.WaitSleepJoin))
                return WaitedForTheWalk;

            if (bound.IsCancellationRequested)
                return $"showed nothing within {Bound} (state {state})";

            Thread.Yield();
        }
    }

    private static void Capture(Action action, Action<Exception> fault)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fault(ex);
        }
    }

    /// <summary>Which call <paramref name="session"/> is, relative to the ended one.</summary>
    private static string Name(CallSession? session, CallSession ended) => session switch
    {
        null => "no call",
        _ when ReferenceEquals(session, ended) => "the ended call",
        _ => "a new call",
    };

    private sealed record Held(
        int RecentCompleted,
        int EndedById,
        int EndedInStore,
        int QueueEntries,
        bool LiveActive,
        int TimersCreated);

    private static async Task<Held> HeldAsync(ResidencyRig rig, IReadOnlyList<CallSession> ended, CallSession live)
    {
        var inStore = 0;
        foreach (var session in ended)
            inStore += await rig.Store.GetAsync(session.SessionId, CancellationToken.None) is not null ? 1 : 0;

        return new Held(
            RecentCompleted: rig.Manager.GetRecentCompleted(int.MaxValue).Count(),
            EndedById: ended.Count(s => rig.Manager.GetById(s.SessionId) is not null),
            EndedInStore: inStore,
            QueueEntries: ended.Sum(s => rig.QueueEntriesFor(s.SessionId)),
            LiveActive: rig.Manager.ActiveSessions.Contains(live),
            TimersCreated: rig.Clock.TimersCreated);
    }
}
