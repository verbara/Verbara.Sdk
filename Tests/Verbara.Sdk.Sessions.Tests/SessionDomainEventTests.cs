using System.Reflection;
using Verbara.Sdk.Sessions;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Sessions.Tests;

public sealed class SessionDomainEventTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    // These tests name CallFailedEvent on purpose: they hold its mark and its shape until 2.12.0 removes it.
#pragma warning disable VSDK0009
    [Fact]
    public void CallFailedEvent_ShouldBeAWarningUnderVsdk0009NamingCallEndedEventAndItsRemoval_WhenRead()
    {
        var mark = typeof(CallFailedEvent).GetCustomAttribute<ObsoleteAttribute>(inherit: false);

        mark.Should().NotBeNull("the SDK never raises CallFailedEvent: every ending, a failed one included, is a CallEndedEvent");
        mark!.DiagnosticId.Should().Be("VSDK0009");
        mark.IsError.Should().BeFalse("a consumer that names it keeps compiling until 2.12.0 removes it");
        mark.Message.Should().Contain("CallEndedEvent").And.EndWith("Removed in Verbara.Sdk 2.12.0.");
        mark.UrlFormat.Should().Be("https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0009");
    }

    [Fact]
    public void CallFailedEvent_ShouldExposeAllProperties()
    {
        var evt = new CallFailedEvent("s1", "srv1", Ts, "No route");

        evt.SessionId.Should().Be("s1");
        evt.ServerId.Should().Be("srv1");
        evt.Timestamp.Should().Be(Ts);
        evt.Reason.Should().Be("No route");
    }

    [Fact]
    public void CallFailedEvent_ShouldSupportRecordEquality()
    {
        var a = new CallFailedEvent("s1", "srv1", Ts, "reason");
        var b = new CallFailedEvent("s1", "srv1", Ts, "reason");

        a.Should().Be(b);
    }
#pragma warning restore VSDK0009

    [Fact]
    public void CallEndedEvent_ShouldCarryNoFinalState_WhenBuiltWithThePositionalConstructorAlone()
    {
        var at = DateTimeOffset.UnixEpoch;
        var positional = new CallEndedEvent("s1", "srv-1", at, HangupCause.NormalClearing, TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(4));
        var completed = positional with { FinalState = CallSessionState.Completed };
        var failed = positional with { FinalState = CallSessionState.Failed };

        (string, string, DateTimeOffset, HangupCause?, TimeSpan, TimeSpan?) positionalMembers =
            ("s1", "srv-1", at, HangupCause.NormalClearing, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4));

        using var scope = new AssertionScope();
        positional.FinalState.Should().BeNull("the positional constructor alone sets no final state");
        completed.FinalState.Should().Be(CallSessionState.Completed, "the final state is set with its init accessor");
        positional.Should().NotBe(completed, "two endings that differ only by the final state are not equal");
        completed.Should().NotBe(failed, "two endings that differ only by the final state are not equal");
        completed.Should().Be(positional with { FinalState = CallSessionState.Completed },
            "the final state is part of the value");
        Deconstructed(completed).Should().Be(positionalMembers,
            "the final state is outside the positional members: Deconstruct is as before");
    }

    private static (string, string, DateTimeOffset, HangupCause?, TimeSpan, TimeSpan?) Deconstructed(CallEndedEvent e)
    {
        var (sessionId, serverId, timestamp, cause, duration, talkTime) = e;
        return (sessionId, serverId, timestamp, cause, duration, talkTime);
    }

    [Fact]
    public void CallRingNoAnswerEvent_ShouldExposeAllProperties()
    {
        var evt = new CallRingNoAnswerEvent("s2", "srv2", Ts, "agent-1", "sales");

        evt.SessionId.Should().Be("s2");
        evt.AgentId.Should().Be("agent-1");
        evt.QueueName.Should().Be("sales");
    }

    [Fact]
    public void CallRingNoAnswerEvent_ShouldAllowNullQueueName()
    {
        var evt = new CallRingNoAnswerEvent("s2", "srv2", Ts, "agent-1", null);

        evt.QueueName.Should().BeNull();
    }

    [Fact]
    public void CallWrapUpEvent_ShouldExposeAllProperties()
    {
        var duration = TimeSpan.FromSeconds(30);
        var evt = new CallWrapUpEvent("s3", "srv3", Ts, "agent-2", "support", duration);

        evt.SessionId.Should().Be("s3");
        evt.AgentId.Should().Be("agent-2");
        evt.QueueName.Should().Be("support");
        evt.WrapUpDuration.Should().Be(duration);
    }

    [Fact]
    public void SessionMergedEvent_ShouldExposeAllProperties()
    {
        var evt = new SessionMergedEvent("s4", "srv4", Ts, "merged-session-id");

        evt.SessionId.Should().Be("s4");
        evt.MergedSessionId.Should().Be("merged-session-id");
    }

    [Fact]
    public void SessionMergedEvent_ShouldInheritFromSessionDomainEvent()
    {
        var evt = new SessionMergedEvent("s1", "srv1", Ts, "m1");

        evt.Should().BeAssignableTo<SessionDomainEvent>();
    }
}
