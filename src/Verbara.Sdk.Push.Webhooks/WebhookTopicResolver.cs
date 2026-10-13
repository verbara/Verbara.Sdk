using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// The name one event is matched by, computed once per event: a topic the subscriptions' patterns are matched
/// against, or — for an event with no topic path whose event type is not a topic either — no name, which only a
/// catch-all <c>**</c> subscription receives.
/// </summary>
internal readonly struct WebhookEventName
{
    private readonly TopicName _topic;
    private readonly bool _hasTopic;

    private WebhookEventName(TopicName topic)
    {
        _topic = topic;
        _hasTopic = true;
    }

    /// <summary>An event matched by <paramref name="topic"/>.</summary>
    public static WebhookEventName Of(TopicName topic) => new(topic);

    /// <summary>An event without a topic name: only a catch-all <c>**</c> subscription receives it.</summary>
    public static WebhookEventName CatchAllOnly => default;

    /// <summary>Whether a subscription with <paramref name="pattern"/> receives the event.</summary>
    /// <param name="pattern">The subscription's topic pattern.</param>
    /// <param name="selfUserId">The event's user, which a <c>{self}</c> placeholder resolves to.</param>
    public bool IsMatchedBy(TopicPattern pattern, string? selfUserId) =>
        _hasTopic
            ? pattern.Matches(_topic, selfUserId)
            : string.Equals(pattern.ToString(), WebhookTopicResolver.CatchAllPattern, StringComparison.Ordinal);
}

/// <summary>
/// Names an event for webhook matching with the same rule the SSE stream applies:
/// <list type="bullet">
/// <item>a non-empty topic path is parsed with <see cref="TopicName.Parse"/> and decides alone — a path that does
/// not parse throws, and the dispatch logs it as a failure; it never falls back to the event type;</item>
/// <item>a null or empty topic path is replaced by the event type: a type that parses is matched like a topic
/// (a <c>{self}</c> placeholder resolving to the event's user), and one that does not reaches only subscriptions
/// whose pattern is the catch-all <c>**</c>.</item>
/// </list>
/// </summary>
internal static class WebhookTopicResolver
{
    /// <summary>The pattern text that receives an event whose name is not a topic.</summary>
    internal const string CatchAllPattern = "**";

    /// <summary>Names <paramref name="evt"/> for matching.</summary>
    /// <exception cref="ArgumentException">The event's topic path is non-empty and not a topic.</exception>
    public static WebhookEventName Resolve(PushEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var topicPath = evt.Metadata?.TopicPath;
        if (!string.IsNullOrEmpty(topicPath))
            return WebhookEventName.Of(TopicName.Parse(topicPath));

        return TryParse(evt.EventType, out var typeAsTopic)
            ? WebhookEventName.Of(typeAsTopic)
            : WebhookEventName.CatchAllOnly;
    }

    private static bool TryParse(string? name, out TopicName topic)
    {
        topic = default;
        if (string.IsNullOrEmpty(name))
            return false;

        try
        {
            topic = TopicName.Parse(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
