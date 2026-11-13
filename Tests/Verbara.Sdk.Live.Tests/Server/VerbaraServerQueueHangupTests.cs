using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Queues;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// What the live server does with a waiting caller when Asterisk reports its <c>Hangup</c>, every event delivered
/// through the server's own observer. A caller that leaves the queue with no <c>QueueCallerLeave</c> (relieved by a
/// transfer, or taken out by a bridge) leaves the table at its <c>Hangup</c>, as not observed; a caller that left
/// normally is not touched again; a caller held without a <c>Uniqueid</c>, or a <c>Hangup</c> no queue holds, changes
/// nothing; and a queue snapshot row read before the <c>Hangup</c> does not bring the caller back.
/// </summary>
[Collection(LiveCountersGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerQueueHangupTests : IAsyncLifetime
{
    private const string Queue = "q-sales";
    private const string Caller = "PJSIP/a-1";
    private const string Relieving = "Local/peer-1;1";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _sut;
    private readonly List<LeftInHandler> _left = [];
    private readonly List<string> _reported = [];
    private IObserver<ManagerEvent>? _observer;
    private ManagerEvent[] _queueStatus = [];

    public VerbaraServerQueueHangupTests()
    {
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? _queueStatus : []));
        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut.Queues.CallerLeft += (queue, entry) =>
        {
            lock (_left)
                _left.Add(new LeftInHandler(queue, entry.UniqueId, entry.Channel, entry.LeaveNotObserved));
        };
        _sut.Queues.CallerLeaveReported += report => _reported.Add(report.UniqueId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _sut.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    [Fact]
    public async Task Hangup_ShouldRemoveTheCallerAsNotObserved_WhenNoLeaveWasReported()
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        using var counters = new LiveCounters();

        Hangup("u1", Caller);

        using var scope = new AssertionScope();
        Held().Should().BeEmpty("the caller's Hangup is the last Live will hear of it, and no leave was reported");
        _left.Should().Equal(new LeftInHandler(Queue, "u1", Caller, LeaveNotObserved: true));
        counters.Left.Should().Be(1, "the removal is counted as a leave");
        counters.WaitSamples.Should().Be(0, "a leave Live did not observe records no wait sample");
        _reported.Should().BeEmpty("no QueueCallerLeave was observed, so none is reported");
    }

    [Fact]
    public async Task Hangup_ShouldNotTouchTheRelievingChannel_WhenItLeavesLater()
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        using var counters = new LiveCounters();

        Hangup("u1", Caller);
        var leftAtTheHangup = counters.Left;
        Leave(Relieving, "u2");

        using var scope = new AssertionScope();
        _left.Should().Equal([new LeftInHandler(Queue, "u1", Caller, LeaveNotObserved: true)],
            "the relieved caller left at its Hangup, and the relieving channel never joined");
        (counters.Left - leftAtTheHangup).Should().Be(0, "the later leave names a channel the table does not hold");
        _reported.Should().Equal(["u2"], "Asterisk's own report of the later leave is raised as before");
    }

    [Fact]
    public async Task Hangup_ShouldRemoveNothingMore_WhenTheCallerLeftNormallyFirst()
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        using var counters = new LiveCounters();

        Leave(Caller, "u1");
        Hangup("u1", Caller);

        using var scope = new AssertionScope();
        _left.Should().Equal(new LeftInHandler(Queue, "u1", Caller, LeaveNotObserved: false));
        counters.Left.Should().Be(1);
        counters.WaitSamples.Should().Be(1, "the observed leave records its wait; the Hangup after it records nothing");
    }

    [Fact]
    public async Task Hangup_ShouldLeaveACallerHeldWithoutAUniqueId_WhenItsChannelNameHangsUp()
    {
        await _sut.StartAsync();
        _sut.Queues.OnCallerJoined(Queue, Caller, "100", 1);
        using var counters = new LiveCounters();

        Hangup("u9", Caller);

        using var scope = new AssertionScope();
        _sut.Queues.GetByName(Queue)!.Entries.Should().ContainKey(Caller,
            "a Hangup names a Uniqueid, and a channel name can be reused, so a caller held without one is not matched");
        _left.Should().BeEmpty();
        counters.Left.Should().Be(0);
    }

    [Theory]
    [InlineData("u9")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Hangup_ShouldChangeNoQueue_WhenNoQueueHoldsItsUniqueId(string? uniqueId)
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        using var counters = new LiveCounters();

        Hangup(uniqueId, "PJSIP/b-2");

        using var scope = new AssertionScope();
        Held().Should().Equal("u1");
        _left.Should().BeEmpty();
        counters.Left.Should().Be(0);
        counters.WaitSamples.Should().Be(0);
    }

    [Fact]
    public async Task Hangup_ShouldNotBringTheCallerBack_WhenASnapshotRowReadBeforeItIsApplied()
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        _queueStatus = [Params(), SnapshotEntry(Caller, "u1")];
        _sut.QueueRowBuffered = () =>
        {
            if (_sut.QueueRowBuffered is not null && BufferedEntry())
            {
                _sut.QueueRowBuffered = null;
                Hangup("u1", Caller);
            }
        };

        await _sut.RequestInitialStateAsync();

        using var scope = new AssertionScope();
        Held().Should().BeEmpty("the row was read before the caller hung up, so applying it would put back a caller who has gone");
        _left.Should().Equal(new LeftInHandler(Queue, "u1", Caller, LeaveNotObserved: true));
        _sut.Queues.ActiveSnapshotWindowCount.Should().Be(0);
    }

    [Fact]
    public async Task Hangup_ShouldKeepAnotherCallerOnTheSameName_WhenTheSnapshotListsItAfterTheHangup()
    {
        await _sut.StartAsync();
        Join(Caller, "u2");
        _queueStatus = [Params(), SnapshotEntry(Caller, "u2")];
        _sut.QueueRowBuffered = () =>
        {
            if (_sut.QueueRowBuffered is not null && BufferedEntry())
            {
                _sut.QueueRowBuffered = null;
                Hangup("u1", Caller);
            }
        };

        await _sut.RequestInitialStateAsync();

        using var scope = new AssertionScope();
        Held().Should().Equal(["u2"], "the Hangup of u1 fences u1, not the channel name u2 now holds");
        _left.Should().BeEmpty();
    }

    [Fact]
    public async Task Hangup_ShouldNotBeUndoneByASnapshotRowAppliedConcurrently()
    {
        await _sut.StartAsync();
        Join(Caller, "u1");
        _queueStatus = [Params(), SnapshotEntry(Caller, "u1")];
        Task? hangup = null;
        _sut.QueueRowBuffered = () =>
        {
            // The event pump reports the Hangup on its own thread while the load goes on to apply the row it read.
            if (_sut.QueueRowBuffered is not null && BufferedEntry())
            {
                _sut.QueueRowBuffered = null;
                hangup = Task.Run(() => Hangup("u1", Caller));
            }
        };

        await _sut.RequestInitialStateAsync().AsTask().WaitAsync(CleanupBound);
        await hangup!.WaitAsync(CleanupBound);

        using var scope = new AssertionScope();
        Held().Should().BeEmpty("the row was read before the caller hung up, so applying it would put back a caller who has gone");
        LeftSoFar().Should().Equal(new LeftInHandler(Queue, "u1", Caller, LeaveNotObserved: true));
    }

    // The buffered rows are Params then the entry: the entry is buffered on the second call.
    private int _buffered;

    private bool BufferedEntry() => ++_buffered == 2;

    private LeftInHandler[] LeftSoFar()
    {
        lock (_left)
            return [.. _left];
    }

    private string?[] Held() =>
        _sut.Queues.GetByName(Queue) is { } queue ? [.. queue.Entries.Values.Select(e => e.UniqueId)] : [];

    private void Join(string channel, string uniqueId) =>
        _observer!.OnNext(new QueueCallerJoinEvent
        {
            Position = 1,
            UniqueId = uniqueId,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = Queue,
                ["Channel"] = channel,
                ["CallerIDNum"] = "100",
            },
        });

    private void Leave(string channel, string uniqueId) =>
        _observer!.OnNext(new QueueCallerLeaveEvent
        {
            UniqueId = uniqueId,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = Queue,
                ["Channel"] = channel,
            },
        });

    private void Hangup(string? uniqueId, string channel) =>
        _observer!.OnNext(new HangupEvent { UniqueId = uniqueId, Channel = channel, Cause = 16 });

    private static QueueParamsEvent Params() => new() { Queue = Queue, Strategy = "ringall" };

    private static QueueEntryEvent SnapshotEntry(string channel, string uniqueId) => new()
    {
        Queue = Queue,
        Channel = channel,
        Uniqueid = uniqueId,
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

    private sealed record LeftInHandler(string Queue, string? UniqueId, string Channel, bool LeaveNotObserved);
}
