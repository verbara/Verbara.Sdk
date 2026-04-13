using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Tests.Topics;

public sealed class TopicNameTests
{
    [Fact]
    public void Parse_ShouldSplitSegments_WhenValidTopic()
    {
        var topic = TopicName.Parse("queue.42.conversation.updated");
        topic.Segments.Should().Equal("queue", "42", "conversation", "updated");
        topic.ToString().Should().Be("queue.42.conversation.updated");
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenEmpty()
    {
        var act = () => TopicName.Parse("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenNull()
    {
        var act = () => TopicName.Parse(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenContainsWildcard()
    {
        var act = () => TopicName.Parse("queue.*.conversation.updated");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*wildcard*");
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenContainsPlaceholder()
    {
        var act = () => TopicName.Parse("agent.{self}.state.changed");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*placeholder*");
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenEmptySegment()
    {
        var act = () => TopicName.Parse("queue..conversation");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenSingleSegment()
    {
        var topic = TopicName.Parse("notification");
        topic.Segments.Should().Equal("notification");
    }

    [Fact]
    public void Equals_ShouldBeTrue_WhenSameSegments()
    {
        var a = TopicName.Parse("agent.123.state.changed");
        var b = TopicName.Parse("agent.123.state.changed");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_ShouldBeFalse_WhenDifferentSegments()
    {
        var a = TopicName.Parse("agent.123.state.changed");
        var b = TopicName.Parse("agent.456.state.changed");
        a.Should().NotBe(b);
    }
}
