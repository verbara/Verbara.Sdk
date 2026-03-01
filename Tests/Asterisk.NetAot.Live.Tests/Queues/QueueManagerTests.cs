using Asterisk.NetAot.Live.Queues;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.NetAot.Live.Tests.Queues;

public class QueueManagerTests
{
    private readonly QueueManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnQueueParams_ShouldCreateQueue()
    {
        _sut.OnQueueParams("sales", 10, "ringall", 3, 15, 120, 500, 20);

        _sut.Queues.Should().HaveCount(1);
        var q = _sut.GetByName("sales");
        q.Should().NotBeNull();
        q!.Strategy.Should().Be("ringall");
        q.Completed.Should().Be(500);
    }

    [Fact]
    public void OnMemberAdded_ShouldAddMember()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);

        var q = _sut.GetByName("sales");
        q!.Members.Should().HaveCount(1);
        q.Members[0].Interface.Should().Be("PJSIP/agent01");
    }

    [Fact]
    public void OnMemberRemoved_ShouldRemoveMember()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberRemoved("sales", "PJSIP/agent01");

        _sut.GetByName("sales")!.Members.Should().BeEmpty();
    }

    [Fact]
    public void OnMemberPaused_ShouldUpdateState()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberPaused("sales", "PJSIP/agent01", true, "Break");

        var member = _sut.GetByName("sales")!.Members[0];
        member.Paused.Should().BeTrue();
        member.PausedReason.Should().Be("Break");
    }

    [Fact]
    public void OnCallerJoined_ShouldAddEntry()
    {
        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);

        var q = _sut.GetByName("sales");
        q!.Entries.Should().HaveCount(1);
        q.Entries[0].CallerId.Should().Be("5551234");
    }

    [Fact]
    public void OnCallerLeft_ShouldRemoveEntry()
    {
        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);
        _sut.OnCallerLeft("sales", "PJSIP/5551234-001");

        _sut.GetByName("sales")!.Entries.Should().BeEmpty();
    }
}
