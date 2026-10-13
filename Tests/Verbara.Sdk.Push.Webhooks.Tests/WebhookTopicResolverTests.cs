using FluentAssertions;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// The per-event name resolution on its own, the same table as the SSE stream's: a topic path that parses is
/// matched; a non-empty one that does not parse throws and never falls back to the event type; a null or empty
/// topic path is replaced by the event type, and a type that is not a topic reaches only <c>**</c>.
/// </summary>
public sealed class WebhookTopicResolverTests
{
    private static readonly TopicPattern CatchAll = TopicPattern.Parse("**");
    private static readonly TopicPattern Billing = TopicPattern.Parse("billing.**");
    private static readonly TopicPattern Queue = TopicPattern.Parse("queue.**");
    private static readonly TopicPattern Self = TopicPattern.Parse("agent.{self}.**");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_ShouldMatchTheEventType_WhenTheTopicPathIsNullOrEmpty(string? topicPath)
    {
        var name = WebhookTopicResolver.Resolve(Event("billing.invoice.created", topicPath));

        name.IsMatchedBy(Billing, "u1").Should().BeTrue("the type billing.invoice.created matches billing.**");
        name.IsMatchedBy(CatchAll, "u1").Should().BeTrue("the type billing.invoice.created matches **");
        name.IsMatchedBy(Queue, "u1").Should().BeFalse("the type does not match queue.**");
    }

    [Fact]
    public void Resolve_ShouldTreatAWhitespaceTopicPathAsATopic_WhenItIsNotEmpty()
    {
        var name = WebhookTopicResolver.Resolve(Event("billing.invoice.created", " "));

        name.IsMatchedBy(CatchAll, "u1").Should().BeTrue("a blank path is one segment, which ** matches");
        name.IsMatchedBy(Billing, "u1").Should().BeFalse("a blank path is matched as itself, never by the event type");
    }

    [Fact]
    public void Resolve_ShouldMatchTheTopicPath_WhenItParses()
    {
        var name = WebhookTopicResolver.Resolve(Event("queue.updated", "billing.invoice.created"));

        name.IsMatchedBy(Billing, "u1").Should().BeTrue("the topic path billing.invoice.created matches billing.**");
        name.IsMatchedBy(Queue, "u1").Should().BeFalse("the topic path is matched, not the type queue.updated");
    }

    [Theory]
    [InlineData("billing..x")]
    [InlineData(".gap")]
    [InlineData("billing.*")]
    [InlineData("agent.{self}")]
    public void Resolve_ShouldMatchOnlyTheCatchAll_WhenATopicLessEventsTypeDoesNotParse(string eventType)
    {
        var name = WebhookTopicResolver.Resolve(Event(eventType, topicPath: null));

        name.IsMatchedBy(CatchAll, "u1").Should().BeTrue("a type that is not a topic reaches ** subscriptions");
        name.IsMatchedBy(Billing, "u1").Should().BeFalse("a type that is not a topic is never matched by its text");
        name.IsMatchedBy(TopicPattern.Parse("*"), "u1").Should().BeFalse("only the catch-all ** receives it, not *");
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("billing.*")]
    public void Resolve_ShouldThrow_WhenANonEmptyTopicPathDoesNotParse(string topicPath)
    {
        var resolve = () => WebhookTopicResolver.Resolve(Event("billing.invoice.created", topicPath));

        resolve.Should().Throw<ArgumentException>("a bad path is a dispatch failure and never falls back to the parseable type");
    }

    [Fact]
    public void Resolve_ShouldResolveTheSelfPlaceholderAgainstTheEventType_WhenTheTopicPathIsNull()
    {
        WebhookTopicResolver.Resolve(Event("agent.u1.state", null)).IsMatchedBy(Self, "u1").Should().BeTrue();
        WebhookTopicResolver.Resolve(Event("agent.u2.state", null)).IsMatchedBy(Self, "u1").Should().BeFalse();
        WebhookTopicResolver.Resolve(Event("agent.u1.state", null)).IsMatchedBy(Self, selfUserId: null)
            .Should().BeFalse("{self} never matches without a user");
    }

    [Fact]
    public void Resolve_ShouldMatchByEventType_WhenTheEventHasNoMetadata()
    {
        var name = WebhookTopicResolver.Resolve(new RoutedEvent("billing.invoice.created"));

        name.IsMatchedBy(Billing, selfUserId: null).Should().BeTrue("an event without metadata has no topic path");
        name.IsMatchedBy(Queue, selfUserId: null).Should().BeFalse();
    }

    private static RoutedEvent Event(string eventType, string? topicPath) =>
        new(eventType)
        {
            Metadata = new PushEventMetadata("T1", null, DateTimeOffset.UnixEpoch, null, topicPath),
        };
}
