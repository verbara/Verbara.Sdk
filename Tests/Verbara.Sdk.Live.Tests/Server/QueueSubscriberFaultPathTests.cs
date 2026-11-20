using System.Diagnostics.Metrics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The three ways a queue event reaches its subscribers through a server: a live AMI event, the start's load and a
/// reload. On each, a subscriber that throws is counted on <c>live.queues.subscriber_faults</c>, the other subscribers
/// and the server's own work go on, and nothing leaves the call: not the AMI observer, not <c>StartAsync</c>, not
/// <c>ReconcileQueuesAsync</c>. The counters are process-wide, so the class runs in <see cref="LiveCountersGroup"/>.
/// </summary>
[Collection(LiveCountersGroup.Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class QueueSubscriberFaultPathTests : IAsyncLifetime
{
    private const string Queue = "sales";
    private const string QueueFaults = "live.queues.subscriber_faults";
    private const string ObserverFaults = "ami.events.observer_faults";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private IObserver<ManagerEvent>? _observer;
    private ManagerEvent[] _queueStatus = [];

    public QueueSubscriberFaultPathTests()
    {
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs)).Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => Answer([]));
        _connection.SendEventGeneratingActionAsync(Arg.Is<ManagerAction>(a => a is QueueStatusAction), Arg.Any<CancellationToken>())
            .Returns(_ => Answer(_queueStatus));
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task EventObserver_ShouldReturnNormallyAndCountTheQueueFault_WhenACallerLeftHandlerThrowsOnALiveLeave()
    {
        await _server.StartAsync();
        _observer!.OnNext(Join("u1", "PJSIP/caller-1"));
        var reported = new List<string>();
        var laterLeft = 0;
        _server.Queues.CallerLeft += (_, _) => throw new InvalidOperationException("caller-left-handler");
        _server.Queues.CallerLeft += (_, _) => laterLeft++;
        _server.Queues.CallerLeaveReported += report => reported.Add(report.UniqueId);
        using var faults = new Faults();

        var thrown = Record.Exception(() => _observer.OnNext(Leave("u1", "PJSIP/caller-1")));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a CallerLeft subscriber's failure no longer reaches the AMI event observer");
        laterLeft.Should().Be(1, "the subscriber after the throwing one hears the leave");
        reported.Should().Equal(["u1"]);
        faults.Count(QueueFaults).Should().Be(1);
        faults.Count(ObserverFaults).Should().Be(0, "it is the queue subscriber's failure, not the AMI observer's");
    }

    [Fact]
    public async Task StartAsync_ShouldCompleteWithTheMembersLoaded_WhenAMemberAddedHandlerThrowsDuringTheLoad()
    {
        _queueStatus =
        [
            new QueueParamsEvent { Queue = Queue, Max = 0, Strategy = "ringall", Calls = 0 },
            new QueueMemberEvent { Queue = Queue, Interface = "PJSIP/a", MemberName = "a", Paused = false, Status = 1 },
            new QueueMemberEvent { Queue = Queue, Interface = "PJSIP/b", MemberName = "b", Paused = false, Status = 1 },
        ];
        var added = 0;
        _server.Queues.MemberAdded += (_, _) =>
        {
            added++;
            throw new InvalidOperationException("member-added-handler");
        };
        using var faults = new Faults();

        var thrown = await Record.ExceptionAsync(() => _server.StartAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a subscriber's failure during the load does not fail the start");
        added.Should().Be(2, "both members the listing reports are announced");
        _server.Queues.GetByName(Queue)?.Members.Keys.Should().BeEquivalentTo(["PJSIP/a", "PJSIP/b"]);
        faults.Count(QueueFaults).Should().Be(2);
    }

    [Fact]
    public async Task ReconcileQueuesAsync_ShouldCompleteAndRemoveTheGoneCaller_WhenACallerLeftHandlerThrows()
    {
        _queueStatus =
        [
            new QueueParamsEvent { Queue = Queue, Max = 0, Strategy = "ringall", Calls = 1 },
            new QueueEntryEvent { Queue = Queue, Position = 1, Channel = "PJSIP/caller-1", Uniqueid = "u1", CallerIDNum = "100", Wait = 1 },
        ];
        await _server.StartAsync();
        _server.Queues.GetByName(Queue)!.Entries.Should().ContainKey("PJSIP/caller-1", "premise: the start loaded the caller");
        var left = 0;
        _server.Queues.CallerLeft += (_, _) =>
        {
            left++;
            throw new InvalidOperationException("caller-left-handler");
        };
        _queueStatus = [new QueueParamsEvent { Queue = Queue, Max = 0, Strategy = "ringall", Calls = 0 }];
        using var faults = new Faults();

        var thrown = await Record.ExceptionAsync(() => _server.ReconcileQueuesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        using var scope = new AssertionScope();
        thrown.Should().BeNull("a subscriber's failure during a reload does not fail the reload");
        left.Should().Be(1);
        _server.Queues.GetByName(Queue)!.Entries.Should().BeEmpty("the reconciliation removed the caller the listing omits");
        faults.Count(QueueFaults).Should().Be(1);
    }

    private static QueueCallerJoinEvent Join(string uniqueId, string channel) => new()
    {
        UniqueId = uniqueId, Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = Queue, ["Channel"] = channel, ["Uniqueid"] = uniqueId, ["CallerIDNum"] = "100",
        },
    };

    private static QueueCallerLeaveEvent Leave(string uniqueId, string channel) => new()
    {
        UniqueId = uniqueId, Position = 1,
        RawFields = new Dictionary<string, string> { ["Queue"] = Queue, ["Channel"] = channel, ["Uniqueid"] = uniqueId },
    };

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] answer)
    {
        await Task.Yield();
        foreach (var evt in answer)
            yield return evt;
    }

    /// <summary>Sums the named counters across threads: the class runs alone, in <see cref="LiveCountersGroup"/>.</summary>
    private sealed class Faults : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _queue;
        private long _observer;

        public Faults()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is QueueFaults or ObserverFaults)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == QueueFaults)
                    Interlocked.Add(ref _queue, value);
                else
                    Interlocked.Add(ref _observer, value);
            });
            _listener.Start();
        }

        public long Count(string name) => name == QueueFaults ? Interlocked.Read(ref _queue) : Interlocked.Read(ref _observer);

        public void Dispose() => _listener.Dispose();
    }
}
