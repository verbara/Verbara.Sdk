namespace Asterisk.Sdk.Push.Topics;

/// <summary>
/// Immutable topic subscription pattern. Supports:
/// <list type="bullet">
///   <item><description>Literal segments — match exactly (e.g. <c>queue.42.updated</c>).</description></item>
///   <item><description><c>*</c> — matches exactly one segment (single-level wildcard).</description></item>
///   <item><description><c>**</c> — matches zero or more trailing segments (multi-level wildcard, must be the final segment).</description></item>
///   <item><description><c>{self}</c> — resolved to the calling user's identifier at match time.</description></item>
/// </list>
/// </summary>
public readonly struct TopicPattern : IEquatable<TopicPattern>
{
    private const string SelfPlaceholder = "{self}";
    private const string SingleWildcard = "*";
    private const string MultiWildcard = "**";

    private readonly string _raw;
    private readonly string[] _segments;

    private TopicPattern(string raw, string[] segments)
    {
        _raw = raw;
        _segments = segments;
    }

    /// <summary>
    /// Parses a dot-separated pattern string into a <see cref="TopicPattern"/>.
    /// Allowed segment values: literal text, <c>*</c>, <c>**</c>, or <c>{self}</c>.
    /// </summary>
    /// <param name="pattern">The pattern string to parse.</param>
    /// <returns>A valid <see cref="TopicPattern"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="pattern"/> is null/empty, contains an empty segment,
    /// <c>**</c> is not the final segment, or a segment mixes literal text with wildcards.
    /// </exception>
    public static TopicPattern Parse(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("Topic pattern must not be null or empty.", nameof(pattern));

        var segments = pattern.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];

            if (seg.Length == 0)
                throw new ArgumentException(
                    "Topic pattern must not contain empty segments (e.g. 'queue..event').",
                    nameof(pattern));

            if (seg == MultiWildcard)
            {
                // ** is only valid as the last segment
                if (i != segments.Length - 1)
                    throw new ArgumentException(
                        "The '**' multi-level wildcard must be the last segment in the pattern.",
                        nameof(pattern));
                continue;
            }

            if (seg == SingleWildcard || seg == SelfPlaceholder)
                continue;

            // Reject mixed segments such as "a*b" or "foo{self}"
            if (seg.Contains('*') || seg.Contains('{') || seg.Contains('}'))
                throw new ArgumentException(
                    $"Invalid segment '{seg}': wildcards and placeholders must occupy a full segment.",
                    nameof(pattern));
        }

        return new TopicPattern(pattern, segments);
    }

    /// <summary>
    /// Determines whether this pattern matches the given <see cref="TopicName"/>.
    /// </summary>
    /// <param name="topic">The concrete topic to test against.</param>
    /// <param name="selfUserId">
    /// The current user's identifier used to resolve <c>{self}</c> segments.
    /// Pass <see langword="null"/> when no user context is available — <c>{self}</c>
    /// segments will never match in that case.
    /// </param>
    /// <returns><see langword="true"/> when the pattern matches the topic.</returns>
    public bool Matches(TopicName topic, string? selfUserId = null)
    {
        var topicSegments = topic.Segments;

        for (var i = 0; i < _segments.Length; i++)
        {
            var seg = _segments[i];

            if (seg == MultiWildcard)
            {
                // ** must be last; matches zero or more remaining topic segments
                return true;
            }

            if (i >= topicSegments.Count)
                return false;

            if (seg == SingleWildcard)
                continue;

            if (seg == SelfPlaceholder)
            {
                if (selfUserId is null)
                    return false;
                if (!string.Equals(topicSegments[i], selfUserId, StringComparison.Ordinal))
                    return false;
                continue;
            }

            if (!string.Equals(seg, topicSegments[i], StringComparison.Ordinal))
                return false;
        }

        // All pattern segments consumed — topic must also be fully consumed.
        return _segments.Length == topicSegments.Count;
    }

    /// <summary>
    /// Attempts to resolve the pattern into a concrete <see cref="TopicName"/> by substituting
    /// <c>{self}</c> with <paramref name="selfUserId"/>.
    /// Returns <see langword="null"/> when the pattern contains unresolvable wildcards (<c>*</c> or <c>**</c>),
    /// or when <c>{self}</c> is present but <paramref name="selfUserId"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="selfUserId">User identifier used to resolve <c>{self}</c>.</param>
    /// <returns>A concrete <see cref="TopicName"/>, or <see langword="null"/>.</returns>
    public TopicName? Resolve(string? selfUserId)
    {
        var resolvedSegments = new string[_segments.Length];

        for (var i = 0; i < _segments.Length; i++)
        {
            var seg = _segments[i];

            if (seg == SingleWildcard || seg == MultiWildcard)
                return null;

            if (seg == SelfPlaceholder)
            {
                if (selfUserId is null)
                    return null;
                resolvedSegments[i] = selfUserId;
                continue;
            }

            resolvedSegments[i] = seg;
        }

        return TopicName.Parse(string.Join('.', resolvedSegments));
    }

    /// <summary>
    /// Returns a new <see cref="TopicPattern"/> with every <c>{self}</c> segment replaced
    /// by <paramref name="selfId"/>. Wildcard segments are preserved as-is.
    /// </summary>
    /// <param name="selfId">The identifier to substitute for <c>{self}</c>.</param>
    /// <returns>A new <see cref="TopicPattern"/> with <c>{self}</c> resolved.</returns>
    public TopicPattern ResolveSelf(string selfId)
    {
        var resolvedSegments = new string[_segments.Length];
        for (var i = 0; i < _segments.Length; i++)
            resolvedSegments[i] = _segments[i] == SelfPlaceholder ? selfId : _segments[i];

        var resolvedRaw = string.Join('.', resolvedSegments);
        return new TopicPattern(resolvedRaw, resolvedSegments);
    }

    /// <summary>Returns the original pattern string.</summary>
    public override string ToString() => _raw;

    /// <inheritdoc/>
    public bool Equals(TopicPattern other) => string.Equals(_raw, other._raw, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TopicPattern other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _raw is null ? 0 : StringComparer.Ordinal.GetHashCode(_raw);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TopicPattern left, TopicPattern right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TopicPattern left, TopicPattern right) => !left.Equals(right);
}
