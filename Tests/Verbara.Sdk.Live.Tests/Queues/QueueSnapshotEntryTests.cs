using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// A queue entry says where it came from: a <c>QueueEntry</c> of a <c>QueueStatus</c> snapshot, with
/// the <c>Wait</c> Asterisk reported on it, or a live <c>QueueCallerJoin</c>, with neither. Everything
/// else Live does with the entry is the same for both: the table, <c>live.queue.calls.joined</c>,
/// <see cref="AsteriskQueueEntry.JoinedAt"/> and <see cref="QueueManager.CallerJoined"/>.
/// </summary>
[Collection(LiveQueueJoinCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class QueueSnapshotEntryTests : IAsyncLifetime
{
    private const string Queue = "q-late";
    private const string Caller = "PJSIP/far-00000001";
    private const string OtherCaller = "PJSIP/far-00000002";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _sut;
    private readonly List<(string Queue, AsteriskQueueEntry Entry)> _joined = [];
    private IObserver<ManagerEvent>? _observer;
    private ManagerEvent[] _queueStatus = [];

    public QueueSnapshotEntryTests()
    {
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? _queueStatus : []));
        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut.Queues.CallerJoined += (queue, entry) => _joined.Add((queue, entry));
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _sut.DisposeAsync();
    }

    [Theory]
    [InlineData(5L)]
    [InlineData(0L)]
    [InlineData(null)]
    [InlineData(-3L)]
    public async Task RequestInitialStateAsync_ShouldRaiseCallerJoinedMarkedAsFromTheSnapshotWithTheWaitAsSent_WhenQueueStatusReportsACallerWaiting(long? wait)
    {
        _queueStatus = [SnapshotEntry(Caller, wait)];

        await _sut.StartAsync();

        var (queue, entry) = _joined.Should().ContainSingle(
            "the snapshot reported one caller waiting, and Live announces it as a join").Subject;
        queue.Should().Be(Queue);
        entry.Channel.Should().Be(Caller);
        entry.FromSnapshot.Should().BeTrue(
            "the entry came from a QueueStatus snapshot, not from a live QueueCallerJoin");
        entry.ReportedWaitSeconds.Should().Be(wait,
            "Live carries the Wait Asterisk reported exactly as sent, absent or not; what it means for a visit is the session manager's to decide");
        _sut.Queues.GetByName(Queue)!.Entries[Caller].Should().BeSameAs(entry,
            "the entry announced is the one Live holds in its queue table");
    }

    [Fact]
    public async Task EventObserver_ShouldRaiseCallerJoinedNotMarkedAndWithNoWait_WhenALiveQueueCallerJoinArrives()
    {
        await _sut.StartAsync();

        _observer!.OnNext(LiveJoin(Caller));

        var (queue, entry) = _joined.Should().ContainSingle(
            "one live QueueCallerJoin is one join").Subject;
        queue.Should().Be(Queue);
        entry.FromSnapshot.Should().BeFalse(
            "a live QueueCallerJoin is app_queue's report of a new entry, not a snapshot");
        entry.ReportedWaitSeconds.Should().BeNull(
            "a live QueueCallerJoin carries no Wait");
        _sut.Queues.GetByName(Queue)!.Entries[Caller].Should().BeSameAs(entry);
    }

    [Fact]
    public void OnCallerJoined_ShouldAddAnEntryNotMarkedAsFromASnapshot_WhenCalledThroughThePublicMethod()
    {
        var queues = new QueueManager(NullLogger.Instance);
        AsteriskQueueEntry? raised = null;
        queues.CallerJoined += (_, entry) => raised = entry;

        queues.OnCallerJoined(Queue, Caller, "5552101", 1);

        raised.Should().NotBeNull("the public method raises CallerJoined as it always has");
        raised!.FromSnapshot.Should().BeFalse(
            "the public method is the route of a live join and of every caller outside this assembly");
        raised.ReportedWaitSeconds.Should().BeNull();
        queues.GetByName(Queue)!.Entries[Caller].Should().BeSameAs(raised);
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldCountTheJoinAndStampJoinedAtAsALiveJoinDoes_WhenTheSnapshotEntryReportsAWait()
    {
        using var counter = new LiveQueueJoinCounter();
        _queueStatus = [SnapshotEntry(Caller, wait: 5)];

        var beforeSnapshot = DateTimeOffset.UtcNow;
        await _sut.StartAsync();
        var afterSnapshot = DateTimeOffset.UtcNow;
        var joinedBySnapshot = counter.Joined;

        _observer!.OnNext(LiveJoin(OtherCaller));
        var afterLive = DateTimeOffset.UtcNow;

        joinedBySnapshot.Should().Be(1,
            "live.queue.calls.joined counts a snapshot entry as a join, as it did before the entry carried its origin");
        counter.Joined.Should().Be(2, "and a live join the same way");
        _joined.Select(j => j.Entry.Channel).Should().Equal(Caller, OtherCaller);
        _joined[0].Entry.JoinedAt.Should().BeOnOrAfter(beforeSnapshot,
                "JoinedAt is when Live handled the entry; the reported Wait does not backdate it")
            .And.BeOnOrBefore(afterSnapshot);
        _joined[1].Entry.JoinedAt.Should().BeOnOrAfter(afterSnapshot).And.BeOnOrBefore(afterLive);
        _sut.Queues.GetByName(Queue)!.EntryCount.Should().Be(2,
            "both routes add the caller to the queue table");
    }

    private static QueueEntryEvent SnapshotEntry(string channel, long? wait) => new()
    {
        Queue = Queue,
        Channel = channel,
        CallerIDNum = "5552101",
        Position = 1,
        Wait = wait
    };

    private static QueueCallerJoinEvent LiveJoin(string channel) => new()
    {
        Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = Queue,
            ["Channel"] = channel,
            ["CallerIDNum"] = "5552102"
        }
    };

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var evt in events)
            yield return evt;
    }
}
