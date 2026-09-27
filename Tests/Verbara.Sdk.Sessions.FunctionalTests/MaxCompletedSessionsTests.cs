using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that <see cref="SessionOptions.MaxCompletedSessions"/> bounds nothing, in either direction,
/// and that <see cref="SessionOptions.CompletedRetention"/> is the only bound on the ended calls held.
///
/// <para>The option is published, validated and read by nothing. Making it a cap was measured and
/// rejected: a cap below the ended calls held within retention releases calls that ended moments
/// ago, so a deferred <c>GetById</c> returns null for a call its consumer was just told about. The
/// first test holds the option to releasing nothing within retention; the second holds it to keeping
/// nothing past retention, so it cannot come back as a floor either.</para>
///
/// <para>Every ending runs the release walk, so by the last of <see cref="Held"/> endings the walk has
/// run with more ended calls held than the configured maximum. Retention is crossed on the manager's
/// clock seam, never by waiting.</para>
/// </summary>
public sealed class MaxCompletedSessionsTests
{
    /// <summary>The configured maximum.</summary>
    private const int Max = 5;

    /// <summary>Ended calls held at once: four times <see cref="Max"/>.</summary>
    private const int Held = 20;

    [Fact]
    public async Task Release_ShouldReleaseNoEndedCall_WhenMoreAreHeldWithinRetentionThanMaxCompletedSessions()
    {
        await using var rig = new ResidencyRig(new SessionOptions { MaxCompletedSessions = Max });

        var ended = rig.Calls("n", Held);

        var cutoff = rig.Cutoff;
        ended.Count(s => s.CompletedAt >= cutoff).Should().Be(Held,
            $"premise: every call ended within retention. Measured: {rig.Describe()}");

        var inStore = 0;
        foreach (var session in ended)
        {
            if (await rig.Store.GetAsync(session.SessionId, CancellationToken.None) is not null)
                inStore++;
        }

        new
        {
            ById = ended.Count(s => rig.Manager.GetById(s.SessionId) is not null),
            ByLinkedId = ended.Count(s => rig.Manager.GetByLinkedId(s.LinkedId) is not null),
            RecentCompleted = rig.Manager.GetRecentCompleted(int.MaxValue).Count(),
            QueueEntries = ended.Sum(s => rig.QueueEntriesFor(s.SessionId)),
            InStore = inStore,
        }.Should().BeEquivalentTo(
            new { ById = Held, ByLinkedId = Held, RecentCompleted = Held, QueueEntries = Held, InStore = Held },
            $"MaxCompletedSessions ({Max}) bounds nothing: all {Held} ended calls are within "
            + "CompletedRetention, the only bound, so release — run by every one of their endings — "
            + $"lets none of them go. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldReleaseEveryEndedCall_WhenRetentionHasPassedWhateverMaxCompletedSessionsSays()
    {
        await using var rig = new ResidencyRig(new SessionOptions { MaxCompletedSessions = Max });
        var ended = rig.Calls("n", Held).ToList();

        rig.MovePastRetention();
        ended.Add(rig.Call("late"));

        new
        {
            EndedHeldPastRetention = rig.EndedHeldPastRetention(),
            ById = ended.Count(s => rig.Manager.GetById(s.SessionId) is not null),
        }.Should().BeEquivalentTo(
            new { EndedHeldPastRetention = 0, ById = 0 },
            "CompletedRetention is the only bound, and MaxCompletedSessions is not a floor either: once "
            + $"retention has passed, every one of the {ended.Count} ended calls is released, the last "
            + $"ending's own call included. Measured: {rig.Describe()}");
    }
}
