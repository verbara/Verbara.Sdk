using System.Globalization;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// Heap bytes a round of reports costs on the measuring thread, read with the per-thread allocation counter. A round
/// costs the same bytes every time it runs the same compiled code, so two readings in a row that agree are what it
/// cost; a reading taken while a background collection runs can be a few bytes to a few kilobytes off, either way, and
/// is read again. A cost paid in every round is caught; one paid in fewer than half of them is not what these readings
/// measure.
/// </summary>
internal static class AllocationReadings
{
    private const int WarmUpRounds = 3;
    private const int MaxReadings = 20;

    /// <summary>Warm-up rounds first, so tiering stubs and one-time initialisation fall before the measured rounds.</summary>
    public static long Measure(Action round) => MeasureAfter(static () => { }, round);

    /// <summary>As <see cref="Measure"/>, with the state each round consumes put back, unmeasured, before it.</summary>
    public static long MeasureAfter(Action setUp, Action round)
    {
        for (var i = 0; i < WarmUpRounds; i++)
        {
            setUp();
            round();
        }

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

    /// <summary>
    /// Reads rounds that are compared with one another in one pass, each on its own counter reading, until two passes
    /// in a row agree on every round.
    /// </summary>
    /// <remarks>
    /// What a manager's own bookkeeping costs can depend on the compiled code running it: a tier-up or an on-stack
    /// replacement can stack-allocate an object that does not escape, and it lands whenever the runtime installs it.
    /// Read one after the other, a round measured before that and one measured after it disagree by that object's
    /// bytes per report; read together, a pass that straddles it disagrees with the next and is read again. Each round
    /// is its own method and changes its compiled version on its own, so a pass could in principle mix two compiled
    /// states and still agree with the next. Measured on a path the JIT does change (a queue lookup that allocated a
    /// capturing factory per report), 3 000 in-process repetitions under CPU load read the compared difference right
    /// every time, where the same rounds read one after the other got it wrong 9 times in 3 000.
    /// </remarks>
    public static long[] MeasureTogether(params Action[] rounds)
    {
        for (var i = 0; i < WarmUpRounds; i++)
        {
            foreach (var round in rounds)
                round();
        }

        long[]? previous = null;
        for (var reading = 0; reading < MaxReadings; reading++)
        {
            var readings = new long[rounds.Length];
            for (var r = 0; r < rounds.Length; r++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                rounds[r]();
                readings[r] = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            if (previous is not null && previous.AsSpan().SequenceEqual(readings))
                return readings;
            previous = readings;
        }

        throw new Xunit.Sdk.XunitException(string.Create(CultureInfo.InvariantCulture,
            $"no two of {MaxReadings} passes over the same rounds agreed; the last read {string.Join(", ", previous!)}"));
    }
}
