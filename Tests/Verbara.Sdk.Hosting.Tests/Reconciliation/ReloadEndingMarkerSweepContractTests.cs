using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

// The same contract class is linked into Hosting.Tests, where the real internal sweep is accessible.
[Collection(global::Verbara.Sdk.Hosting.Tests.Reconciliation.SweepCounterGroup.Name)]
public sealed partial class ReloadEndingMarkerContractTests
{
    [Fact]
    public async Task Sweep_ShouldWriteTheSameMarker_WhenItsStatusFreePassEndsThePendingLeftCall()
    {
        await using var first = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        await using var replacement = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        await using var rig = new MarkerRig(first);
        await PreparePendingReplacement(first, rig, replacement);
        var asked = replacement.FirstPeer.Asked("Status");
        await new global::Verbara.Sdk.Hosting.ServerSweep(rig.Manager, new SessionOptions(), NullLogger.Instance, TimeProvider.System)
            .SweepAsync(replacement.Server, ServerId, CancellationToken.None);
        rig.Manager.GetByLinkedId(GoneUid)!.EndingDelivered.Should().BeTrue();
        replacement.FirstPeer.Asked("Status").Should().Be(asked);
        await rig.ObservedCallAsync(replacement.FirstPeer, replacement);
        rig.Measured().Should().BeEquivalentTo(MarkerRig.Contract, MarkerRig.Because("Status-free sweep decision"));
        rig.EndingsOf(GoneUid).Should().Be(1);
        rig.EndingsOf(ObservedUid).Should().Be(1);
    }
}
