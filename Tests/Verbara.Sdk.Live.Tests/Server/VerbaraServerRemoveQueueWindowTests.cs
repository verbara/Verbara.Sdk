using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Queues;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// What a queue listing being read does with a queue the host removes through <c>QueueManager.RemoveQueue</c>, every
/// listing answered through the server's own AMI connection. Rows a listing read before the removal do not bring the
/// queue, its members or its waiting callers back when the listing is applied; a listing asked for after the removal
/// lists the queue again, as the next event naming it would.
/// </summary>
[Collection(LiveCountersGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerRemoveQueueWindowTests : IAsyncLifetime
{
    private const string Queue = "q-sales";
    private const string Member = "PJSIP/agent-1";
    private const string Caller = "PJSIP/a-1";

    /// <summary>Bound on every load and on the class cleanup, so a hang fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _sut;
    private ManagerEvent[] _queueStatus = [];

    public VerbaraServerRemoveQueueWindowTests()
    {
        _connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? _queueStatus : []));
        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _sut.DisposeAsync().AsTask().WaitAsync(Bound);

    [Fact]
    public async Task RemoveQueue_ShouldKeepTheQueueRemoved_WhenAListingReadBeforeTheRemovalIsApplied()
    {
        await StartHoldingTheQueueAsync();
        using var counters = new LiveCounters();
        var removed = false;
        _sut.QueueRowBuffered = () =>
        {
            // The first row of the second listing is read: the host removes the queue before the listing is applied.
            if (!removed)
                removed = _sut.Queues.RemoveQueue(Queue);
            return default;
        };

        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(Bound);

        using var scope = new AssertionScope();
        removed.Should().BeTrue("the queue was held when the listing's first row was read");
        _sut.Queues.GetByName(Queue).Should().BeNull(
            "the listing was read before the host removed the queue, so applying it would undo the removal");
        _sut.Queues.GetQueueObjectsForMember(Member).Should().BeEmpty("the removed queue's member is not put back");
        counters.Left.Should().Be(1, "RemoveQueue counts the caller the queue held as left");
        counters.Joined.Should().Be(0, "no row read before the removal admits the caller again");
        _sut.Queues.ActiveSnapshotWindowCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveQueue_ShouldKeepTheQueueRemoved_WhenTheRemovalRunsBetweenTwoRowsBeingApplied()
    {
        await StartHoldingTheQueueAsync();
        var removed = false;
        // The listing's queue row is applied; the host removes the queue before its member and caller rows are.
        _sut.Queues.SnapshotMemberApplying = () =>
        {
            if (!removed)
                removed = _sut.Queues.RemoveQueue(Queue);
        };

        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(Bound);

        using var scope = new AssertionScope();
        removed.Should().BeTrue();
        _sut.Queues.GetByName(Queue).Should().BeNull(
            "the rows still to apply were read before the removal, so they do not recreate the queue");
        _sut.Queues.GetQueueObjectsForMember(Member).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveQueue_ShouldKeepTheQueueRemoved_WhenTheHostRemovesItOnAnotherThreadWhileTheListingIsApplied()
    {
        await StartHoldingTheQueueAsync();
        Task<bool>? removal = null;
        var rows = 0;
        _sut.QueueRowBuffered = () =>
        {
            // The last row is read: the host removes the queue on its own thread while the load applies the rows.
            if (++rows == 3)
                removal = Task.Run(() => _sut.Queues.RemoveQueue(Queue));
            return default;
        };

        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(Bound);
        var removed = await removal!.WaitAsync(Bound);

        using var scope = new AssertionScope();
        removed.Should().BeTrue();
        _sut.Queues.GetByName(Queue).Should().BeNull(
            "a removal while the listing is applied holds, whichever of the two takes the table first");
        _sut.Queues.GetQueueObjectsForMember(Member).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveQueue_ShouldLetTheNextListingAdmitTheQueueAgain_WhenItIsAskedForAfterTheRemoval()
    {
        await StartHoldingTheQueueAsync();
        _sut.Queues.RemoveQueue(Queue).Should().BeTrue();

        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(Bound);

        using var scope = new AssertionScope();
        var queue = _sut.Queues.GetByName(Queue);
        queue.Should().NotBeNull("a listing asked for after the removal is Asterisk's answer about the queue now");
        queue!.Members.Should().ContainKey(Member);
        queue.Entries.Values.Select(e => e.UniqueId).Should().Equal("u1");
    }

    private async Task StartHoldingTheQueueAsync()
    {
        _queueStatus = [Params(), SnapshotMember(), SnapshotEntry()];
        await _sut.StartAsync().WaitAsync(Bound);
        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(Bound);
        var queue = _sut.Queues.GetByName(Queue);
        queue.Should().NotBeNull("the first listing admits the queue");
        queue!.Members.Should().ContainKey(Member);
        queue.Entries.Should().HaveCount(1);
    }

    private static QueueParamsEvent Params() => new() { Queue = Queue, Strategy = "ringall" };

    private static QueueMemberEvent SnapshotMember() => new()
    {
        Queue = Queue,
        Location = Member,
        MemberName = "Agent 1",
        Status = 1,
    };

    private static QueueEntryEvent SnapshotEntry() => new()
    {
        Queue = Queue,
        Channel = Caller,
        Uniqueid = "u1",
        CallerIDNum = "100",
        Position = 1,
        Wait = 3,
    };

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var evt in events)
            yield return evt;
    }
}
