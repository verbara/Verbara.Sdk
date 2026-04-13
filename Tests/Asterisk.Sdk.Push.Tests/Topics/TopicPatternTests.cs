using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Tests.Topics;

public sealed class TopicPatternTests
{
    // ── Parse: valid patterns ──────────────────────────────────────────────

    [Fact]
    public void Parse_ShouldSucceed_WhenLiteralPattern()
    {
        var pattern = TopicPattern.Parse("queue.42.conversation.updated");
        pattern.ToString().Should().Be("queue.42.conversation.updated");
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenSingleWildcard()
    {
        var pattern = TopicPattern.Parse("queue.*.conversation.updated");
        pattern.ToString().Should().Be("queue.*.conversation.updated");
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenMultiLevelWildcard()
    {
        var pattern = TopicPattern.Parse("queue.**");
        pattern.ToString().Should().Be("queue.**");
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenSelfPlaceholder()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        pattern.ToString().Should().Be("agent.{self}.state.changed");
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenSingleSegment()
    {
        var pattern = TopicPattern.Parse("*");
        pattern.ToString().Should().Be("*");
    }

    [Fact]
    public void Parse_ShouldSucceed_WhenGlobalWildcard()
    {
        var pattern = TopicPattern.Parse("**");
        pattern.ToString().Should().Be("**");
    }

    // ── Parse: invalid patterns ────────────────────────────────────────────

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenNull()
    {
        var act = () => TopicPattern.Parse(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenEmpty()
    {
        var act = () => TopicPattern.Parse("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenEmptySegment()
    {
        var act = () => TopicPattern.Parse("queue..event");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenDoubleStarNotAlone()
    {
        var act = () => TopicPattern.Parse("queue.**.extra");
        act.Should().Throw<ArgumentException>().WithMessage("*'**'*");
    }

    [Fact]
    public void Parse_ShouldThrowArgumentException_WhenMixedWildcardInSegment()
    {
        var act = () => TopicPattern.Parse("queue.a*b.event");
        act.Should().Throw<ArgumentException>().WithMessage("*segment*");
    }

    // ── Matches: literal ──────────────────────────────────────────────────

    [Fact]
    public void Matches_ShouldReturnTrue_WhenLiteralPatternEqualsTopicName()
    {
        var pattern = TopicPattern.Parse("queue.42.conversation.updated");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnFalse_WhenLiteralPatternDiffers()
    {
        var pattern = TopicPattern.Parse("queue.42.conversation.updated");
        var topic = TopicName.Parse("queue.99.conversation.updated");
        pattern.Matches(topic).Should().BeFalse();
    }

    // ── Matches: * single-segment wildcard ────────────────────────────────

    [Fact]
    public void Matches_ShouldReturnTrue_WhenSingleWildcardMatchesOneSegment()
    {
        var pattern = TopicPattern.Parse("queue.*.conversation.updated");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnFalse_WhenSingleWildcardSegmentCountDiffers()
    {
        var pattern = TopicPattern.Parse("queue.*.updated");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeFalse();
    }

    [Fact]
    public void Matches_ShouldReturnTrue_WhenMultipleWildcards()
    {
        var pattern = TopicPattern.Parse("*.*.conversation.*");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeTrue();
    }

    // ── Matches: ** multi-level wildcard ──────────────────────────────────

    [Fact]
    public void Matches_ShouldReturnTrue_WhenDoubleStarMatchesMultipleSegments()
    {
        var pattern = TopicPattern.Parse("queue.**");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnTrue_WhenDoubleStarMatchesSingleSegment()
    {
        var pattern = TopicPattern.Parse("queue.**");
        var topic = TopicName.Parse("queue.42");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnTrue_WhenDoubleStarMatchesZeroRemainingSegments()
    {
        var pattern = TopicPattern.Parse("queue.**");
        var topic = TopicName.Parse("queue");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnTrue_WhenStandaloneDoubleStarMatchesAnything()
    {
        var pattern = TopicPattern.Parse("**");
        var topic = TopicName.Parse("any.deep.topic.path");
        pattern.Matches(topic).Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnFalse_WhenDoubleStarPrefixDoesNotMatch()
    {
        var pattern = TopicPattern.Parse("agent.**");
        var topic = TopicName.Parse("queue.42.conversation.updated");
        pattern.Matches(topic).Should().BeFalse();
    }

    // ── Matches: {self} placeholder ───────────────────────────────────────

    [Fact]
    public void Matches_ShouldReturnTrue_WhenSelfResolvesToCorrectUserId()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        var topic = TopicName.Parse("agent.user-99.state.changed");
        pattern.Matches(topic, selfUserId: "user-99").Should().BeTrue();
    }

    [Fact]
    public void Matches_ShouldReturnFalse_WhenSelfResolvesToDifferentUserId()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        var topic = TopicName.Parse("agent.user-99.state.changed");
        pattern.Matches(topic, selfUserId: "user-42").Should().BeFalse();
    }

    [Fact]
    public void Matches_ShouldReturnFalse_WhenSelfPlaceholderButNoUserIdProvided()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        var topic = TopicName.Parse("agent.user-99.state.changed");
        pattern.Matches(topic, selfUserId: null).Should().BeFalse();
    }

    // ── Resolve: {self} substitution ──────────────────────────────────────

    [Fact]
    public void Resolve_ShouldReturnTopicName_WhenPatternIsLiteral()
    {
        var pattern = TopicPattern.Parse("queue.42.conversation.updated");
        var result = pattern.Resolve(selfUserId: null);
        result.Should().Be(TopicName.Parse("queue.42.conversation.updated"));
    }

    [Fact]
    public void Resolve_ShouldSubstituteSelf_WhenUserIdProvided()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        var result = pattern.Resolve(selfUserId: "user-99");
        result.Should().Be(TopicName.Parse("agent.user-99.state.changed"));
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenPatternContainsWildcards()
    {
        var pattern = TopicPattern.Parse("queue.*.updated");
        var result = pattern.Resolve(selfUserId: null);
        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenSelfNotProvided()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state");
        var result = pattern.Resolve(selfUserId: null);
        result.Should().BeNull();
    }

    // ── ResolveSelf ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveSelf_ShouldReplaceSelfPlaceholder_WhenCalled()
    {
        var pattern = TopicPattern.Parse("agent.{self}.state.changed");
        var resolved = pattern.ResolveSelf("user-99");
        resolved.ToString().Should().Be("agent.user-99.state.changed");
    }

    [Fact]
    public void ResolveSelf_ShouldPreserveWildcards_WhenPatternContainsThem()
    {
        var pattern = TopicPattern.Parse("agent.{self}.**");
        var resolved = pattern.ResolveSelf("user-42");
        resolved.ToString().Should().Be("agent.user-42.**");
    }

    [Fact]
    public void ResolveSelf_ShouldReturnEquivalentPattern_WhenNoSelfPlaceholder()
    {
        var pattern = TopicPattern.Parse("queue.*.updated");
        var resolved = pattern.ResolveSelf("user-99");
        resolved.ToString().Should().Be("queue.*.updated");
    }

    [Fact]
    public void ResolveSelf_ShouldMatchCorrectly_AfterResolution()
    {
        var pattern = TopicPattern.Parse("agent.{self}.**");
        var resolved = pattern.ResolveSelf("user-99");
        var topic = TopicName.Parse("agent.user-99.state.changed");
        resolved.Matches(topic).Should().BeTrue();
    }

    // ── Equality ──────────────────────────────────────────────────────────

    [Fact]
    public void Equals_ShouldBeTrue_WhenSamePattern()
    {
        var a = TopicPattern.Parse("queue.*.updated");
        var b = TopicPattern.Parse("queue.*.updated");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_ShouldBeFalse_WhenDifferentPattern()
    {
        var a = TopicPattern.Parse("queue.*.updated");
        var b = TopicPattern.Parse("queue.**");
        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }
}
