using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Live.Tests.Queues;

namespace Verbara.Sdk.Live.Tests.Server;

[Collection(LiveCountersGroup.Name)]
public sealed class StatusRefusalStateTests
{
    [Fact]
    public async Task LastStatusRefusal_ShouldKeepAnImmutableMessageAndUtcTime_WhenStatusIsRefusedAgain()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        run.Server.LastStatusRefusal.Should().BeNull();
        peer.StatusRefusedFromAsk = 2;
        var firstTime = run.Clock.GetUtcNow();
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var first = run.Server.LastStatusRefusal;
        first.Should().NotBeNull();
        first!.Message.Should().Be(BootingAsterisk.PermissionDenied);
        first.OccurredAt.Should().Be(firstTime);
        first.OccurredAt.Offset.Should().Be(TimeSpan.Zero);

        run.Clock.Advance(TimeSpan.FromSeconds(1));
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var second = run.Server.LastStatusRefusal;
        second.Should().NotBeSameAs(first);
        second!.Message.Should().Be(BootingAsterisk.PermissionDenied);
        second.OccurredAt.Should().Be(run.Clock.GetUtcNow());
        first.OccurredAt.Should().Be(firstTime, "a previously read refusal remains an immutable snapshot");
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task StatusRefused_ShouldCountEveryReaderAndKeepWarningPolicy_WhenLoadsAndReconciliationsAreRefused()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.ConnectAsync(peer);
        var log = new SignalingLogger<VerbaraServer> { MinimumLevel = LogLevel.Debug };
        await using var server = new VerbaraServer(run.Connection, log) { TimeProvider = run.Clock };
        await using var pool = NewPool();
        pool.AddExistingServer("n1", server);
        using var counter = new RefusalCounter();
        await server.StartAsync().WaitAsync(Run.Bound);
        await server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        for (var i = 0; i < 3; i++)
            await server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        counter.Measurements.Should().HaveCount(5);
        counter.Measurements.Should().OnlyContain(m => m.Value == 1 && m.Tags.Count == 2 && m.Tags["server.id"] == "n1");
        counter.Measurements.Count(m => m.Tags["live.status.reader"] == "load").Should().Be(2);
        counter.Measurements.Count(m => m.Tags["live.status.reader"] == "reconciliation").Should().Be(3);
        log.Entries.Where(e => e.Line.StartsWith("[LIVE] Status refused", StringComparison.Ordinal))
            .Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Warning, LogLevel.Warning, LogLevel.Debug, LogLevel.Debug);
        peer.Asked("Status").Should().Be(5);
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task StatusRefused_ShouldCountTheFirstLoadWithoutAnIdAndNeverReplayIt_WhenTheServerEntersAPoolLater()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.ConnectAsync(peer);
        using var counter = new RefusalCounter();
        await run.StartServerAsync();
        var refusal = run.Server.LastStatusRefusal;
        await using var pool = NewPool();
        pool.AddExistingServer("n1", run.Server);

        counter.Measurements.Should().ContainSingle();
        var first = counter.Measurements.Single();
        first.Value.Should().Be(1);
        first.Tags.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("live.status.reader", "load"));
        run.Server.LastStatusRefusal.Should().BeSameAs(refusal);
        refusal!.Message.Should().Be(BootingAsterisk.PermissionDenied);
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        counter.Measurements.Should().HaveCount(2, "joining the pool did not replay the original increment");
        counter.Measurements[^1].Tags.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["live.status.reader"] = "reconciliation", ["server.id"] = "n1",
        });
        peer.Fault.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastStatusRefusal_ShouldClear_WhenALaterStatusIsReadInFull(bool load)
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        peer.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        run.Server.LastStatusRefusal.Should().NotBeNull();
        peer.StatusRefusedFromAsk = 0;
        if (load)
            await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        else
            await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        run.Server.LastStatusRefusal.Should().BeNull();
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task LastStatusRefusal_ShouldClearBeforeTheBarrierAndStayClear_WhenTheReconciliationIsDeferred()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        var barrierClock = new FakeTimeProvider();
        await using var run = await Run.StartAsync(peer, barrierClock: barrierClock);
        await using var pool = NewPool();
        pool.AddExistingServer("n1", run.Server);
        var health = new VerbaraServerPoolHealthCheck(pool);
        peer.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        run.Server.LastStatusRefusal.Should().NotBeNull();
        peer.StatusRefusedFromAsk = 0;
        using var gate = new DispatchGate();
        using var subscription = run.Connection.Subscribe(gate);
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.FenceWaitObserved = (_, _) => waiting.TrySetResult();
        var completions = 0;
        run.Server.ChannelSnapshotCompleted += _ => Interlocked.Increment(ref completions);
        var snapshot = run.Server.ReconcileChannelsAsync().AsTask();
        try
        {
            await waiting.Task.WaitAsync(Run.Bound);
            run.Server.LastStatusRefusal.Should().BeNull("Status was fully read before the held handler can return");
            run.Server.ChannelTableComplete.Should().BeFalse();
            snapshot.IsCompleted.Should().BeFalse();
            while (!snapshot.IsCompleted)
            {
                using var stop = new CancellationTokenSource();
                var next = barrierClock.TimersCreated.ReadAsync(stop.Token).AsTask();
                var first = await Task.WhenAny(snapshot, next).WaitAsync(Run.Bound);
                await stop.CancelAsync();
                if (first == next && next.IsCompletedSuccessfully) barrierClock.Advance((await next).DueTime);
            }
            await snapshot.WaitAsync(Run.Bound);
            run.Server.LastStatusRefusal.Should().BeNull();
            run.Server.ChannelTableComplete.Should().BeFalse("a complete Status answer is independent of a complete table");
            Volatile.Read(ref completions).Should().Be(0);
            var result = await health.CheckHealthAsync(new HealthCheckContext());
            result.Status.Should().Be(HealthStatus.Healthy, "a deferred application does not retain a completed Status refusal");
            result.Data.Should().NotContainKey("status-refused");
        }
        finally
        {
            gate.Release();
            await snapshot.WaitAsync(Run.Bound);
        }
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task LastStatusRefusal_ShouldRetainStateAndCounter_WhenTheNextReadIsCancelled()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        using var counter = new RefusalCounter();
        peer.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var refusal = run.Server.LastStatusRefusal;
        peer.StatusRefusedFromAsk = 0;
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusAnsweredAfter = answer.Task;
        using var cts = new CancellationTokenSource();
        var snapshot = run.Server.ReconcileChannelsAsync(cts.Token).AsTask();
        try
        {
            await peer.AskedAtLeast("Status", 3).WaitAsync(Run.Bound);
            await cts.CancelAsync();
            answer.TrySetResult();
            var error = await Record.ExceptionAsync(() => snapshot.WaitAsync(Run.Bound));
            error.Should().BeAssignableTo<OperationCanceledException>();
            run.Server.LastStatusRefusal.Should().BeSameAs(refusal);
            counter.Measurements.Should().ContainSingle();
        }
        finally { answer.TrySetResult(); }
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task LastStatusRefusal_ShouldRetainStateAndCounter_WhenTheNextReadLosesItsAmiSession()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel("x", "PJSIP/x", "x")] };
        await using var run = await Run.StartAsync(peer);
        using var counter = new RefusalCounter();
        peer.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var refusal = run.Server.LastStatusRefusal;
        peer.StatusRefusedFromAsk = 0;
        peer.Close = PeerClose.DuringStatus;
        peer.StatusChannelsBeforeClose = 1;
        var error = await Record.ExceptionAsync(() => run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound));
        error.Should().BeOfType<AmiNotConnectedException>();
        run.Server.LastStatusRefusal.Should().BeSameAs(refusal);
        counter.Measurements.Should().ContainSingle();
        peer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task LastStatusRefusal_ShouldRetainStateUntilTheReloadAnswers_WhenTheConnectionReconnects()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = answer.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        using var counter = new RefusalCounter();
        first.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var refusal = run.Server.LastStatusRefusal;
        run.ReleaseSecondPeer();
        first.CloseSession();
        try
        {
            await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            run.Server.LastStatusRefusal.Should().BeSameAs(refusal, "reconnection alone is not a completed Status answer");
            counter.Measurements.Should().ContainSingle();
            answer.TrySetResult();
            await second.AskedAtLeast("Agents", 1).WaitAsync(Run.Bound);
            run.Server.LastStatusRefusal.Should().BeNull();
            counter.Measurements.Should().ContainSingle("a successful reload added no refusal");
        }
        finally { answer.TrySetResult(); }
        first.Fault.Should().BeNull();
        second.Fault.Should().BeNull();
    }

    private static VerbaraServerPool NewPool() =>
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    private sealed record Measurement(long Value, IReadOnlyDictionary<string, string> Tags);

    private sealed class RefusalCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public RefusalCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Live" && instrument.Name == "live.status.refused")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags) values.Add(tag.Key, (string)tag.Value!);
                _measurements.Enqueue(new Measurement(value, values));
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }
}
