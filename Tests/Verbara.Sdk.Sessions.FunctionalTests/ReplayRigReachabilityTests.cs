using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.FunctionalTests;

public sealed class ReplayRigReachabilityTests
{
    [Fact]
    public async Task ReplayRig_ShouldBeReachableFromAnotherTest_WhenAnInProcessObserverReplaysAnArrival()
    {
        await using var rig = await AmiCaptureReplay.ReplayRig.StartAsync(new SessionOptions());

        var error = rig.Deliver(new NewChannelEvent
        {
            UniqueId = "reachable", Channel = "PJSIP/100-reachable",
            Linkedid = "reachable-linked",
        });

        error.Should().BeNull();
        rig.Manager.GetByLinkedId("reachable-linked").Should().NotBeNull();
        SessionIndexInvariant.Assert(rig.Manager);
    }
}
