using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds that the default store — the in-memory store the SDK resolves when a consumer registers
/// none — does not keep a call the manager has released.
///
/// <para>The in-memory store keeps the same <see cref="CallSession"/> object the manager holds, and
/// the manager never tells it that a call was released. A release in the manager therefore frees a
/// dictionary entry and nothing else: the session, its participants and its audit trail stay
/// reachable from the store for the life of the process. The second test covers the save that
/// follows a release — a leg that joined the ended call and leaves after the release — which must not
/// put the call back.</para>
///
/// <para>Both read the store through its own lookups, by id and by <c>linkedid</c>.</para>
/// </summary>
public sealed class DefaultStoreReleaseTests
{
    [Fact]
    public async Task DefaultStore_ShouldNoLongerReturnAnEndedCall_WhenTheManagerHasReleasedIt()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("v");
        (await rig.Store.GetAsync(call.SessionId, CancellationToken.None)).Should().NotBeNull(
            "premise: the store holds the call while it is retained");

        rig.MovePastRetention();
        rig.Call("n");
        rig.Manager.GetById(call.SessionId).Should().BeNull(
            $"premise: the manager released the call. Measured: {rig.Describe()}");

        var byId = await rig.Store.GetAsync(call.SessionId, CancellationToken.None);
        var byLinkedId = await rig.Store.GetByLinkedIdAsync(ResidencyRig.LinkedIdOf("v"), CancellationToken.None);

        new { ById = byId?.SessionId, ByLinkedId = byLinkedId?.SessionId }.Should().BeEquivalentTo(
            new { ById = (string?)null, ByLinkedId = (string?)null },
            "the default store releases what the manager releases; holding the same object after the "
            + $"manager let it go frees nothing. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task DefaultStore_ShouldNotHoldTheCallAgain_WhenALegThatJoinedItLeavesAfterItsRelease()
    {
        await using var rig = new ResidencyRig();
        var call = rig.Call("v");
        rig.LegJoins("x-v", "v");
        (rig.Manager.GetByChannelId("x-v")?.SessionId).Should().Be(call.SessionId,
            "premise: the leg joined the ended call while it was retained");

        rig.MovePastRetention();
        rig.Call("n");
        rig.Manager.GetById(call.SessionId).Should().BeNull(
            $"premise: the call was released while the leg was still up. Measured: {rig.Describe()}");
        var beforeDeparture = (await rig.Store.GetAsync(call.SessionId, CancellationToken.None)) is null
            ? "not held"
            : "held";

        rig.LegLeaves("x-v");

        var byId = await rig.Store.GetAsync(call.SessionId, CancellationToken.None);
        var byLinkedId = await rig.Store.GetByLinkedIdAsync(ResidencyRig.LinkedIdOf("v"), CancellationToken.None);

        new { ById = byId?.SessionId, ByLinkedId = byLinkedId?.SessionId }.Should().BeEquivalentTo(
            new { ById = (string?)null, ByLinkedId = (string?)null },
            "the save that follows the leg's departure is about a call the manager no longer holds, "
            + $"and must not put it back in the store (store before the departure: {beforeDeparture}). "
            + $"Measured: {rig.Describe()}");
    }
}
