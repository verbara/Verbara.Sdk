namespace Verbara.Sdk.Push.AspNetCore;

using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

/// <summary>What one SSE stream does with one event that passed the delivery filter.</summary>
internal enum SseTopicMatch
{
    /// <summary>An allowed pattern matches the event's name: the event is written to the stream.</summary>
    Deliver,

    /// <summary>No allowed pattern matches the event's name: the stream skips it.</summary>
    Skip,

    /// <summary>The event's non-empty topic path is not a topic: every stream drops it and it is reported.</summary>
    Unparseable,
}

/// <summary>
/// Decides, per event and per stream, whether the event's name matches the stream's allowed patterns. The name
/// is the event's topic path; an event whose topic path is null or empty is named by its event type instead, so
/// it reaches only the streams allowed to see that type. A pure function of the event, the allowed patterns and
/// the subscriber's user.
/// </summary>
internal static class SseTopicResolver
{
    /// <summary>
    /// Resolves an event against a stream's allowed patterns:
    /// <list type="bullet">
    /// <item>a non-empty topic path that parses is matched against the patterns; one that does not parse is
    /// <see cref="SseTopicMatch.Unparseable"/> on every stream, the catch-all one included, and never falls back to
    /// the event type;</item>
    /// <item>a null or empty topic path is replaced by the event type; a type that parses is matched like a topic,
    /// and one that does not is delivered only when an allowed pattern is the catch-all <c>**</c>.</item>
    /// </list>
    /// Parsing is <see cref="TopicName.Parse"/> alone: no name is matched by its raw text, and the reserved
    /// gap-marker name gets no special case here (it is escaped on the wire, not in matching).
    /// </summary>
    public static SseTopicMatch Resolve(PushEvent evt, IReadOnlyList<TopicPattern> allowedPatterns, string? userId)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(allowedPatterns);

        if (allowedPatterns.Count == 0)
            return SseTopicMatch.Skip;

        var topicPath = evt.Metadata?.TopicPath;
        if (!string.IsNullOrEmpty(topicPath))
        {
            if (!TryParse(topicPath, out var topic))
                return SseTopicMatch.Unparseable;

            return MatchesAny(topic, allowedPatterns, userId) ? SseTopicMatch.Deliver : SseTopicMatch.Skip;
        }

        if (TryParse(evt.EventType, out var typeAsTopic))
            return MatchesAny(typeAsTopic, allowedPatterns, userId) ? SseTopicMatch.Deliver : SseTopicMatch.Skip;

        return HasCatchAll(allowedPatterns) ? SseTopicMatch.Deliver : SseTopicMatch.Skip;
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

    private static bool MatchesAny(TopicName topic, IReadOnlyList<TopicPattern> patterns, string? userId)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Matches(topic, userId))
                return true;
        }

        return false;
    }

    private static bool HasCatchAll(IReadOnlyList<TopicPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.Equals(pattern.ToString(), SseAdmission.CatchAllTopic, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
