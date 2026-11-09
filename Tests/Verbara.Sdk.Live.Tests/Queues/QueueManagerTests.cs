using Verbara.Sdk.Live.Queues;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Live.Tests.Queues;

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
    public void GetQueueObjectsForMember_ShouldLookUpMemberWhenEnumerated_WhenMemberIsAddedAfterCall()
    {
        // The result is lazy: each enumeration looks the member up again, so a sequence taken
        // before the member existed sees the member's queue once it is added.
        var queues = _sut.GetQueueObjectsForMember("PJSIP/agent01");
        queues.Should().BeEmpty();

        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);

        queues.Select(q => q.Name).Should().Equal("sales");
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

    // --- MemberPaused: red on main by compile error (CS1061) ---

    [Fact]
    public void MemberPaused_ShouldBeRaisedOnce_WhenAHeldMemberIsPaused()
    {
        _sut.OnMemberAdded("sales", "PJSIP/2001", "2001", 0, false, 1);
        var seen = new List<(string Queue, AsteriskQueueMember Member, bool Paused, string? Reason)>();
        _sut.MemberPaused += (q, m) => seen.Add((q, m, m.Paused, m.PausedReason));

        _sut.OnMemberPaused("sales", "PJSIP/2001", true, "lunch");

        seen.Should().ContainSingle();
        seen[0].Queue.Should().Be("sales");
        seen[0].Member.Should().BeSameAs(_sut.GetByName("sales")!.Members["PJSIP/2001"], "the table's own member object");
        seen[0].Paused.Should().BeTrue();
        seen[0].Reason.Should().Be("lunch");
    }

    [Fact]
    public void MemberPaused_ShouldReportTheMemberUnpaused_WhenAHeldMemberIsUnpaused()
    {
        _sut.OnMemberAdded("sales", "PJSIP/2001", "2001", 0, true, 1);
        var seen = new List<bool>();
        _sut.MemberPaused += (_, m) => seen.Add(m.Paused);

        _sut.OnMemberPaused("sales", "PJSIP/2001", false);

        seen.Should().Equal([false]);
    }

    [Fact]
    public void MemberPaused_ShouldShowTheMemberAsTheTableHoldsIt_WhenAHandlerUnpausesIt()
    {
        _sut.OnMemberAdded("sales", "PJSIP/2001", "2001", 0, false, 1);
        var first = new List<bool>();
        var second = new List<bool>();
        _sut.MemberPaused += (q, m) =>
        {
            first.Add(m.Paused);
            if (m.Paused)
                _sut.OnMemberPaused(q, m.Interface, false);
        };
        _sut.MemberPaused += (_, m) => second.Add(m.Paused);

        _sut.OnMemberPaused("sales", "PJSIP/2001", true);

        using (new AssertionScope())
        {
            first.Should().Equal([true, false]);
            second.Should().Equal([false, false],
                "the second handler reads the member as the table holds it, then receives the unpause's own notification");
        }
    }

    [Fact]
    public void MemberPaused_ShouldNotBeRaised_WhenNoQueueHoldsTheMember()
    {
        _sut.OnMemberAdded("sales", "PJSIP/2001", "2001", 0, false, 1);
        var raised = 0;
        _sut.MemberPaused += (_, _) => raised++;

        _sut.OnMemberPaused("sales", "PJSIP/9999", true);

        using (new AssertionScope())
        {
            raised.Should().Be(0);
            _sut.GetByName("sales")!.Members.Keys.Should().Equal(["PJSIP/2001"], "the table is unchanged");
            _sut.GetByName("sales")!.Members["PJSIP/2001"].Paused.Should().BeFalse();
        }
    }

    [Fact]
    public void MemberPaused_ShouldNotBeRaised_WhenAPauseArrivesDuringASnapshotReadBeforeTheTableHoldsTheMember()
    {
        var paused = 0;
        var added = new List<bool>();
        _sut.MemberPaused += (_, _) => paused++;
        _sut.MemberAdded += (_, m) => added.Add(m.Paused);
        var window = _sut.OpenSnapshotWindow();
        try
        {
            _sut.OnMemberPaused("sales", "PJSIP/2001", true, "lunch");
            _sut.OnSnapshotQueueParams(window, "sales", 0, "ringall", 0, 0, 0, 0, 0);
            _sut.OnSnapshotMember(window, "sales", "PJSIP/2001", "2001", 0, false, 1);
            _sut.CompleteSnapshot(window);
        }
        finally
        {
            _sut.CloseSnapshotWindow(window);
        }

        using (new AssertionScope())
        {
            paused.Should().Be(0, "the table did not hold the member when the pause arrived");
            added.Should().Equal([true], "the snapshot's member is announced once, with the live pause it was read after");
        }
    }
}
