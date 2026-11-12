using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// <c>QueueMemberPaused</c> is the name Asterisk 11 and earlier gave the pause event; no supported version sends it, and
/// from 2.10.0 it has no typed class. The server keeps no arm for it: the frame reaches the server as a base event and
/// changes no member's pause state. <c>QueueMemberPause</c>, the name every supported version sends, still does.
/// </summary>
/// <remarks>
/// A real connection reads the frame from an in-memory peer, so the server's observer gets exactly what the generated
/// deserializer makes of it. The peer then writes a <c>QueueMemberStatus</c> for the same member; events reach the
/// server in the order they were read, so once its status change is announced, the frame before it has been handled.
/// </remarks>
public sealed class RemovedQueueMemberPausedTests
{
    [Fact]
    public async Task Server_ShouldLeaveThePauseStateUnchanged_WhenQueueMemberPausedArrives()
    {
        var member = await PauseThroughAsync("QueueMemberPaused");

        member.Should().BeFalse("no supported Asterisk sends QueueMemberPaused, and the server keeps no arm for it");
    }

    [Fact]
    public async Task Server_ShouldPauseTheMember_WhenQueueMemberPauseArrivesWithTheSameHeaders()
    {
        var member = await PauseThroughAsync("QueueMemberPause");

        member.Should().BeTrue("QueueMemberPause is the pause event every supported Asterisk sends");
    }

    /// <summary>Writes <paramref name="eventName"/> pausing the loaded member, then a status change; returns its pause state.</summary>
    private static async Task<bool> PauseThroughAsync(string eventName)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var queues = run.Server.Queues;
        var pausedAtStart = queues.GetByName(BootingAsterisk.QueueName)?.Members.GetValueOrDefault(BootingAsterisk.MemberLocation)?.Paused;
        var statusChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queues.MemberStatusChanged += (_, m) =>
        {
            if (m.Interface == BootingAsterisk.MemberLocation && m.Status == QueueMemberState.DeviceInUse)
                statusChanged.TrySetResult();
        };

        (await peer.SendEventAsync(eventName,
        [
            new("Privilege", "agent,all"), new("Queue", BootingAsterisk.QueueName),
            new("Location", BootingAsterisk.MemberLocation), new("MemberName", "agent"), new("Paused", "1"),
            new("Reason", "lunch"),
        ])).Should().BeTrue();
        (await peer.SendEventAsync("QueueMemberStatus",
        [
            new("Privilege", "agent,all"), new("Queue", BootingAsterisk.QueueName),
            new("Interface", BootingAsterisk.MemberLocation), new("Location", BootingAsterisk.MemberLocation),
            new("Status", "2"),
        ])).Should().BeTrue();
        await statusChanged.Task.WaitAsync(Run.Bound);

        var after = queues.GetByName(BootingAsterisk.QueueName)?.Members.GetValueOrDefault(BootingAsterisk.MemberLocation);
        using (new AssertionScope())
        {
            pausedAtStart.Should().BeFalse("the server loaded the peer's queue member at start, not paused");
            after.Should().NotBeNull();
        }

        return after?.Paused ?? false;
    }
}
