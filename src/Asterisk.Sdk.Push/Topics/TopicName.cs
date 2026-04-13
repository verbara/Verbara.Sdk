namespace Asterisk.Sdk.Push.Topics;

/// <summary>
/// Immutable hierarchical topic identifier. Dot-separated segments.
/// Wildcards not allowed — use TopicPattern for subscriptions.
/// </summary>
public readonly struct TopicName : IEquatable<TopicName>
{
    private readonly string _raw;

    /// <summary>Gets the individual path segments split by '.'.</summary>
    public IReadOnlyList<string> Segments { get; }

    private TopicName(string raw, string[] segments)
    {
        _raw = raw;
        Segments = segments;
    }

    /// <summary>
    /// Parses a dot-separated topic string into a <see cref="TopicName"/>.
    /// </summary>
    /// <param name="topic">The topic string to parse.</param>
    /// <returns>A valid <see cref="TopicName"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="topic"/> is null/empty, contains an empty segment,
    /// contains a wildcard character, or contains a placeholder.
    /// </exception>
    public static TopicName Parse(string topic)
    {
        if (string.IsNullOrEmpty(topic))
            throw new ArgumentException("Topic name must not be null or empty.", nameof(topic));

        var segments = topic.Split('.');

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new ArgumentException("Topic name must not contain empty segments (e.g. 'queue..event').", nameof(topic));

            if (segment.Contains('*'))
                throw new ArgumentException(
                    "Topic name cannot contain wildcards; use TopicPattern instead.",
                    nameof(topic));

            if (segment.Contains('{') || segment.Contains('}'))
                throw new ArgumentException(
                    "Topic name cannot contain placeholders; use TopicPattern instead.",
                    nameof(topic));
        }

        return new TopicName(topic, segments);
    }

    /// <summary>Returns the original dot-separated topic string.</summary>
    public override string ToString() => _raw;

    /// <inheritdoc/>
    public bool Equals(TopicName other) => string.Equals(_raw, other._raw, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TopicName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _raw is null ? 0 : StringComparer.Ordinal.GetHashCode(_raw);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TopicName left, TopicName right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TopicName left, TopicName right) => !left.Equals(right);
}
