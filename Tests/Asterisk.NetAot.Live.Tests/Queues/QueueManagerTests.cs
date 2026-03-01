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

        _sut.QueueCount.Should().Be(1);
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
        q!.MemberCount.Should().Be(1);
        q.Members["PJSIP/agent01"].Interface.Should().Be("PJSIP/agent01");
    }

    [Fact]
    public void OnMemberAdded_ShouldReplaceExisting()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01 Updated", 5, true, 2);

        var q = _sut.GetByName("sales");
        q!.MemberCount.Should().Be(1);
        q.Members["PJSIP/agent01"].Penalty.Should().Be(5);
        q.Members["PJSIP/agent01"].MemberName.Should().Be("Agent 01 Updated");
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

        var member = _sut.GetByName("sales")!.Members["PJSIP/agent01"];
        member.Paused.Should().BeTrue();
        member.PausedReason.Should().Be("Break");
    }

    [Fact]
    public void OnMemberStatusChanged_ShouldUpdateStatus()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberStatusChanged("sales", "PJSIP/agent01", (int)QueueMemberState.DeviceInUse);

        var member = _sut.GetByName("sales")!.Members["PJSIP/agent01"];
        member.Status.Should().Be(QueueMemberState.DeviceInUse);
    }

    [Fact]
    public void OnMemberStatusChanged_ShouldFireEvent()
    {
        AsteriskQueueMember? changed = null;
        _sut.MemberStatusChanged += (_, m) => changed = m;

        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberStatusChanged("sales", "PJSIP/agent01", (int)QueueMemberState.DeviceRinging);

        changed.Should().NotBeNull();
        changed!.Status.Should().Be(QueueMemberState.DeviceRinging);
    }

    [Fact]
    public void OnCallerJoined_ShouldAddEntry()
    {
        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);

        var q = _sut.GetByName("sales");
        q!.EntryCount.Should().Be(1);
        q.Entries["PJSIP/5551234-001"].CallerId.Should().Be("5551234");
    }

    [Fact]
    public void OnCallerLeft_ShouldRemoveEntry()
    {
        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);
        _sut.OnCallerLeft("sales", "PJSIP/5551234-001");

        _sut.GetByName("sales")!.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task OnMemberAdded_ConcurrentCalls_ShouldNotCorrupt()
    {
        const int concurrency = 100;
        var tasks = Enumerable.Range(0, concurrency).Select(i =>
            Task.Run(() => _sut.OnMemberAdded("sales", $"PJSIP/agent{i:D4}", $"Agent {i}", i, false, 1)));

        await Task.WhenAll(tasks);

        _sut.GetByName("sales")!.MemberCount.Should().Be(concurrency);
    }

    [Fact]
    public async Task OnCallerJoined_ConcurrentCalls_ShouldNotCorrupt()
    {
        const int concurrency = 100;
        var tasks = Enumerable.Range(0, concurrency).Select(i =>
            Task.Run(() => _sut.OnCallerJoined("sales", $"PJSIP/caller{i:D4}", $"555{i:D4}", i)));

        await Task.WhenAll(tasks);

        _sut.GetByName("sales")!.EntryCount.Should().Be(concurrency);
    }

    [Fact]
    public void GetQueuesForMember_ShouldReturnAllQueues()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberAdded("billing", "PJSIP/agent01", "Agent 01", 0, false, 1);

        _sut.GetQueuesForMember("PJSIP/agent01").Should().HaveCount(3);
        _sut.GetQueuesForMember("PJSIP/agent01").Should().Contain(["sales", "support", "billing"]);
    }

    [Fact]
    public void GetQueuesForMember_ShouldReturnEmpty_WhenMemberNotFound()
    {
        _sut.GetQueuesForMember("PJSIP/unknown").Should().BeEmpty();
    }

    [Fact]
    public void GetQueueObjectsForMember_ShouldReturnQueueInstances()
    {
        _sut.OnQueueParams("sales", 10, "ringall", 0, 0, 0, 0, 0);
        _sut.OnQueueParams("support", 5, "roundrobin", 0, 0, 0, 0, 0);
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 1);

        var queues = _sut.GetQueueObjectsForMember("PJSIP/agent01").ToList();
        queues.Should().HaveCount(2);
        queues.Select(q => q.Name).Should().Contain(["sales", "support"]);
    }

    [Fact]
    public void OnMemberRemoved_ShouldUpdateReverseIndex()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberRemoved("sales", "PJSIP/agent01");

        _sut.GetQueuesForMember("PJSIP/agent01").Should().HaveCount(1);
        _sut.GetQueuesForMember("PJSIP/agent01").Should().Contain("support");
    }
}
