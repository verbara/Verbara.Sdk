using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Tests.Topics;

public sealed class TopicRegistryTests
{
    [Fact]
    public void Register_ShouldStoreTemplate_WhenNewEventType()
    {
        var registry = new TopicRegistry();
        registry.Register<TestAssignedEvent>("conversation.{0}.assigned");
        registry.TryGetTemplate(typeof(TestAssignedEvent), out var template).Should().BeTrue();
        template.Should().Be("conversation.{0}.assigned");
    }

    [Fact]
    public void Register_ShouldThrowInvalidOperationException_WhenDuplicateEventType()
    {
        var registry = new TopicRegistry();
        registry.Register<TestAssignedEvent>("conversation.{0}.assigned");
        var act = () => registry.Register<TestAssignedEvent>("conversation.{0}.updated");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryGetTemplate_ShouldReturnFalse_WhenNotRegistered()
    {
        var registry = new TopicRegistry();
        registry.TryGetTemplate(typeof(TestAssignedEvent), out _).Should().BeFalse();
    }

    [Fact]
    public void ResolveTopicName_ShouldFormatTemplate_WhenRegistered()
    {
        var registry = new TopicRegistry();
        registry.Register<TestAssignedEvent>("conversation.{0}.assigned");
        var topic = registry.ResolveTopicName(typeof(TestAssignedEvent), "42");
        topic.Should().NotBeNull();
        topic!.Value.ToString().Should().Be("conversation.42.assigned");
    }

    [Fact]
    public void ResolveTopicName_ShouldReturnNull_WhenNotRegistered()
    {
        var registry = new TopicRegistry();
        registry.ResolveTopicName(typeof(TestAssignedEvent), "42").Should().BeNull();
    }

    [Fact]
    public void ResolveTopicName_ShouldFormatMultiplePlaceholders_WhenProvided()
    {
        var registry = new TopicRegistry();
        registry.Register<TestAssignedEvent>("tenant.{0}.queue.{1}.event");
        var topic = registry.ResolveTopicName(typeof(TestAssignedEvent), "abc", "99");
        topic!.Value.ToString().Should().Be("tenant.abc.queue.99.event");
    }
}

file sealed record TestAssignedEvent : PushEvent
{
    public override string EventType => "test.assigned";
}
