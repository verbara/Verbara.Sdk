using System.Text;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Pure translator from a Push <c>TopicPath</c> into a NATS subject. The Push topic
/// hierarchy uses <c>.</c> as separator (per <see cref="Topics.TopicName"/>); some
/// callers express paths with <c>/</c>. Both are accepted. Empty segments are skipped,
/// and characters that are invalid in NATS subjects (spaces, wildcards, control chars)
/// are replaced with <c>_</c>.
/// </summary>
public static class NatsSubjectTranslator
{
    // NATS wildcards: '*' matches one token, '>' matches the tail. Also '.' is the
    // separator and spaces are forbidden. Control chars are rejected too.
    private const char InvalidCharReplacement = '_';

    /// <summary>
    /// Translate a Push topic path into a NATS subject string. The result is
    /// <paramref name="subjectPrefix"/> followed by a <c>.</c> and the sanitized path.
    /// </summary>
    /// <param name="topicPath">Push topic path. May use <c>.</c> or <c>/</c> as separator.</param>
    /// <param name="subjectPrefix">Prefix segment (e.g. <c>asterisk.sdk</c>).</param>
    /// <returns>A NATS-safe subject string.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="subjectPrefix"/> is null or empty.</exception>
    public static string ToNatsSubject(string topicPath, string subjectPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectPrefix);

        // Normalize the prefix itself: the caller can pass a multi-segment prefix like
        // "asterisk.sdk" — we sanitize each of its segments independently rather than
        // treating the whole thing as a single segment (which would turn '.' into '_').
        var prefix = NormalizeMultiSegment(subjectPrefix.Trim().TrimEnd('.'));
        if (string.IsNullOrEmpty(topicPath))
            return prefix;

        var sb = new StringBuilder(capacity: prefix.Length + topicPath.Length + 8);
        sb.Append(prefix);

        // Split on both '.' and '/' so callers using either convention are handled.
        var segments = topicPath.Split(['.', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in segments)
        {
            var sanitized = SanitizeSegment(raw);
            if (sanitized.Length == 0)
                continue;
            sb.Append('.').Append(sanitized);
        }

        return sb.ToString();
    }

    private static string NormalizeMultiSegment(string multiSegment)
    {
        if (string.IsNullOrEmpty(multiSegment))
            return string.Empty;

        var parts = multiSegment.Split(['.', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(capacity: multiSegment.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var sanitized = SanitizeSegment(parts[i]);
            if (sanitized.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(sanitized);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sanitize a single segment: replace invalid NATS characters (spaces, wildcards,
    /// separators, control chars) with an underscore. A segment with nothing but
    /// invalid characters collapses to an underscore run (callers treat empty segments
    /// as skipped at the split layer).
    /// </summary>
    private static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return string.Empty;

        StringBuilder? sb = null;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (IsInvalid(c))
            {
                sb ??= new StringBuilder(segment.Length).Append(segment, 0, i);
                sb.Append(InvalidCharReplacement);
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? segment;
    }

    private static bool IsInvalid(char c)
    {
        if (char.IsWhiteSpace(c)) return true;
        if (char.IsControl(c)) return true;
        return c switch
        {
            '*' or '>' or '.' or '/' or '\0' => true,
            _ => false,
        };
    }
}
