using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Tests.Shared.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// A caller a load or a reconnect reload finds in a queue is held with what the <c>QueueEntry</c> of the
/// <c>QueueStatus</c> snapshot reported for it: the calling number from <c>CallerIDNum</c>, the header Asterisk
/// sends (a <c>CallerID</c> header only when <c>CallerIDNum</c> is absent), and, when it leaves, a
/// <c>live.queue.wait_time</c> sample that counts the <c>Wait</c> Asterisk reported plus the time Live held it.
/// <see cref="AsteriskQueueEntry.JoinedAt"/> keeps its meaning: when Live handled the entry.
/// </summary>
/// <remarks>
/// Every <c>QueueEntry</c> captured from Asterisk 20.20.1, 22.9.0 and 23.4.1 carries <c>CallerIDNum</c> and no
/// <c>CallerID</c>; the snapshot entries here are shaped that way unless a test says otherwise. The wait samples
/// are read on the thread that removes the entry (<see cref="LiveQueueWaitSamples"/>), and bounded rather than
/// equal because the queue manager reads the wall clock.
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class QueueSnapshotWireTests : IAsyncLifetime
{
    private const string Queue = "q-late";
    private const string Caller = "PJSIP/far-00000001";
    private const string CallerNumber = "5552101";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly List<VerbaraServer> _servers = [];
    private readonly LiveQueueWaitSamples _samples = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _samples.Dispose();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    [Theory]
    [InlineData(CallerNumber, null, CallerNumber)]
    [InlineData(CallerNumber, "999", CallerNumber)]
    [InlineData(null, "5552100", "5552100")]
    [InlineData(null, null, null)]
    public async Task StartAsync_ShouldHoldTheCallerNumberAsteriskSent_WhenTheSnapshotEntryCarriesTheseHeaders(
        string? callerIdNum, string? callerId, string? expected)
    {
        var (server, _) = await StartAsync(SnapshotEntry(callerIdNum, callerId, wait: 3));

        server.Queues.GetByName(Queue)!.Entries[Caller].CallerId.Should().Be(expected,
            "the caller number is read from CallerIDNum, the header every captured Asterisk version sends, and from "
            + "a CallerID header only when CallerIDNum is absent");
    }

    [Fact]
    public async Task StartAsync_ShouldHoldTheSameCallerNumberAsALiveJoin_WhenBothReportTheSameCaller()
    {
        var (loaded, _) = await StartAsync(SnapshotEntry(CallerNumber, callerId: null, wait: 3));
        var (live, observer) = await StartAsync();
        observer.OnNext(LiveJoin(CallerNumber));

        var liveNumber = live.Queues.GetByName(Queue)!.Entries[Caller].CallerId;
        liveNumber.Should().Be(CallerNumber, "the live join reads CallerIDNum");
        loaded.Queues.GetByName(Queue)!.Entries[Caller].CallerId.Should().Be(liveNumber,
            "a caller admitted from a snapshot carries the number the same caller admitted live carries");
    }

    [Fact]
    public async Task OnCallerLeft_ShouldRecordTheReportedWaitPlusTheTimeHeld_WhenASnapshotEntryLeavesAtOnce()
    {
        var (server, _) = await StartAsync(SnapshotEntry(CallerNumber, callerId: null, wait: 3));

        var samples = _samples.During(() => server.Queues.OnCallerLeft(Queue, Caller));

        samples.Should().ContainSingle("one leave records one sample")
            .Which.Should().BeGreaterThanOrEqualTo(3000,
                "Asterisk reported the caller had waited 3 s when the snapshot was taken")
            .And.BeLessThan(4000, "the caller left as soon as the load finished");
    }

    [Fact]
    public async Task StartAsync_ShouldStampJoinedAtWhenLiveHandledTheEntry_WhenTheSnapshotEntryReportsAWait()
    {
        var before = DateTimeOffset.UtcNow;
        var (server, _) = await StartAsync(SnapshotEntry(CallerNumber, callerId: null, wait: 3));
        var after = DateTimeOffset.UtcNow;

        server.Queues.GetByName(Queue)!.Entries[Caller].JoinedAt.Should().BeOnOrAfter(before,
                "JoinedAt is when Live handled the entry; the reported Wait does not backdate it")
            .And.BeOnOrBefore(after);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-3L)]
    public async Task OnCallerLeft_ShouldRecordOnlyTheTimeHeld_WhenTheSnapshotEntryReportsNoUsableWait(long? wait)
    {
        var (server, _) = await StartAsync(SnapshotEntry(CallerNumber, callerId: null, wait));

        var samples = _samples.During(() => server.Queues.OnCallerLeft(Queue, Caller));

        samples.Should().ContainSingle("one leave records one sample")
            .Which.Should().BeLessThan(1000,
                "with no wait reported, or a negative one Asterisk does not send, the sample is the time Live held the "
                + "caller, as for a live join");
    }

    [Fact]
    public async Task OnCallerLeft_ShouldRaiseTheLeaveAndRecordOnlyTheTimeHeld_WhenTheReportedWaitIsBeyondWhatAClockCanHold()
    {
        var (server, _) = await StartAsync(SnapshotEntry(CallerNumber, callerId: null, wait: long.MaxValue));
        var left = new List<string>();
        server.Queues.CallerLeft += (_, entry) => left.Add(entry.Channel);

        var samples = _samples.During(() => server.Queues.OnCallerLeft(Queue, Caller));

        left.Should().Equal([Caller], "the leave is raised whatever wait the snapshot reported");
        server.Queues.GetByName(Queue)!.Entries.Should().NotContainKey(Caller);
        samples.Should().ContainSingle("one leave records one sample")
            .Which.Should().BeLessThan(1000,
                "a wait reaching back before the earliest instant a clock can hold is not one Asterisk measured");
    }

    [Fact]
    public void OnCallerLeft_ShouldRecordOnlyTheTimeHeld_WhenAnEntryNotFromASnapshotCarriesAReportedWait()
    {
        var queues = new QueueManager(NullLogger.Instance);
        queues.OnCallerJoined(Queue, Caller, CallerNumber, 1, fromSnapshot: false, reportedWaitSeconds: 3);

        var samples = _samples.During(() => queues.OnCallerLeft(Queue, Caller));

        samples.Should().ContainSingle("one leave records one sample")
            .Which.Should().BeLessThan(1000, "only a snapshot entry's reported wait counts");
    }

    [Fact]
    public async Task OnCallerLeft_ShouldRecordExactlyOneSampleOfTheTimeHeld_WhenALiveJoinLeavesAtOnce()
    {
        var (server, observer) = await StartAsync();
        observer.OnNext(LiveJoin(CallerNumber));

        var samples = _samples.During(() => server.Queues.OnCallerLeft(Queue, Caller));

        samples.Should().ContainSingle("one leave records one sample, and the listener sees it")
            .Which.Should().BeLessThan(1000, "a live join's sample is the time since Live handled the join");
    }

    private async Task<(VerbaraServer Server, IObserver<ManagerEvent> Observer)> StartAsync(
        params ManagerEvent[] queueStatus)
    {
        var connection = Substitute.For<IAmiConnection>();
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => observer = obs))
            .Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Answer(call.Arg<ManagerAction>() is QueueStatusAction ? queueStatus : []));
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        _servers.Add(server);
        await server.StartAsync();
        return (server, observer!);
    }

    private static QueueEntryEvent SnapshotEntry(string? callerIdNum, string? callerId, long? wait) => new()
    {
        Queue = Queue,
        Channel = Caller,
        Uniqueid = "1790633900.11",
        CallerIDNum = callerIdNum,
        CallerId = callerId,
        Position = 1,
        Wait = wait
    };

    private static QueueCallerJoinEvent LiveJoin(string callerIdNum) => new()
    {
        Position = 1,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = Queue,
            ["Channel"] = Caller,
            ["CallerIDNum"] = callerIdNum
        }
    };

    private static async IAsyncEnumerable<ManagerEvent> Answer(ManagerEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var evt in events)
            yield return evt;
    }
}
