using Asterisk.Sdk.Push.Nats;

using FluentAssertions;

using Xunit;

namespace Asterisk.Sdk.Push.Nats.Tests;

public class NatsSubjectTranslatorTests
{
    [Fact]
    public void ToNatsSubject_ShouldJoinWithDots_WhenSeparatorIsDot()
    {
        var result = NatsSubjectTranslator.ToNatsSubject("a.b.c", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.a.b.c");
    }

    [Fact]
    public void ToNatsSubject_ShouldTranslateForwardSlashToDot_WhenSeparatorIsSlash()
    {
        var result = NatsSubjectTranslator.ToNatsSubject("push/channels/uniqueid-42", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.push.channels.uniqueid-42");
    }

    [Fact]
    public void ToNatsSubject_ShouldSkipEmptySegments_WhenTopicHasConsecutiveSeparators()
    {
        // Three forms of empty segments: leading, trailing, doubled.
        var result = NatsSubjectTranslator.ToNatsSubject("/push//channels/", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.push.channels");
    }

    [Fact]
    public void ToNatsSubject_ShouldReplaceSpaces_WithUnderscore()
    {
        var result = NatsSubjectTranslator.ToNatsSubject("queues/42/agent state", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.queues.42.agent_state");
    }

    [Fact]
    public void ToNatsSubject_ShouldReplaceWildcards_WithUnderscore()
    {
        // NATS subjects forbid '*' and '>' — translator must sanitize them to avoid silently
        // re-interpreting a literal path segment as a subscription wildcard downstream.
        var result = NatsSubjectTranslator.ToNatsSubject("calls.*.ended", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.calls._.ended");

        var tailResult = NatsSubjectTranslator.ToNatsSubject("calls.>.ended", "asterisk.sdk");
        tailResult.Should().Be("asterisk.sdk.calls._.ended");
    }

    [Fact]
    public void ToNatsSubject_ShouldPreserveCase_WhenTopicHasMixedCase()
    {
        var result = NatsSubjectTranslator.ToNatsSubject("Push.Channels.UniqueId-42", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.Push.Channels.UniqueId-42");
    }

    [Fact]
    public void ToNatsSubject_ShouldPreservePrefix_WhenTopicPathIsEmpty()
    {
        // Empty topic path still yields a valid subject — just the prefix. Callers can
        // rely on this for liveness probes or synthetic events with no TopicPath set.
        var result = NatsSubjectTranslator.ToNatsSubject(string.Empty, "asterisk.sdk");
        result.Should().Be("asterisk.sdk");
    }

    [Fact]
    public void ToNatsSubject_ShouldReplaceControlCharacters_WithUnderscore()
    {
        // Control chars (\0, \t, \n) are illegal in NATS subjects and must be sanitized.
        var result = NatsSubjectTranslator.ToNatsSubject("push.chan\tnel\nend", "asterisk.sdk");
        result.Should().Be("asterisk.sdk.push.chan_nel_end");
    }

    [Fact]
    public void ToNatsSubject_ShouldThrow_WhenSubjectPrefixIsNullOrWhitespace()
    {
        var act = () => NatsSubjectTranslator.ToNatsSubject("a.b.c", "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToNatsSubject_ShouldTrimTrailingDotOnPrefix_WhenPrefixHasTrailingSeparator()
    {
        // A sloppy prefix like "asterisk.sdk." should not produce "asterisk.sdk..push"
        // — the translator trims the trailing dot before joining.
        var result = NatsSubjectTranslator.ToNatsSubject("push", "asterisk.sdk.");
        result.Should().Be("asterisk.sdk.push");
    }
}
