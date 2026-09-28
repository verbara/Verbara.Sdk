using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds <see cref="SessionOptions.CompletedRetention"/> as the bound on how long an ended call is
/// held, at its edge: a call that ended less than, or exactly, the retention period ago is still held
/// when release runs, and one that ended more than that ago is not.
///
/// <para>"More than the period has passed" is the release condition, so a call that ended exactly the
/// period ago is not yet past it: releasing it would release it early. Both tests place the release
/// cutoff on the manager's clock seam relative to a call's own completion time
/// (<see cref="ResidencyRig.PlaceCutoffAt"/>), tick-exact and without waiting, then run release by both
/// of its triggers — a leg arriving and a call ending — and read the call back through every place
/// that can hold it: the manager by id and by <c>linkedid</c>, its recent completed calls, the default
/// store, and the release queue.</para>
/// </summary>
public sealed class RetentionBoundTests
{
    [Fact]
    public async Task Release_ShouldKeepTheEndedCalls_WhenTheyEndedNoMoreThanTheRetentionPeriodAgo()
    {
        await using var rig = new ResidencyRig();
        var edge = rig.Call("edge");
        var inside = rig.Call("inside");

        // The first call ended exactly the retention period ago; the second, later, less than that.
        rig.PlaceCutoffAt(edge.CompletedAt!.Value);
        new { Edge = edge.CompletedAt == rig.Cutoff, Inside = inside.CompletedAt > rig.Cutoff }.Should().BeEquivalentTo(
            new { Edge = true, Inside = true },
            $"premise: one call at the retention edge, one inside it. Measured: {rig.Describe()}");

        RunRelease(rig);

        (await WhereHeldAsync(rig, edge, inside)).Should().BeEquivalentTo(
            new[] { Held(edge), Held(inside) },
            options => options.WithStrictOrdering(),
            "a call that ended no more than CompletedRetention ago is still held when release runs — "
            + "by id, by linkedid, among the recent completed calls and in the default store, with its "
            + "release still queued: releasing the call at the edge would release it before the period "
            + $"had passed. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Release_ShouldReleaseTheEndedCall_WhenMoreThanTheRetentionPeriodHasPassedSinceItEnded()
    {
        await using var rig = new ResidencyRig();
        var past = rig.Call("past");
        var inside = rig.Call("inside");

        // One tick more than the retention period has passed since the first call ended.
        rig.PlaceCutoffAt(past.CompletedAt!.Value + TimeSpan.FromTicks(1));
        new { Past = past.CompletedAt < rig.Cutoff, Inside = inside.CompletedAt >= rig.Cutoff }.Should().BeEquivalentTo(
            new { Past = true, Inside = true },
            $"premise: one call a tick past the retention edge, one within it. Measured: {rig.Describe()}");

        RunRelease(rig);

        (await WhereHeldAsync(rig, past, inside)).Should().BeEquivalentTo(
            new[] { Released(past), Held(inside) },
            options => options.WithStrictOrdering(),
            "once more than CompletedRetention has passed since a call ended, release lets it go "
            + "everywhere it was held, and only it: the call that ended after it, within the period, "
            + $"stays held. Measured: {rig.Describe()}");
    }

    /// <summary>Runs release by both of its triggers: a leg of a new call arrives, and another call ends.</summary>
    private static void RunRelease(ResidencyRig rig)
    {
        rig.LegJoins("c-arrival", "arrival");
        rig.Call("ending");
    }

    private static Residency Held(CallSession call) =>
        new(call.SessionId, ById: true, ByLinkedId: true, InRecentCompleted: true, InStore: true, QueueEntries: 1);

    private static Residency Released(CallSession call) =>
        new(call.SessionId, ById: false, ByLinkedId: false, InRecentCompleted: false, InStore: false, QueueEntries: 0);

    private static async Task<Residency[]> WhereHeldAsync(ResidencyRig rig, params CallSession[] calls)
    {
        var recent = rig.Manager.GetRecentCompleted(int.MaxValue).ToList();
        var residency = new List<Residency>(calls.Length);
        foreach (var call in calls)
        {
            residency.Add(new Residency(
                call.SessionId,
                ById: ReferenceEquals(rig.Manager.GetById(call.SessionId), call),
                ByLinkedId: ReferenceEquals(rig.Manager.GetByLinkedId(call.LinkedId), call),
                InRecentCompleted: recent.Contains(call),
                InStore: ReferenceEquals(await rig.Store.GetAsync(call.SessionId, CancellationToken.None), call),
                QueueEntries: rig.QueueEntriesFor(call.SessionId)));
        }

        return [.. residency];
    }

    /// <summary>Where one ended call is still held.</summary>
    private sealed record Residency(
        string Call,
        bool ById,
        bool ByLinkedId,
        bool InRecentCompleted,
        bool InStore,
        int QueueEntries);
}
