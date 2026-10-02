namespace Verbara.Sdk.Push.AspNetCore.Tests;

/// <summary>
/// The per-event, per-stream name resolution on its own, apart from Kestrel: a topic path that parses is matched;
/// a non-empty one that does not parse is dropped on every stream and never falls back to the event type; a null
/// or empty topic path is replaced by the event type, and a type that is not a topic reaches only <c>**</c>.
/// </summary>
public sealed class SseTopicResolverTests
{
    private static readonly TopicPattern[] CatchAll = [TopicPattern.Parse("**")];
    private static readonly TopicPattern[] Billing = [TopicPattern.Parse("billing.**")];
    private static readonly TopicPattern[] Self = [TopicPattern.Parse("agent.{self}.**")];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_ShouldMatchTheEventType_WhenTheTopicPathIsNullOrEmpty(string? topicPath)
    {
        var evt = Event("billing.invoice.created", topicPath);

        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Deliver, "the type billing.invoice.created matches billing.**");
        SseTopicResolver.Resolve(evt, [TopicPattern.Parse("queue.**")], "u1").Should().Be(SseTopicMatch.Skip, "the type does not match queue.**");
    }

    [Fact]
    public void Resolve_ShouldTreatAWhitespaceTopicPathAsATopic_WhenItIsNotEmpty()
    {
        var evt = Event("billing.invoice.created", " ");

        SseTopicResolver.Resolve(evt, CatchAll, "u1").Should().Be(SseTopicMatch.Deliver, "a blank path is one segment, which ** matches");
        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Skip, "a blank path is matched as itself, never by the event type");
    }

    [Fact]
    public void Resolve_ShouldMatchTheTopicPath_WhenItParses()
    {
        var evt = Event("queue.updated", "billing.invoice.created");

        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Deliver);
        SseTopicResolver.Resolve(evt, [TopicPattern.Parse("queue.**")], "u1").Should().Be(SseTopicMatch.Skip, "the topic path is matched, not the type");
    }

    [Theory]
    [InlineData("billing..x")]
    [InlineData(".gap")]
    [InlineData("billing.*")]
    public void Resolve_ShouldDeliverATopicLessEventOnlyToCatchAll_WhenItsTypeDoesNotParse(string eventType)
    {
        var evt = Event(eventType, topicPath: null);

        SseTopicResolver.Resolve(evt, CatchAll, "u1").Should().Be(SseTopicMatch.Deliver, "a type that is not a topic reaches ** streams");
        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Skip, "a type that is not a topic is never matched by its text");
        SseTopicResolver.Resolve(evt, [.. Billing, .. CatchAll], "u1").Should().Be(SseTopicMatch.Deliver, "any allowed ** pattern is enough");
    }

    [Fact]
    public void Resolve_ShouldReportUnparseable_WhenANonEmptyTopicPathDoesNotParseEvenOnACatchAllStream()
    {
        var evt = Event("queue.updated", "a..b");

        SseTopicResolver.Resolve(evt, CatchAll, "u1").Should().Be(SseTopicMatch.Unparseable);
        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Unparseable);
    }

    [Fact]
    public void Resolve_ShouldNotFallBackToTheEventType_WhenTheTopicPathDoesNotParse()
    {
        var evt = Event("billing.invoice.created", "a..b");

        SseTopicResolver.Resolve(evt, Billing, "u1").Should().Be(SseTopicMatch.Unparseable, "the parseable billing type is not used in place of a bad path");
    }

    [Fact]
    public void Resolve_ShouldResolveTheSelfPlaceholderAgainstTheEventType_WhenTheTopicPathIsNull()
    {
        SseTopicResolver.Resolve(Event("agent.u1.state", null), Self, "u1").Should().Be(SseTopicMatch.Deliver);
        SseTopicResolver.Resolve(Event("agent.u2.state", null), Self, "u1").Should().Be(SseTopicMatch.Skip);
        SseTopicResolver.Resolve(Event("agent.u1.state", null), Self, userId: null).Should().Be(SseTopicMatch.Skip, "{self} never matches without a user");
    }

    [Fact]
    public void Resolve_ShouldSkip_WhenNoPatternIsAllowed()
    {
        SseTopicResolver.Resolve(Event("billing..x", null), [], "u1").Should().Be(SseTopicMatch.Skip);
        SseTopicResolver.Resolve(Event("queue.updated", "a..b"), [], "u1").Should().Be(SseTopicMatch.Skip);
    }

    private static TypedTestEvent Event(string eventType, string? topicPath) =>
        new(eventType)
        {
            Metadata = new PushEventMetadata("T1", null, DateTimeOffset.UnixEpoch, null, topicPath),
        };
}
