using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Tests.Shared.Sessions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

internal static class UnjoinedLegBaseline
{
    public static async Task ReconcileAsync(RigServer server, params string[] ids)
    {
        server.StatusReply = token => Status(ids, token);
        await server.Server.ReconcileChannelsAsync().AsTask().WaitAsync(SweepRig.Bound);
    }
    private static async IAsyncEnumerable<ManagerEvent> Status(string[] ids, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            yield return new StatusEvent
            {
                EventType = "Status", UniqueId = id, Channel = "PJSIP/" + id, LinkedId = "L",
                RawFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Channel"] = "PJSIP/" + id, ["Uniqueid"] = id, ["Linkedid"] = "L",
                    ["ChannelState"] = "6", ["ChannelStateDesc"] = "Up",
                },
            };
        }
    }
    public static async Task<int> SweepAsync(PoolSweepRig rig, RigServer server)
    {
        rig.Clock.Advance(TimeSpan.FromHours(2));
        await new ServerSweep(rig.Manager, rig.Options, rig.Log.CreateLogger("UnjoinedLegBaseline"), rig.Clock)
            .SweepAsync(server.Server, server.Id, CancellationToken.None).WaitAsync(SweepRig.Bound);
        return (int)rig.Log.Entries.Last(e => e.Values.ContainsKey("Unverifiable")).Values["Unverifiable"]!;
    }
    public static CallSession Reconstructed(bool withX = false)
    {
        var call = new CallSession("reconstructed", "L", "server", CallDirection.Inbound);
        if (withX)
            call.AddParticipant(new SessionParticipant { UniqueId = "X", Channel = "PJSIP/X", Technology = "PJSIP", Role = ParticipantRole.Caller });
        return call;
    }
    public static void AssertEnded(PoolSweepRig rig, CallSession call, int unverifiable, bool reload)
    {
        new { Active = rig.Manager.ActiveSessions.Contains(call), Endings = rig.EndingsOf(call), Unverifiable = unverifiable,
            Cause = call.Metadata.GetValueOrDefault("cause"), Hangup = call.HangupCause }
            .Should().BeEquivalentTo(new { Active = false, Endings = 1, Unverifiable = 0,
                Cause = reload ? "reload" : null, Hangup = reload ? null : (HangupCause?)HangupCause.NormalClearing });
    }
    public static string Describe(PoolSweepRig rig, CallSession call, int unverifiable) =>
        $"state={call.State}; active={rig.Manager.ActiveSessions.Contains(call)}; participants=[{string.Join(',', call.Participants.Select(p => p.UniqueId))}]; endings={rig.EndingsOf(call)}; unverifiable={unverifiable}; cause={call.Metadata.GetValueOrDefault("cause") ?? "null"}; hangup={call.HangupCause?.ToString() ?? "null"}; {rig.Describe()}";
}

public sealed class ReconstructedLeglessEndingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OnHangup_ShouldEndTheReconstructedCallOnce_WhenAllOfItsUnjoinedChannelsHangUp()
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("server", attach: false);
        await UnjoinedLegBaseline.ReconcileAsync(server, "X", "Y");
        rig.Manager.AttachToServer(server.Server, "server");
        var call = UnjoinedLegBaseline.Reconstructed();
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
        var unverifiable = await UnjoinedLegBaseline.SweepAsync(rig, server);
        output.WriteLine(UnjoinedLegBaseline.Describe(rig, call, unverifiable));
        UnjoinedLegBaseline.AssertEnded(rig, call, unverifiable, reload: false);
    }
}

public sealed class DetachedMixedLegEndingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OnHangup_ShouldEndTheLeftCallOnce_WhenTheSurvivingLegHangsUpAfterReconciliation()
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("server");
        await UnjoinedLegBaseline.ReconcileAsync(server);
        server.Server.Channels.OnNewChannel("X", "PJSIP/X", ChannelState.Up, linkedId: "L");
        server.Server.Channels.OnNewChannel("Y", "PJSIP/Y", ChannelState.Up, linkedId: "L");
        var call = rig.Manager.GetByLinkedId("L")!;
        server.Server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
        server.Server.Bridges.OnChannelEntered("B", "X");
        server.Server.Bridges.OnChannelEntered("B", "Y");
        rig.Manager.DetachFromServer("server");
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        rig.Manager.AttachToServer(server.Server, "server");
        await UnjoinedLegBaseline.ReconcileAsync(server, "Y");
        call.Participants.Single(p => p.UniqueId == "X").LeftAt.Should().NotBeNull();
        call.Participants.Single(p => p.UniqueId == "X").HangupCause.Should().BeNull();
        call.Participants.Single(p => p.UniqueId == "Y").LeftAt.Should().BeNull();
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(1);
        rig.Manager.GetByChannelId("server", "X").Should().BeNull();
        rig.Manager.GetByChannelId("server", "Y").Should().BeSameAs(call);
        rig.Manager.GetByBridgeId("server", "B").Should().BeSameAs(call);
        rig.EndingsOf(call).Should().Be(0);
        SessionIndexInvariant.Assert(rig.Manager);
        await UnjoinedLegBaseline.ReconcileAsync(server, "Y");
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(1);
        server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
        var unverifiable = await UnjoinedLegBaseline.SweepAsync(rig, server);
        output.WriteLine(UnjoinedLegBaseline.Describe(rig, call, unverifiable));
        UnjoinedLegBaseline.AssertEnded(rig, call, unverifiable, reload: false);
        rig.Manager.GetByChannelId("server", "Y").Should().BeNull();
        rig.Manager.GetByBridgeId("server", "B").Should().BeNull();
        rig.Manager.InspectIndexes().ChannelIds.Should().NotContain(e => ReferenceEquals(e.Value, call));
        rig.Manager.InspectIndexes().Bridges.Should().NotContain(e => e.Value == call.SessionId);
        SessionIndexInvariant.Assert(rig.Manager);
    }
}

public sealed class ReconstructionRegistrationGapTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RegisterReconstructedSession_ShouldEndTheCallAtRegistration_WhenItsChannelsHungUpAfterTheRead()
    {
        await using var rig = new PoolSweepRig();
        var server = rig.AddServer("server", attach: false);
        await UnjoinedLegBaseline.ReconcileAsync(server, "X", "Y");
        rig.Manager.AttachToServer(server.Server, "server");
        var read = server.Server.Channels.ActiveChannels.ToArray();
        read.Select(c => c.UniqueId).Should().BeEquivalentTo(["X", "Y"]);
        server.Server.Channels.OnHangup("X", HangupCause.UserBusy);
        server.Server.Channels.OnHangup("Y", HangupCause.NormalClearing);
        var call = UnjoinedLegBaseline.Reconstructed();
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        var endingsAtRegistration = rig.EndingsOf(call);
        var unverifiable = await UnjoinedLegBaseline.SweepAsync(rig, server);
        output.WriteLine($"read=[{string.Join(',', read.Select(c => c.UniqueId))}]; endingsAtRegistration={endingsAtRegistration}; " + UnjoinedLegBaseline.Describe(rig, call, unverifiable));
        UnjoinedLegBaseline.AssertEnded(rig, call, unverifiable, reload: true);
        endingsAtRegistration.Should().Be(1);
    }
}
