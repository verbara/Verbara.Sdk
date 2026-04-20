using Asterisk.Sdk.Cluster.Primitives;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Cluster.Primitives.Tests;

public sealed class NodeInfoTests
{
    [Fact]
    public void Defaults_ShouldBeUsableImmediately()
    {
        var node = new NodeInfo("node-1", NodeState.Healthy);

        node.NodeId.Should().Be("node-1");
        node.State.Should().Be(NodeState.Healthy);
        node.OwnerInstanceId.Should().BeNull();
        node.Generation.Should().Be(0);
        node.Weight.Should().Be(1.0);
        node.PriorityTier.Should().Be(0);
        node.MaxCapacity.Should().Be(500);
        node.Tags.Should().BeNull();
    }

    [Fact]
    public void WithExpression_ShouldUpdateStateWithoutMutation()
    {
        var original = new NodeInfo("n", NodeState.Healthy);

        var updated = original with { State = NodeState.Draining };

        original.State.Should().Be(NodeState.Healthy, "records are immutable");
        updated.State.Should().Be(NodeState.Draining);
    }

    [Fact]
    public void Equality_ShouldHoldForIdenticalValues()
    {
        var a = new NodeInfo("n", NodeState.Healthy) { Weight = 2.0 };
        var b = new NodeInfo("n", NodeState.Healthy) { Weight = 2.0 };

        a.Should().Be(b);
    }
}
