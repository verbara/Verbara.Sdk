using System.Diagnostics;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Resilience.Diagnostics;
using Xunit;

namespace Verbara.Sdk.Resilience.Tests;

/// <summary>
/// Two threads released on the same instant, once per round, against one breaker. The clock is a constant:
/// a real clock's read in the trip's path pulls the two threads apart enough to lose the race.
/// </summary>
public sealed class CircuitBreakerRaceTests
{
    private const int Rounds = 2_000_000;
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void RecordSuccess_ShouldNotLeaveAnOpenCircuitWithoutAnOpeningTime_WhenAFailureTripsAtTheSameInstant()
    {
        var state = new CircuitBreakerState("race");
        var clock = new FixedClock();
        long dead = 0;
        long cannotTrip = 0;

        var rounds = Lockstep(
            state.RecordSuccess,
            () => state.RecordFailure(1, clock),
            () =>
            {
                if (state.IsOpen && state.OpenedAt is null)
                    dead++;

                state.RecordFailure(1, clock);
                if (!state.IsOpen || state.OpenedAt is null)
                    cannotTrip++;

                state.RecordSuccess();
            });

        rounds.Should().BeGreaterThan(0);
        using (new AssertionScope())
        {
            dead.Should().Be(0, "of {0} rounds, none may leave a circuit open with no opening time", rounds);
            cannotTrip.Should().Be(0, "of {0} rounds, the next failure must leave the circuit open with its opening time", rounds);
        }
    }

    [Fact]
    public void ShouldAllow_ShouldNotEraseATrip_WhenItRunsWhileTheTripIsInProgress()
    {
        var state = new CircuitBreakerState("race");
        var clock = new FixedClock();
        long lost = 0;

        var rounds = Lockstep(
            () =>
            {
                for (var i = 0; i < 6; i++)
                    state.ShouldAllow(OpenWindow, clock);
            },
            () => state.RecordFailure(1, clock),
            () =>
            {
                if (!state.IsOpen)
                    lost++;
                state.RecordSuccess();
            });

        rounds.Should().BeGreaterThan(0);
        lost.Should().Be(0, "of {0} rounds, an admission check may not erase a trip", rounds);
    }

    [Fact]
    public void ReadState_ShouldNotEraseATrip_WhenItRunsWhileTheTripIsInProgress()
    {
        var state = new CircuitBreakerState("race");
        var clock = new FixedClock();
        long lost = 0;

        var rounds = Lockstep(
            () =>
            {
                for (var i = 0; i < 6; i++)
                    state.ReadState(OpenWindow, clock);
            },
            () => state.RecordFailure(1, clock),
            () =>
            {
                if (!state.IsOpen)
                    lost++;
                state.RecordSuccess();
            });

        rounds.Should().BeGreaterThan(0);
        lost.Should().Be(0, "of {0} rounds, a read may not erase a trip", rounds);
    }

    [Fact]
    public void RecordFailure_ShouldOvertakeASuccess_WhenTheSuccessLandsJustBeforeTheTrip()
    {
        var state = new CircuitBreakerState("race");
        var clock = new FixedClock();
        clock.OnNextRead = state.RecordSuccess;

        state.RecordFailure(1, clock);

        state.IsOpen.Should().BeTrue("the clock is read before the trip, so the trip comes after that success");
        state.OpenedAt.Should().NotBeNull();
        state.ShouldAllow(OpenWindow, clock).Should().BeFalse();
    }

    [Fact]
    public void ShouldAllow_ShouldNotLeaveAnOpenCircuitWithoutAnOpeningTime_WhenATripRacesItsReset()
    {
        var state = new CircuitBreakerState("race");
        var opened = new FixedClock();
        var windowElapsed = new LateClock();
        state.RecordFailure(1, opened);
        long dead = 0;

        var rounds = Lockstep(
            () => state.ShouldAllow(OpenWindow, windowElapsed),
            () => state.RecordFailure(1, windowElapsed),
            () =>
            {
                if (state.IsOpen && state.OpenedAt is null)
                    dead++;

                state.RecordSuccess();
                state.RecordFailure(1, opened);
            });

        rounds.Should().BeGreaterThan(0);
        dead.Should().Be(0, "of {0} rounds, a reset past the window may not leave a circuit open with no opening time", rounds);
    }

    // Runs one and two together, then after with both done, until Rounds or Budget, and returns the rounds run.
    internal static long Lockstep(Action one, Action two, Action after)
    {
        var go = 0;
        var stop = 0;
        var doneOne = 0;
        var doneTwo = 0;

        Thread Worker(Action body, Action<int> done) => new(() =>
        {
            var seen = 0;
            while (true)
            {
                while (Volatile.Read(ref go) == seen)
                {
                    if (Volatile.Read(ref stop) != 0)
                        return;
                    Thread.SpinWait(1); // fence-allow: LOOP-DRIVER — a signal would wake the threads too far apart to race; bounded by stop
                }

                seen++;
                body();
                done(seen);
            }
        }) { IsBackground = true };

        var first = Worker(one, i => Volatile.Write(ref doneOne, i));
        var second = Worker(two, i => Volatile.Write(ref doneTwo, i));
        first.Start();
        second.Start();

        var clock = Stopwatch.StartNew();
        var round = 0;
        while (round < Rounds && clock.Elapsed < Budget) // fence-allow: GUARD-TIMEOUT — caps a starved lane; the rounds are the test, not a wait
        {
            round++;
            Volatile.Write(ref go, round);
            while (Volatile.Read(ref doneOne) < round || Volatile.Read(ref doneTwo) < round)
                Thread.SpinWait(1); // fence-allow: LOOP-DRIVER — waits for the two workers of this round, which always finish
            after();
        }

        Volatile.Write(ref stop, 1);
        first.Join();
        second.Join();
        return round;
    }

    private sealed class FixedClock : TimeProvider
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

    // A full open window after FixedClock's time.
    private sealed class LateClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + OpenWindow;
    }
}
