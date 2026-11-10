using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A call the session engine has not observed in full is decided only on a channel table no dropped event has left
/// untrusted: once the full buffer has refused an event no action received, such a call is kept — no leg retired, no
/// ending, no promotion — until a channel reconciliation covers the loss. No part of the engine sends a <c>Status</c>
/// for it while the loss is behind the backlog, a reconnect's lost-session backlog included; once the loss is
/// delivered, the sweep's first verification, an admission on a complete table, or the host's own reconciliation
/// covers it, and that read's completion decides the call.
/// </summary>
/// <remarks>
/// Over <see cref="LossRig"/>: a real connection with a two-event buffer, a <see cref="CallSessionManager"/> attached to
/// the server, one sweep tick at a time on the manual clocks. The drop is a <c>Newchannel</c> (or another event no
/// action received) refused while a handler holds the dispatch with the buffer full; it is delivered once a tail event
/// written after the release has been dispatched.
/// </remarks>
[Collection(SweepCounterGroup.Name)]
public sealed class LossAwareTableDecisionTests
{
    private const string Reload = "reload";

    // ---- the waiting-call source ----

    [Fact]
    public async Task Sweep_ShouldSendNothingForACallWaitingOnAnIncompleteTable_WhileTheLossIsBehindTheBacklog()
    {
        await using var rig = await LossRig.StartAsync(refuseFirstLoad: true);
        var waiting = LossRig.Reconstructed("R");
        rig.Manager.RegisterReconstructedSession(waiting).Should().BeTrue();
        await rig.HoldAndFillAsync();
        await rig.DropUnrelatedAsync();

        var first = await rig.TickAsync(pool: true);
        var second = await rig.TickAsync(pool: true);
        await rig.DeliverAsync();
        var delivered = await rig.TickAsync(pool: true);

        using (new AssertionScope())
        {
            first.Should().BeEquivalentTo(new { Status = 0, Waited = false, Verification = "skipped:no-candidate" },
                "a call waiting on an incomplete table adds no candidate while a loss is behind the backlog");
            second.Should().BeEquivalentTo(new { Status = 0, Waited = false, Verification = "skipped:no-candidate" },
                "nor on the next tick");
            delivered.Should().BeEquivalentTo(new { Status = 1, Verification = "run", Trigger = "table" },
                "the first tick after delivery verifies once, prompted by the table");
            rig.EndingsOf(waiting).Should().Be(1, "the snapshot lists nothing of R");
            waiting.Metadata.GetValueOrDefault("cause").Should().Be(Reload);
        }
    }
}
