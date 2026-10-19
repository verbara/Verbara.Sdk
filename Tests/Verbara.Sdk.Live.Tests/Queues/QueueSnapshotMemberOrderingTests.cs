using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Queues;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// Replays retained values through the real manager/window entry point after newer live reports. These are the
/// manager defects that whole-answer buffering must repair; today's streaming wire path applies its rows earlier.
/// </summary>
[Collection(LiveCountersGroup.Name)]
public sealed class QueueSnapshotMemberOrderingTests : IDisposable
{
    private const string Queue = "q-sales";
    private const string Interface = "PJSIP/agent-1";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly QueueManager _sut = new(NullLogger.Instance);
    private readonly List<QueueSnapshotWindow> _windows = [];
    private readonly List<Observed> _added = [];
    private readonly List<Observed> _removed = [];
    private readonly List<Observed> _status = [];

    public QueueSnapshotMemberOrderingTests()
    {
        _sut.MemberAdded += (_, member) => _added.Add(Observe(member));
        _sut.MemberRemoved += (_, member) => _removed.Add(Observe(member));
        _sut.MemberStatusChanged += (_, member) => _status.Add(Observe(member));
    }

    public void Dispose()
    {
        _sut.SnapshotMemberApplying = null;
        foreach (var window in _windows)
            window.Dispose();
        _sut.ActiveSnapshotWindowCount.Should().Be(0, "the fixture releases every manager read window even after a red assertion");
    }

    [Theory]
    [InlineData("held")]
    [InlineData("queue-only")]
    [InlineData("absent")]
    public void OnSnapshotMember_ShouldNotResurrectARemovedMember_WhenItsOlderRowIsReplayed(string initial)
    {
        Seed(initial);
        var window = OpenWindow();
        var row = Row();
        ClearObserved();

        _sut.OnMemberRemoved(Queue, Interface);
        Replay(window, row);
        _sut.CompleteSnapshot(window);

        using var scope = new AssertionScope();
        Member().Should().BeNull("a live removal invalidates the older row even if the member or queue was absent");
        _sut.GetQueuesForMember(Interface).Should().BeEmpty("an invalidated row adds no reverse index membership");
        _sut.GetQueueObjectsForMember(Interface).Should().BeEmpty();
        _added.Should().BeEmpty("a stale row must not announce MemberAdded after the live removal");
        _removed.Should().HaveCount(initial == "held" ? 1 : 0);
        _status.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pause", true)]
    [InlineData("pause", false)]
    [InlineData("status", true)]
    [InlineData("status", false)]
    public void OnSnapshotMember_ShouldPreserveTheLatestLiveFieldAndPayload_WhenItsOlderRowIsReplayed(
        string field, bool changed)
    {
        AddLive(Row(paused: field == "pause" && !changed,
            status: field == "status" && !changed ? 2 : 1));
        if (field == "pause" && !changed)
            _sut.OnMemberPaused(Queue, Interface, true, "break");
        var window = OpenWindow();
        var row = Row();
        ClearObserved();

        Report(field);
        Replay(window, row);

        var expected = Observe(row) with
        {
            Paused = field == "pause",
            Reason = field == "pause" ? "break" : null,
            Status = field == "status" ? QueueMemberState.DeviceInUse : QueueMemberState.DeviceNotInUse,
        };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "newer reports, including same-value reports, protect their field from the captured row");
        _added.Should().Equal([expected], "the valid listed member is announced with its merged effective state");
        _status.Should().HaveCount(field == "status" ? 1 : 0, "only the existing live status route announces a status event");
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("status")]
    public void OnSnapshotMember_ShouldRefreshUntouchedFields_WhenOnlyTheOtherFieldHasANewerLiveReport(string field)
    {
        AddLive(Row(name: "held", penalty: 0));
        var window = OpenWindow();
        var row = Row(name: "refreshed", penalty: 7, paused: true, status: 2);
        ClearObserved();

        if (field == "pause")
            _sut.OnMemberPaused(Queue, Interface, false, "available");
        else
            _sut.OnMemberStatusChanged(Queue, Interface, 1);
        Replay(window, row);

        var expected = Observe(row) with
        {
            Paused = field != "pause",
            Reason = field == "pause" ? "available" : null,
            Status = field == "status" ? QueueMemberState.DeviceNotInUse : QueueMemberState.DeviceInUse,
        };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "protecting a live field still permits name, penalty and the untouched state field to refresh");
        _added.Should().Equal([expected]);
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    public void OnSnapshotMember_ShouldSeedAnAbsentMembersLatestLiveField_WhenItsQueueMayAlsoBeAbsent(
        string field, bool queueExists)
    {
        Seed(queueExists ? "queue-only" : "absent");
        var window = OpenWindow();
        var row = Row();

        Report(field);
        Member().Should().BeNull("a live report about an absent member does not create it");
        _added.Should().BeEmpty();
        _status.Should().BeEmpty("an absent member report does not fabricate a public status event");
        Replay(window, row);

        var expected = Observe(row) with
        {
            Paused = field == "pause",
            Reason = field == "pause" ? "break" : null,
            Status = field == "status" ? QueueMemberState.DeviceInUse : QueueMemberState.DeviceNotInUse,
        };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "the read window retains newer field reports independently of table admission");
        _added.Should().Equal([expected]);
        _status.Should().BeEmpty();
        _sut.GetQueuesForMember(Interface).Should().Equal(Queue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnSnapshotMember_ShouldKeepTheLatestAddedLife_WhenRemovalAndReaddFollowTheCapturedRow(bool removeAgain)
    {
        AddLive(Row());
        var window = OpenWindow();
        var row = Row(name: "stale", paused: true, status: 6, penalty: 99);
        _sut.OnMemberPaused(Queue, Interface, true, "old-life");
        _sut.OnMemberStatusChanged(Queue, Interface, 6);
        _sut.OnMemberRemoved(Queue, Interface);
        var replacement = Row(name: "new-life", penalty: 3, status: 2);
        AddLive(replacement);
        if (removeAgain)
            _sut.OnMemberRemoved(Queue, Interface);
        ClearObserved();

        Replay(window, row);

        using var scope = new AssertionScope();
        if (removeAgain)
        {
            Member().Should().BeNull("remove/add/remove leaves a tombstone until another live add");
            _sut.GetQueuesForMember(Interface).Should().BeEmpty();
            _added.Should().BeEmpty();
        }
        else
        {
            Observe(Member()!).Should().Be(Observe(replacement), "an old row and prior-life fields cannot replace the newer live add");
            _added.Should().Equal([Observe(replacement)]);
            _sut.GetQueuesForMember(Interface).Should().Equal(Queue);
        }
    }

    [Fact]
    public void OnSnapshotMember_ShouldKeepATombstoneThroughFieldReports_WhenNoLiveAddStartedAnotherLife()
    {
        var window = OpenWindow();
        var row = Row();
        _sut.OnMemberRemoved(Queue, Interface);
        Report("pause");
        Report("status");

        Replay(window, row);

        using var scope = new AssertionScope();
        Member().Should().BeNull("field reports cannot revive the lifecycle invalidated by a removal");
        _added.Should().BeEmpty();
        _status.Should().BeEmpty();
        _sut.GetQueuesForMember(Interface).Should().BeEmpty();
    }

    [Fact]
    public void OnSnapshotMember_ShouldUseEachFieldsLatestReport_WhenPauseAndStatusArriveIndependently()
    {
        AddLive(Row());
        var window = OpenWindow();
        var row = Row(name: "refreshed", penalty: 4, paused: true, status: 2);
        _sut.OnMemberPaused(Queue, Interface, true, "old-reason");
        _sut.OnMemberStatusChanged(Queue, Interface, 2);
        _sut.OnMemberPaused(Queue, Interface, false, "available");
        _sut.OnMemberStatusChanged(Queue, Interface, 6);
        ClearObserved();

        Replay(window, row);

        var expected = Observe(row) with { Paused = false, Reason = "available", Status = QueueMemberState.DeviceRinging };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "the latest pause/reason and status are independently ordered");
        _added.Should().Equal([expected]);
        _status.Should().BeEmpty("snapshot merging does not synthesize a status transition");
    }

    [Fact]
    public void OnSnapshotMember_ShouldDiscardPriorLifeFields_WhenAFreshReadAdmitsAfterAnUnknownRemoval()
    {
        var old = OpenWindow();
        Report("pause");
        Report("status");
        _sut.OnMemberRemoved(Queue, Interface);
        var fresh = OpenWindow();

        Replay(old, Row(paused: true, status: 6));
        var restoredByOld = Member() is not null;
        Replay(fresh, Row());

        using var scope = new AssertionScope();
        restoredByOld.Should().BeFalse("the removal invalidates the already-read lifetime");
        Observe(Member()!).Should().Be(Observe(Row()), "a later read does not inherit absent-key fields from the removed lifetime");
        _added.Should().Equal([Observe(Row())]);
        _status.Should().BeEmpty();
    }

    [Fact]
    public void OnSnapshotMember_ShouldShareQueueCasingAndKeepInterfaceCasingDistinct_WhenReportsUseCaseVariants()
    {
        var window = OpenWindow();
        var row = Row();
        _sut.OnMemberRemoved("Q-SALES", Interface);
        var other = row with { Interface = "pjsip/agent-1", Name = "distinct" };
        AddLive(other, "Q-SALES");
        ClearObserved();

        Replay(window, row);
        Replay(window, other);
        _sut.CompleteSnapshot(window);

        using var scope = new AssertionScope();
        _sut.QueueCount.Should().Be(1);
        _sut.GetByName(Queue)!.Members.Keys.Should().Equal([other.Interface],
            "queue names ignore case while interface keys remain ordinally distinct");
        _sut.GetQueuesForMember(Interface).Should().BeEmpty();
        _sut.GetQueuesForMember(other.Interface).Should().ContainSingle().Which.Should().BeEquivalentTo(Queue);
        _sut.GetQueueObjectsForMember(other.Interface).Should().ContainSingle();
        _added.Should().Equal([Observe(other)]);
    }

    [Fact]
    public void OnMemberRemoved_ShouldRemoveTheSameReverseMembership_WhenTheQueueNameChangesCase()
    {
        AddLive(Row());

        _sut.OnMemberRemoved("Q-SALES", Interface);

        using var scope = new AssertionScope();
        Member().Should().BeNull();
        _sut.GetQueuesForMember(Interface).Should().BeEmpty("the reverse index uses the table's case-insensitive queue identity");
        _sut.GetQueueObjectsForMember(Interface).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnSnapshotMember_ShouldMergeDeviceAndMemberStatusInOrder_WhenTheExactInterfaceWasNotYetAdmitted(
        bool queueExists, bool deviceLast)
    {
        Seed(queueExists ? "queue-only" : "absent");
        var window = OpenWindow();
        var row = Row();
        if (!deviceLast)
            _sut.OnDeviceStateChanged(Interface, "BUSY");
        _sut.OnMemberStatusChanged(Queue, Interface, 2);
        if (deviceLast)
            _sut.OnDeviceStateChanged(Interface, "BUSY");
        _status.Should().BeEmpty();
        Member().Should().BeNull();

        Replay(window, row);

        var expected = Observe(row) with { Status = deviceLast ? QueueMemberState.DeviceBusy : QueueMemberState.DeviceInUse };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "device reports use their exact supported interface key and share live status order");
        _added.Should().Equal([expected]);
        _status.Should().BeEmpty();
    }

    [Fact]
    public void OnSnapshotMember_ShouldKeepADeviceReportForEveryMatchingQueue_WhenBothRowsWereCaptured()
    {
        AddLive(Row());
        AddLive(Row(), "q-support");
        var window = OpenWindow();
        ClearObserved();
        _sut.OnDeviceStateChanged(Interface, "RINGING");

        Replay(window, Row());
        Replay(window, Row(), "q-support");

        using var scope = new AssertionScope();
        Member()!.Status.Should().Be(QueueMemberState.DeviceRinging);
        Member("q-support")!.Status.Should().Be(QueueMemberState.DeviceRinging);
        _status.Should().HaveCount(2).And.OnlyContain(value => value.Status == QueueMemberState.DeviceRinging);
        _added.Should().HaveCount(2).And.OnlyContain(value => value.Status == QueueMemberState.DeviceRinging);
        _sut.GetQueuesForMember(Interface).Should().BeEquivalentTo([Queue, "q-support"]);
    }

    [Fact]
    public void OnSnapshotMember_ShouldKeepASameValueDeviceStatus_WhenTheRowCarriesAnotherStatus()
    {
        AddLive(Row());
        var window = OpenWindow();
        var row = Row(name: "refreshed", status: 2);
        ClearObserved();
        _sut.OnDeviceStateChanged(Interface, "NOT_INUSE");

        Replay(window, row);

        var expected = Observe(row) with { Status = QueueMemberState.DeviceNotInUse };
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "a same-value device report is still newer than the buffered row");
        _added.Should().Equal([expected]);
        _status.Should().Equal([Observe(Row())], "the existing device route raises its one live status notification");
    }

    [Theory]
    [InlineData("pjsip/agent-1", "PJSIP/agent-1")]
    [InlineData("PJSIP/agent-1", "Local/agent-1@agents/n")]
    public void OnSnapshotMember_ShouldNotInferAnotherDeviceKey_WhenAnUnmatchedDeviceReportPrecedesAdmission(
        string device, string iface)
    {
        var window = OpenWindow();
        _sut.OnDeviceStateChanged(device, "BUSY");

        Replay(window, Row() with { Interface = iface });

        using var scope = new AssertionScope();
        _sut.GetByName(Queue)!.Members[iface].Status.Should().Be(QueueMemberState.DeviceNotInUse);
        _status.Should().BeEmpty();
        _added.Should().Equal([Observe(Row() with { Interface = iface })]);
    }

    [Fact]
    public void OnSnapshotMember_ShouldKeepANewerLiveAdd_WhenItFollowsAnAbsentDeviceReport()
    {
        var window = OpenWindow();
        _sut.OnDeviceStateChanged(Interface, "BUSY");
        var current = Row(name: "current", status: 2);
        AddLive(current);
        ClearObserved();

        Replay(window, Row());

        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(Observe(current));
        _added.Should().Equal([Observe(current)]);
        _status.Should().BeEmpty();
    }

    [Fact]
    public async Task OnSnapshotMember_ShouldRecheckTheLiveLifetime_WhenRemoveAndReaddWinAtTheApplicationBoundary()
    {
        AddLive(Row());
        var window = OpenWindow();
        var row = Row(name: "old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.SnapshotMemberApplying = () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        };
        var apply = Task.Run(() => Replay(window, row));
        try
        {
            await entered.Task.WaitAsync(Bound);
            _sut.OnMemberRemoved("Q-SALES", Interface);
            AddLive(Row(name: "new", penalty: 9, paused: true, status: 2), "Q-SALES");
            ClearObserved();
        }
        finally
        {
            release.TrySetResult();
            await apply.WaitAsync(Bound);
            _sut.SnapshotMemberApplying = null;
        }

        var expected = Observe(Row(name: "new", penalty: 9, paused: true, status: 2));
        using var scope = new AssertionScope();
        Observe(Member()!).Should().Be(expected, "a lifetime check cannot authorize a stale write across the live transition");
        _added.Should().Equal([expected]);
        _sut.GetQueuesForMember(Interface).Should().ContainSingle();
        _sut.GetQueueObjectsForMember(Interface).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnSnapshotMember_ShouldLeaveNoOldWritesAfterTheNotification_WhenAListenerRemovesOrReaddsTheMember(bool readd)
    {
        var window = OpenWindow();
        var initial = Row();
        var replacement = Row(name: "new", penalty: 5, paused: true, status: 2);
        var callbacks = 0;
        var laterSubscriber = new List<Observed>();
        _sut.MemberAdded += (_, _) =>
        {
            if (++callbacks != 1)
                return;
            _sut.OnMemberRemoved(Queue, Interface);
            if (readd)
                AddLive(replacement);
        };
        _sut.MemberAdded += (_, member) => laterSubscriber.Add(Observe(member));

        Replay(window, initial);

        using var scope = new AssertionScope();
        if (readd)
        {
            Observe(Member()!).Should().Be(Observe(replacement));
            _added.Should().Equal([Observe(initial), Observe(replacement)]);
            laterSubscriber.Should().Equal([Observe(replacement), Observe(initial)],
                "normal multicast delivery resumes the original valid event after nested replacement delivery");
            _sut.GetQueuesForMember(Interface).Should().Equal(Queue);
        }
        else
        {
            Member().Should().BeNull();
            _added.Should().Equal([Observe(initial)], "the original event was valid when its normal multicast Invoke began");
            laterSubscriber.Should().Equal([Observe(initial)], "removal does not cancel later subscribers of a valid Invoke");
            _sut.GetQueuesForMember(Interface).Should().BeEmpty();
        }
        _removed.Should().Equal([Observe(initial)]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteSnapshot_ShouldKeepTheReplacementMembersIndex_WhenCallerLeftRecreatesMembershipBeforeOldCleanup(
        bool wholeQueue, bool caseVariant)
    {
        AddLive(Row(name: "old"));
        _sut.OnCallerJoined(Queue, "PJSIP/caller", "100", 1);
        var old = _sut.GetByName(Queue)!;
        var window = OpenWindow();
        if (!wholeQueue)
            _sut.OnSnapshotQueueParams(window, Queue, 0, "ringall", 0, 0, 0, 0, 0);
        var replacements = 0;
        _sut.CallerLeft += (_, _) =>
        {
            replacements++;
            AddLive(Row(name: "replacement"), caseVariant ? "Q-SALES" : Queue);
        };

        _sut.CompleteSnapshot(window);

        using var scope = new AssertionScope();
        replacements.Should().Be(1, "the omitted caller provides a real reentrant boundary before old index cleanup");
        Member()!.MemberName.Should().Be("replacement");
        if (wholeQueue)
            _sut.GetByName(Queue).Should().NotBeSameAs(old);
        _sut.GetQueuesForMember(Interface).Should().ContainSingle().Which.Should().BeEquivalentTo(Queue,
            "old-instance cleanup must not erase a replacement queue/member reverse membership");
        _sut.GetQueueObjectsForMember(Interface).Should().Equal(_sut.GetByName(Queue)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveQueue_ShouldKeepTheReplacementMembersIndex_WhenItsRemovalLogRecreatesTheQueue(bool caseVariant)
    {
        var logger = new CallbackLogger();
        var queues = new QueueManager(logger);
        queues.OnMemberAdded(Queue, Interface, "old", 0, false, 1);
        queues.OnCallerJoined(Queue, "PJSIP/caller", "100", 1);
        var old = queues.GetByName(Queue);
        var logged = 0;
        logger.CallerRemoved = () =>
        {
            logged++;
            queues.OnMemberAdded(caseVariant ? "Q-SALES" : Queue, Interface, "replacement", 0, false, 2);
        };

        queues.RemoveQueue(Queue).Should().BeTrue();

        using var scope = new AssertionScope();
        logged.Should().Be(1, "the real removal log runs after table removal and before ForgetMembers");
        queues.GetByName(Queue).Should().NotBeSameAs(old);
        queues.GetByName(Queue)!.Members[Interface].MemberName.Should().Be("replacement");
        queues.GetQueuesForMember(Interface).Should().ContainSingle().Which.Should().BeEquivalentTo(Queue);
        queues.GetQueueObjectsForMember(Interface).Should().Equal(queues.GetByName(Queue)!);
    }

    [Fact]
    public void OnSnapshotMember_ShouldInvokeTheObservationHook_WhenAValidMemberIsApplied()
    {
        var window = OpenWindow();
        var calls = 0;
        _sut.SnapshotMemberApplying = () => calls++;

        Replay(window, Row());

        calls.Should().Be(1);
        _added.Should().Equal([Observe(Row())]);
    }

    [Fact]
    public void RemoveQueue_ShouldKeepItsNoCallerLeftSemantics_WhenTheRemovalIsLogged()
    {
        var logger = new CallbackLogger();
        var queues = new QueueManager(logger);
        queues.OnCallerJoined(Queue, "PJSIP/caller", "100", 1);
        var logged = 0;
        var left = 0;
        logger.CallerRemoved = () => logged++;
        queues.CallerLeft += (_, _) => left++;

        queues.RemoveQueue(Queue).Should().BeTrue();

        logged.Should().Be(1);
        left.Should().Be(0, "public RemoveQueue keeps its existing silent caller notification semantics");
    }

    private QueueSnapshotWindow OpenWindow()
    {
        var window = _sut.OpenSnapshotWindow();
        _windows.Add(window);
        return window;
    }

    private void Seed(string initial)
    {
        if (initial == "held")
            AddLive(Row());
        else if (initial == "queue-only")
            _sut.OnQueueParams(Queue, 0, "ringall", 0, 0, 0, 0, 0);
        ClearObserved();
    }

    private void Report(string field)
    {
        if (field == "pause")
            _sut.OnMemberPaused(Queue, Interface, true, "break");
        else
            _sut.OnMemberStatusChanged(Queue, Interface, 2);
    }

    private void AddLive(MemberRow row, string queue = Queue) =>
        _sut.OnMemberAdded(queue, row.Interface, row.Name, row.Penalty, row.Paused, row.Status);

    private void Replay(QueueSnapshotWindow window, MemberRow row, string queue = Queue) =>
        _sut.OnSnapshotMember(window, queue, row.Interface, row.Name, row.Penalty, row.Paused, row.Status);

    private AsteriskQueueMember? Member(string queue = Queue) => _sut.GetByName(queue)?.Members.GetValueOrDefault(Interface);

    private void ClearObserved()
    {
        _added.Clear();
        _removed.Clear();
        _status.Clear();
    }

    private static MemberRow Row(string name = "snapshot", int penalty = 0, bool paused = false, int status = 1) =>
        new(Interface, name, penalty, paused, status);

    private static Observed Observe(MemberRow row) =>
        new(row.Interface, row.Name, row.Penalty, row.Paused, null, (QueueMemberState)row.Status);

    private static Observed Observe(AsteriskQueueMember member) =>
        new(member.Interface, member.MemberName, member.Penalty, member.Paused, member.PausedReason, member.Status);

    private sealed record MemberRow(string Interface, string Name, int Penalty, bool Paused, int Status);
    private sealed record Observed(string Interface, string? Name, int Penalty, bool Paused, string? Reason, QueueMemberState Status);

    private sealed class CallbackLogger : ILogger
    {
        public Action? CallerRemoved { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "CallerRemovedUnobserved")
                CallerRemoved?.Invoke();
        }
    }
}
