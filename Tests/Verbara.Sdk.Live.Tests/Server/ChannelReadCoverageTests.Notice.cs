using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The public reading of a delivered loss no covering read has covered (<see cref="VerbaraServer.HasUnreconciledLoss"/>)
/// and the notice of each moment it turns true (<see cref="VerbaraServer.UnreconciledLossDelivered"/>): raised off the
/// connection's reader and outside any event dispatch, once per loss mark, its subscribers isolated from one another.
/// </summary>
/// <remarks>
/// The same rig as the coverage tests: a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> with an event buffer
/// of <see cref="Capacity"/>, a drop made by holding the dispatch and filling the buffer behind it, and delivered once a
/// tail event written after the release has been dispatched. A notice is read from a channel the handler writes to, so
/// every wait ends on a signal, bounded by <see cref="Run.Bound"/>.
/// </remarks>
public sealed partial class ChannelReadCoverageTests
{
    private const string NoticeSubscriberFailed = "[LIVE] An UnreconciledLossDelivered subscriber threw";

    // ---- The public reading ----

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadTrue_WhenADropIsDeliveredThroughAndNotYetCovered()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var beforeTheDrop = run.Server.HasUnreconciledLoss;

        using var hold = await HoldAndDropAsync(run, peer, "public-1");
        var behindTheBacklog = run.Server.HasUnreconciledLoss;
        await DeliverAsync(run, peer, hold, "public-1");

        using (new AssertionScope())
        {
            beforeTheDrop.Should().BeFalse("nothing was lost");
            behindTheBacklog.Should().BeFalse("an event received before the drop is still buffered: the drop is not delivered");
            run.Server.HasUnreconciledLoss.Should().BeTrue("the drop is delivered through and no read has covered it");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadFalseAfterTheCall_WhenACoveringReconciliationRuns()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "public-2"))
            await DeliverAsync(run, peer, hold, "public-2");
        var before = run.Server.HasUnreconciledLoss;

        var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            before.Should().BeTrue("premise: the delivered drop is unreconciled");
            Tag(span, Covered).Should().Be("True", "premise: the read covered");
            run.Server.HasUnreconciledLoss.Should().BeFalse("a covering reconciliation clears it");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldStayTrue_WhenTheReconciliationDoesNotCover()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using (var hold = await HoldAndDropAsync(run, peer, "public-3"))
            await DeliverAsync(run, peer, hold, "public-3");
        var before = run.Server.HasUnreconciledLoss;

        peer.StatusRefusedFromAsk = peer.Asked("Status") + 1;
        var span = await spans.TraceAsync(() => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            before.Should().BeTrue("premise: the delivered drop is unreconciled");
            Tag(span, Covered).Should().Be("False", "premise: a refused Status covers nothing");
            run.Server.HasUnreconciledLoss.Should().BeTrue("a reconciliation that did not cover leaves it true");
        }
    }

    [Fact]
    public async Task HasUnreconciledLoss_ShouldReadFalse_WhenTheServerRunsOverAConnectionThatIsNotTheSdksOwn()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var substitute = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection),
            NullLogger<VerbaraServer>.Instance);
        await substitute.StartAsync().WaitAsync(Run.Bound);

        using (var hold = await HoldAndDropAsync(run, peer, "public-4"))
            await DeliverAsync(run, peer, hold, "public-4");

        using (new AssertionScope())
        {
            run.Server.HasUnreconciledLoss.Should().BeTrue("premise: the connection lost an event and delivered through it");
            substitute.HasUnreconciledLoss.Should().BeFalse("over a substitute connection no loss is ever reported");
        }
    }

    // ---- The notice ----

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnceNamingTheServer_WhenSeveralDropsAreDeliveredThroughTogether()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        using (var hold = await HoldAndDropAsync(run, peer, "notice-1"))
        {
            await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.8", "PJSIP/lost-00000008", "1790800000.8"));
            await FenceAsync(run);
            await DeliverAsync(run, peer, hold, "notice-1");
        }
        var first = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        // A further event delivered and read back: a second notice for the same drops would have had its turn.
        using (var hold = await HoldAsync(run, peer, "notice-1-after"))
            hold.Release();
        await FenceAsync(run);

        using (new AssertionScope())
        {
            first.Server.Should().BeSameAs(run.Server, "the notice names its server");
            first.Reading.Should().BeTrue("the reading is true when the notice is raised");
            notices.Reader.TryRead(out _).Should().BeFalse("the two drops were delivered through together: one notice");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedAgain_WhenALaterDropIsDeliveredThrough()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        using (var hold = await HoldAndDropAsync(run, peer, "again-1"))
            await DeliverAsync(run, peer, hold, "again-1");
        var first = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);
        using (var hold = await HoldAndDropAsync(run, peer, "again-2"))
            await DeliverAsync(run, peer, hold, "again-2");
        var second = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            first.Reading.Should().BeTrue();
            second.Reading.Should().BeTrue("a later drop, delivered through with no reconciliation since, is announced again");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaised_WhileAnEventReceivedBeforeTheDropIsStillBuffered()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var notices = Notices(run.Server);

        var hold = await HoldAndDropAsync(run, peer, "backlog-1");
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup("1790800000.7", "PJSIP/lost-00000007", "1790800000.7"));
        await FenceAsync(run);
        var whileHeld = notices.Reader.TryRead(out _);
        var readingWhileHeld = run.Server.HasUnreconciledLoss;
        await DeliverAsync(run, peer, hold, "backlog-1");
        hold.Dispose();
        var once = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            whileHeld.Should().BeFalse("delivery has not passed the drops: nothing to reconcile yet");
            readingWhileHeld.Should().BeFalse();
            once.Reading.Should().BeTrue("premise: once delivery passes the drops, the notice comes");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedWithTheReadingTrue_WhenAPendingReconnectSettlesWithALossItsReloadDidNotCover()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var notices = Notices(run.Server);

        first.CloseSession();
        run.ReleaseSecondPeer();
        await run.ServerLog.Logged(StateLoaded, 2).WaitAsync(Run.Bound);
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            notice.Server.Should().BeSameAs(run.Server);
            notice.Reading.Should().BeTrue("the session change is a loss the refused reload did not cover");
            run.Server.HasUnreconciledLoss.Should().BeTrue("nothing has covered it since");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldBeRaisedOnlyByTheServerThatLostTheEvent_WhenTwoServersOfAPoolRun()
    {
        var lossyPeer = new BootingAsterisk { BootedAtLogin = true };
        var quietPeer = new BootingAsterisk { BootedAtLogin = true };
        await using var lossy = await StartAsync(lossyPeer);
        await using var quiet = await StartAsync(quietPeer);
        lossy.Server.PoolId = "n1";
        quiet.Server.PoolId = "n2";
        var fromLossy = Notices(lossy.Server);
        var fromQuiet = Notices(quiet.Server);

        using (var hold = await HoldAndDropAsync(lossy, lossyPeer, "pool-1"))
            await DeliverAsync(lossy, lossyPeer, hold, "pool-1");
        var notice = await fromLossy.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            notice.Server.Should().BeSameAs(lossy.Server);
            fromQuiet.Reader.TryRead(out _).Should().BeFalse("the other server lost nothing");
            quiet.Server.HasUnreconciledLoss.Should().BeFalse();
            lossy.Server.HasUnreconciledLoss.Should().BeTrue();
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldLetAReconciliationStartedFromItCover_WhenNoOtherReadIsInFlight()
    {
        using var spans = new Spans();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var started = new TaskCompletionSource<Task<Activity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.UnreconciledLossDelivered += server =>
            started.TrySetResult(spans.TraceAsync(() => server.ReconcileChannelsAsync().AsTask()));

        using (var hold = await HoldAndDropAsync(run, peer, "from-notice-1"))
            await DeliverAsync(run, peer, hold, "from-notice-1");
        var span = await (await started.Task.WaitAsync(Run.Bound)).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            Tag(span, Covered).Should().Be("True",
                "the notice runs outside any event dispatch, so the reconciliation waits for the backlog and covers");
            run.Server.HasUnreconciledLoss.Should().BeFalse("the drop is covered afterwards");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldHoldNoDelivery_WhenAHandlerDoesNotReturn()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        run.Server.UnreconciledLossDelivered += _ =>
        {
            entered.Set();
            release.Wait(Run.Bound);
        };

        using (var hold = await HoldAndDropAsync(run, peer, "held-notice-1"))
            await DeliverAsync(run, peer, hold, "held-notice-1");
        var handlerHeld = entered.Wait(Run.Bound);

        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = run.Connection.Subscribe(new Observer(e =>
        {
            if (e is UserEventEvent { UserEvent: "after-held-notice" })
                later.TrySetResult();
        }));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame("after-held-notice"));
        var delivered = await Task.WhenAny(later.Task, Task.Delay(Run.Bound)) == later.Task;
        var pingAnswered = await Record.ExceptionAsync(() => FenceAsync(run));
        release.Set();

        using (new AssertionScope())
        {
            handlerHeld.Should().BeTrue("premise: the handler is held");
            delivered.Should().BeTrue("a held notice handler delays no event delivery");
            pingAnswered.Should().BeNull("nor the connection's reading of its answers");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldNotBeRaised_WhenTheServersDisposalHasBegun()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var witness = new VerbaraServer(run.Connection, NullLogger<VerbaraServer>.Instance);
        await witness.StartAsync().WaitAsync(Run.Bound);
        var fromDisposed = Notices(run.Server);
        var fromWitness = Notices(witness);

        var hold = await HoldAndDropAsync(run, peer, "disposed-1");
        await run.Server.DisposeAsync();
        await DeliverAsync(run, peer, hold, "disposed-1");
        hold.Dispose();
        await fromWitness.Reader.ReadAsync().AsTask().WaitAsync(Run.Bound);

        fromDisposed.Reader.TryRead(out _).Should().BeFalse(
            "the same signal reached the witness, and a server whose disposal has begun raises nothing");
    }

    // ---- Subscriber isolation ----

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldStillTellTheOtherSubscribersAndCountTheFailure_WhenOneThrows()
    {
        using var faults = new FaultCount();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.UnreconciledLossDelivered += _ => throw new InvalidOperationException("first subscriber");
        run.Server.UnreconciledLossDelivered += _ => second.TrySetResult();
        run.Server.UnreconciledLossDelivered += _ => third.TrySetResult();

        using (var hold = await HoldAndDropAsync(run, peer, "throws-1"))
            await DeliverAsync(run, peer, hold, "throws-1");
        await third.Task.WaitAsync(Run.Bound);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Ghost, GhostChannel, Ghost));
        await FenceAsync(run);
        var failures = run.ServerLog.Entries.Where(e => e.Line.Contains(NoticeSubscriberFailed, StringComparison.Ordinal)).ToList();

        using (new AssertionScope())
        {
            second.Task.IsCompletedSuccessfully.Should().BeTrue("the subscriber after the one that threw is still told");
            failures.Should().ContainSingle("the failure is reported once").Which.Level.Should().Be(LogLevel.Warning);
            faults.Value.Should().Be(1, "the failure is counted once");
            run.Server.Channels.GetByUniqueId(Ghost).Should().NotBeNull("the server keeps delivering events");
        }
    }

    [Fact]
    public async Task UnreconciledLossDelivered_ShouldStillTellTheLaterSubscribers_WhenOneThrowsAndTheLoggerThrowsOnItsReport()
    {
        using var faults = new FaultCount();
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await StartAsync(peer);
        await using var server = new VerbaraServer(run.Connection, new ThrowingOnWarningLogger());
        await server.StartAsync().WaitAsync(Run.Bound);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UnreconciledLossDelivered += _ => throw new InvalidOperationException("first subscriber");
        server.UnreconciledLossDelivered += _ => later.TrySetResult();

        using (var hold = await HoldAndDropAsync(run, peer, "logger-throws-1"))
            await DeliverAsync(run, peer, hold, "logger-throws-1");
        await later.Task.WaitAsync(Run.Bound);

        faults.Value.Should().Be(1, "the failure is counted before the report the logger fails on");
    }

    // ---- The rig ----

    private static Channel<(VerbaraServer Server, bool Reading)> Notices(VerbaraServer server)
    {
        var notices = Channel.CreateUnbounded<(VerbaraServer Server, bool Reading)>();
        server.UnreconciledLossDelivered += s => notices.Writer.TryWrite((s, s.HasUnreconciledLoss));
        return notices;
    }

    /// <summary>A logger whose Warning lines throw, as a host's broken logging pipeline may.</summary>
    private sealed class ThrowingOnWarningLogger : ILogger<VerbaraServer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                throw new InvalidOperationException("the host's logger failed");
        }
    }

    /// <summary>The measurements of <c>live.server.subscriber_faults</c> taken while it is alive.</summary>
    private sealed class FaultCount : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _value;

        public FaultCount()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Live" && instrument.Name == "live.server.subscriber_faults")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref _value, measurement));
            _listener.Start();
        }

        public long Value => Interlocked.Read(ref _value);

        public void Dispose() => _listener.Dispose();
    }
}
