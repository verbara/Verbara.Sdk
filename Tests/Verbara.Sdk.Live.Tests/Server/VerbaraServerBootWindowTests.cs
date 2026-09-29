using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A load of live state over a session that logged in while Asterisk was still loading its modules.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk accepts the AMI login before app_queue and app_agent_pool have registered <c>QueueStatus</c> and
/// <c>Agents</c>, and refuses each as <c>Invalid/unknown command</c> until they have; it reports the end of its start
/// with <c>FullyBooted</c>, and only to AMI users with <c>system</c> in their read permissions. The window, as
/// measured, is described on <see cref="BootingAsterisk"/>, the peer that plays it.
/// </para>
/// <para>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a
/// <see cref="Verbara.Sdk.Live.Server.VerbaraServer"/> through <see cref="Run"/>. The peer boots when the test says
/// so, the load's waits run on <see cref="Run.Clock"/>, and every await is bounded by <see cref="Run.Bound"/> and
/// ends on its signal.
/// </para>
/// </remarks>
public sealed class VerbaraServerBootWindowTests
{
    [Fact]
    public async Task StartAsync_ShouldLoadTheQueueItsMemberAndTheAgentAskingEachOnce_WhenAsteriskHasBootedAndTheUserReceivesFullyBooted()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, SendsFullyBooted = true };

        await using var run = await Run.StartAsync(peer);
        var loaded = await CompletesWithinBoundAsync(run.ServerLog.Logged("[LIVE] State loaded"));

        var queue = run.Server.Queues.GetByName(BootingAsterisk.QueueName);
        using (new AssertionScope())
        {
            loaded.Should().BeTrue("a start that returned has logged its load");
            run.Server.Queues.QueueCount.Should().Be(1, "Asterisk reports one queue");
            queue.Should().NotBeNull("the queue Asterisk reports is loaded");
            queue?.Strategy.Should().Be(BootingAsterisk.QueueStrategy, "the queue keeps the strategy Asterisk reports");
            queue?.MemberCount.Should().Be(1, "the queue keeps its one static member");
            run.Server.Agents.AgentCount.Should().Be(1, "Asterisk reports one agent");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().NotBeNull("the agent Asterisk reports is loaded");
            peer.Asked("Status").Should().Be(1, "the channels are asked once");
            peer.Asked("QueueStatus").Should().Be(1, "the queues are asked once");
            peer.Asked("Agents").Should().Be(1, "the agents are asked once");
            run.Clock.TimersCreated.TryRead(out _).Should().BeFalse("a load that was answered waits for nothing");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return false;
        }
    }
}
