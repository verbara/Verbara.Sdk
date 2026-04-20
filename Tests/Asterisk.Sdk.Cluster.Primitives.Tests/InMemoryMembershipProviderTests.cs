using Asterisk.Sdk.Cluster.Primitives;
using Asterisk.Sdk.Cluster.Primitives.InMemory;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Cluster.Primitives.Tests;

public sealed class InMemoryMembershipProviderTests
{
    [Fact]
    public async Task RegisterNodeAsync_ShouldMakeNodeDiscoverable()
    {
        var members = new InMemoryMembershipProvider();
        var node = new NodeInfo("node-1", NodeState.Healthy) { OwnerInstanceId = "instance-A" };

        await members.RegisterNodeAsync(node);

        var all = await members.GetNodesAsync();
        all.Should().ContainSingle(n => n.NodeId == "node-1");
    }

    [Fact]
    public async Task UnregisterNodeAsync_ShouldRemoveNode()
    {
        var members = new InMemoryMembershipProvider();
        await members.RegisterNodeAsync(new NodeInfo("n", NodeState.Healthy));

        await members.UnregisterNodeAsync("n");

        (await members.GetNodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateNodeStateAsync_ShouldMutateTrackedState()
    {
        var members = new InMemoryMembershipProvider();
        await members.RegisterNodeAsync(new NodeInfo("n", NodeState.Healthy));

        await members.UpdateNodeStateAsync("n", NodeState.Draining);

        var node = (await members.GetNodesAsync()).Should().ContainSingle().Subject;
        node.State.Should().Be(NodeState.Draining);
    }

    [Fact]
    public async Task UpdateNodeStateAsync_ShouldBeNoOp_WhenNodeMissing()
    {
        var members = new InMemoryMembershipProvider();

        await members.UpdateNodeStateAsync("missing", NodeState.Unhealthy);

        (await members.GetNodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task HeartbeatAsync_ShouldMakeInstanceLive()
    {
        var time = new FakeTimeProvider();
        var members = new InMemoryMembershipProvider(time);

        await members.HeartbeatAsync("inst-A", TimeSpan.FromSeconds(10));

        var live = await members.GetLiveInstancesAsync();
        live.Should().ContainSingle().Which.Should().Be("inst-A");
    }

    [Fact]
    public async Task GetLiveInstancesAsync_ShouldExcludeExpired()
    {
        var time = new FakeTimeProvider();
        var members = new InMemoryMembershipProvider(time);

        await members.HeartbeatAsync("inst-A", TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));

        var live = await members.GetLiveInstancesAsync();
        live.Should().BeEmpty();
    }
}
