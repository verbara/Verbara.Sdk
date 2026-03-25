using Asterisk.Sdk.Sessions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class SessionDomainEventTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

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
    public void CallFailedEvent_ShouldSupportRecordEquality()
    {
        var a = new CallFailedEvent("s1", "srv1", Ts, "reason");
        var b = new CallFailedEvent("s1", "srv1", Ts, "reason");

        a.Should().Be(b);
    }

    [Fact]
    public void SessionMergedEvent_ShouldInheritFromSessionDomainEvent()
    {
        var evt = new SessionMergedEvent("s1", "srv1", Ts, "m1");

        evt.Should().BeAssignableTo<SessionDomainEvent>();
    }
}
