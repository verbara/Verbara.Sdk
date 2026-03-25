using Asterisk.Sdk.Live.Queues;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Queues;

public sealed class QueueManagerExtendedTests
{
    private readonly QueueManager _sut = new(NullLogger.Instance);

    // ── OnDeviceStateChanged: all 9 device state mappings ──

    [Theory]
    [InlineData("NOT_INUSE", QueueMemberState.DeviceNotInUse)]
    [InlineData("INUSE", QueueMemberState.DeviceInUse)]
    [InlineData("BUSY", QueueMemberState.DeviceBusy)]
    [InlineData("INVALID", QueueMemberState.DeviceInvalid)]
    [InlineData("UNAVAILABLE", QueueMemberState.DeviceUnavailable)]
    [InlineData("RINGING", QueueMemberState.DeviceRinging)]
    [InlineData("RINGINUSE", QueueMemberState.DeviceRingInUse)]
    [InlineData("ONHOLD", QueueMemberState.DeviceOnHold)]
    [InlineData("SOMETHING_ELSE", QueueMemberState.DeviceUnknown)]
    public void OnDeviceStateChanged_ShouldMapState_WhenDeviceStateIs(string deviceState, QueueMemberState expected)
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);

        _sut.OnDeviceStateChanged("PJSIP/agent01", deviceState);

        var member = _sut.GetByName("sales")!.Members["PJSIP/agent01"];
        member.Status.Should().Be(expected);
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldUpdateAllQueues_WhenMemberInMultipleQueues()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnMemberAdded("billing", "PJSIP/agent01", "Agent 01", 0, false, 0);

        _sut.OnDeviceStateChanged("PJSIP/agent01", "INUSE");

        _sut.GetByName("sales")!.Members["PJSIP/agent01"].Status.Should().Be(QueueMemberState.DeviceInUse);
        _sut.GetByName("support")!.Members["PJSIP/agent01"].Status.Should().Be(QueueMemberState.DeviceInUse);
        _sut.GetByName("billing")!.Members["PJSIP/agent01"].Status.Should().Be(QueueMemberState.DeviceInUse);
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldFireMemberStatusChanged_ForEachQueue()
    {
        var firedEvents = new List<(string QueueName, QueueMemberState Status)>();
        _sut.MemberStatusChanged += (queueName, member) =>
            firedEvents.Add((queueName, member.Status));

        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 0);

        _sut.OnDeviceStateChanged("PJSIP/agent01", "RINGING");

        firedEvents.Should().HaveCount(2);
        firedEvents.Should().AllSatisfy(e => e.Status.Should().Be(QueueMemberState.DeviceRinging));
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldBeNoOp_WhenDeviceNotRegistered()
    {
        var fired = false;
        _sut.MemberStatusChanged += (_, _) => fired = true;

        _sut.OnDeviceStateChanged("PJSIP/unknown", "INUSE");

        fired.Should().BeFalse();
    }

    [Fact]
    public void OnDeviceStateChanged_ShouldBeCaseInsensitive()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);

        _sut.OnDeviceStateChanged("PJSIP/agent01", "not_inuse");

        _sut.GetByName("sales")!.Members["PJSIP/agent01"].Status.Should().Be(QueueMemberState.DeviceNotInUse);
    }

    // ── GetMembersWhere ──

    [Fact]
    public void GetMembersWhere_ShouldReturnMatchingMembers()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, true, 0);
        _sut.OnMemberAdded("sales", "PJSIP/agent02", "Agent 02", 0, false, 0);
        _sut.OnMemberAdded("sales", "PJSIP/agent03", "Agent 03", 0, true, 0);

        var paused = _sut.GetMembersWhere("sales", m => m.Paused).ToList();

        paused.Should().HaveCount(2);
        paused.Select(m => m.Interface).Should().Contain(["PJSIP/agent01", "PJSIP/agent03"]);
    }

    [Fact]
    public void GetMembersWhere_ShouldReturnEmpty_WhenNoMatch()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);

        _sut.GetMembersWhere("sales", m => m.Paused).Should().BeEmpty();
    }

    [Fact]
    public void GetMembersWhere_ShouldReturnEmpty_WhenQueueNotFound()
    {
        _sut.GetMembersWhere("nonexistent", _ => true).Should().BeEmpty();
    }

    // ── RemoveQueue ──

    [Fact]
    public void RemoveQueue_ShouldRemoveQueueAndCleanReverseIndex()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnMemberAdded("sales", "PJSIP/agent02", "Agent 02", 0, false, 0);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 0);

        var result = _sut.RemoveQueue("sales");

        result.Should().BeTrue();
        _sut.GetByName("sales").Should().BeNull();
        // agent01 should still be in support but not sales
        _sut.GetQueuesForMember("PJSIP/agent01").Should().ContainSingle().Which.Should().Be("support");
        // agent02 was only in sales, so reverse index should be empty for it
        _sut.GetQueuesForMember("PJSIP/agent02").Should().BeEmpty();
    }

    [Fact]
    public void RemoveQueue_ShouldReturnFalse_WhenQueueDoesNotExist()
    {
        _sut.RemoveQueue("nonexistent").Should().BeFalse();
    }

    // ── Events ──

    [Fact]
    public void OnQueueParams_ShouldFireQueueUpdatedEvent()
    {
        AsteriskQueue? fired = null;
        _sut.QueueUpdated += q => fired = q;

        _sut.OnQueueParams("sales", 10, "ringall", 3, 15, 120, 500, 20);

        fired.Should().NotBeNull();
        fired!.Name.Should().Be("sales");
        fired.Strategy.Should().Be("ringall");
    }

    [Fact]
    public void OnMemberAdded_ShouldFireMemberAddedEvent()
    {
        string? firedQueue = null;
        AsteriskQueueMember? firedMember = null;
        _sut.MemberAdded += (q, m) => { firedQueue = q; firedMember = m; };

        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 5, false, 1);

        firedQueue.Should().Be("sales");
        firedMember.Should().NotBeNull();
        firedMember!.Interface.Should().Be("PJSIP/agent01");
        firedMember.Penalty.Should().Be(5);
    }

    [Fact]
    public void OnMemberRemoved_ShouldFireMemberRemovedEvent()
    {
        string? firedQueue = null;
        AsteriskQueueMember? firedMember = null;
        _sut.MemberRemoved += (q, m) => { firedQueue = q; firedMember = m; };

        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 1);
        _sut.OnMemberRemoved("sales", "PJSIP/agent01");

        firedQueue.Should().Be("sales");
        firedMember.Should().NotBeNull();
        firedMember!.Interface.Should().Be("PJSIP/agent01");
    }

    [Fact]
    public void OnCallerJoined_ShouldFireCallerJoinedEvent()
    {
        string? firedQueue = null;
        AsteriskQueueEntry? firedEntry = null;
        _sut.CallerJoined += (q, e) => { firedQueue = q; firedEntry = e; };

        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);

        firedQueue.Should().Be("sales");
        firedEntry.Should().NotBeNull();
        firedEntry!.Channel.Should().Be("PJSIP/5551234-001");
        firedEntry.Position.Should().Be(1);
    }

    [Fact]
    public void OnCallerLeft_ShouldFireCallerLeftEvent()
    {
        string? firedQueue = null;
        AsteriskQueueEntry? firedEntry = null;
        _sut.CallerLeft += (q, e) => { firedQueue = q; firedEntry = e; };

        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);
        _sut.OnCallerLeft("sales", "PJSIP/5551234-001");

        firedQueue.Should().Be("sales");
        firedEntry.Should().NotBeNull();
        firedEntry!.Channel.Should().Be("PJSIP/5551234-001");
    }

    // ── No-op edge cases ──

    [Fact]
    public void OnMemberPaused_ShouldBeNoOp_WhenQueueDoesNotExist()
    {
        // Should not throw
        var act = () => _sut.OnMemberPaused("nonexistent", "PJSIP/agent01", true, "Break");
        act.Should().NotThrow();
    }

    [Fact]
    public void OnMemberPaused_ShouldBeNoOp_WhenMemberDoesNotExist()
    {
        _sut.OnQueueParams("sales", 10, "ringall", 0, 0, 0, 0, 0);

        var act = () => _sut.OnMemberPaused("sales", "PJSIP/unknown", true, "Break");
        act.Should().NotThrow();
    }

    [Fact]
    public void OnCallerLeft_ShouldBeNoOp_WhenQueueDoesNotExist()
    {
        var fired = false;
        _sut.CallerLeft += (_, _) => fired = true;

        var act = () => _sut.OnCallerLeft("nonexistent", "PJSIP/5551234-001");
        act.Should().NotThrow();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnCallerLeft_ShouldBeNoOp_WhenChannelDoesNotExist()
    {
        _sut.OnQueueParams("sales", 10, "ringall", 0, 0, 0, 0, 0);
        var fired = false;
        _sut.CallerLeft += (_, _) => fired = true;

        var act = () => _sut.OnCallerLeft("sales", "PJSIP/unknown-001");
        act.Should().NotThrow();
        fired.Should().BeFalse();
    }

    // ── Clear ──

    [Fact]
    public void Clear_ShouldResetPrimaryAndReverseIndices()
    {
        _sut.OnMemberAdded("sales", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnMemberAdded("support", "PJSIP/agent01", "Agent 01", 0, false, 0);
        _sut.OnCallerJoined("sales", "PJSIP/5551234-001", "5551234", 1);

        _sut.Clear();

        _sut.QueueCount.Should().Be(0);
        _sut.GetByName("sales").Should().BeNull();
        _sut.GetQueuesForMember("PJSIP/agent01").Should().BeEmpty();
    }
}
