using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A server is disposed once, whoever asks and however many times, and a reconnect never runs against a disposal.
/// </summary>
public sealed class ServerDisposedOnceTests
{
    private const string ReloadStarted = "[LIVE] Reconnected: reloading state";
    private const string StateLoaded = "[LIVE] State loaded";
    /// <summary>How long a handler blocks on a disposal: under <see cref="Run.Bound"/>, so a hang fails its assertion.</summary>
    private static readonly TimeSpan InsideBound = TimeSpan.FromSeconds(5);
    private const string StrayUid = "1790800000.9";
    private const string StrayChannel = "PJSIP/stray-00000009";
    private const string ListedUid = "1790800000.1";
    private const string ListedChannel = "PJSIP/listed-00000001";

    [Fact]
    public async Task DisposeAsync_ShouldReleaseNothingAgain_WhenCalledASecondTime()
    {
        var subscription = Substitute.For<IDisposable>();
        var server = NewServer(subscription, out _);
        var ari = Substitute.For<IAriClient>();
        server.SetAriClient(ari);
        await server.StartAsync();

        await server.DisposeAsync();
        await server.DisposeAsync();

        using (new AssertionScope())
        {
            DisposeCalls(subscription).Should().Be(1, "a second disposal releases nothing again");
            DisposeCalls(ari).Should().Be(1, "a second disposal releases nothing again");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReturnNormallyAndReleaseNothingAgain_WhenTheFirstDisposalThrew()
    {
        var server = NewServer(Substitute.For<IDisposable>(), out _);
        var ari = Substitute.For<IAriClient>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ => throw new InvalidOperationException("ari"));
#pragma warning restore CA2012
        server.SetAriClient(ari);
        await server.StartAsync();
        var first = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        var second = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            first.Should().BeOfType<InvalidOperationException>("the first caller is told");
            second.Should().BeNull("the failure was reported once, to the disposal that met it");
            DisposeCalls(ari).Should().Be(1, "the ARI client is not disposed again");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseOnceAndReturnAfterTheFirstHasFinished_WhenTwoCallsRace()
    {
        var server = NewServer(Substitute.For<IDisposable>(), out _);
        var ari = Substitute.For<IAriClient>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ =>
        {
            entered.TrySetResult();
            return new ValueTask(hold.Task);
        });
#pragma warning restore CA2012
        server.SetAriClient(ari);
        await server.StartAsync();

        var first = server.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(Run.Bound);
        var second = server.DisposeAsync().AsTask();
        var secondFinishedBeforeTheFirst = second.IsCompleted;
        hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            DisposeCalls(ari).Should().Be(1, "two racing disposals release once");
            secondFinishedBeforeTheFirst.Should().BeFalse("the second call returns once the release has finished");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldRunEveryRelease_WhenTheConnectionsRemoveAccessorThrows()
    {
        using var instruments = new ServerInstruments();
        var subscription = Substitute.For<IDisposable>();
        var server = NewServer(subscription, out var connection);
        var ari = Substitute.For<IAriClient>();
        server.SetAriClient(ari);
        var failure = new InvalidOperationException("remove");
        connection.When(c => c.Reconnected -= Arg.Any<Action>()).Do(_ => throw failure);
        await instruments.StartAsync(server);

        var thrown = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            thrown.Should().BeSameAs(failure);
            DisposeCalls(subscription).Should().Be(1, "the subscription is released whatever the accessor threw");
            DisposeCalls(ari).Should().Be(1, "the ARI client is released whatever the accessor threw");
            instruments.Published.Should().BePositive();
            instruments.Completed.Should().Be(instruments.Published, "every instrument the server published is released");
        }
    }

    /// <summary>
    /// OnReconnected has passed the point where it disposes the old subscription when the disposal runs. A substitute's
    /// disposal completes inline, so the disposal has run when the call returns.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldLeaveNoSubscription_WhenOnReconnectedIsReplacingItsSubscription()
    {
        var old = Substitute.For<IDisposable>();
        var fresh = Substitute.For<IDisposable>();
        var server = NewServer(old, out var connection);
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(old, fresh);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        old.When(s => s.Dispose()).Do(_ =>
        {
            if (Interlocked.Increment(ref disposals) != 1)
                return;
            entered.TrySetResult();
            hold.Task.Wait(Run.Bound);
        });
        await server.StartAsync();

        var reconnect = Task.Run(() => connection.Reconnected += Raise.Event<Action>());
        await entered.Task.WaitAsync(Run.Bound);
        var disposal = server.DisposeAsync().AsTask();
        hold.SetResult();
        await Task.WhenAll(reconnect, disposal).WaitAsync(Run.Bound);

        DisposeCalls(fresh).Should().Be(1, "the subscription the reconnect made has an owner once the disposal returned");
    }

    /// <summary>
    /// The same race with no hold, for the widened-window control: the disposal starts when the old subscription's
    /// disposal is entered, so it loses the race only when a delay widens the window between that and the swap.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldLeaveNoSubscription_WhenItStartsAsTheOldSubscriptionIsReleased()
    {
        var old = Substitute.For<IDisposable>();
        var fresh = Substitute.For<IDisposable>();
        var server = NewServer(old, out var connection);
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(old, fresh);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.When(s => s.Dispose()).Do(_ => entered.TrySetResult());
        await server.StartAsync();

        var reconnect = Task.Run(() => connection.Reconnected += Raise.Event<Action>());
        await entered.Task.WaitAsync(Run.Bound);
        await server.DisposeAsync().AsTask().WaitAsync(Run.Bound);
        await reconnect.WaitAsync(Run.Bound);

        DisposeCalls(fresh).Should().Be(1, "the subscription the reconnect made has an owner once the disposal returned");
    }

    /// <summary>
    /// The connection reads its handlers at delivery, so a handler removed after that read is still called. A consumer's
    /// slow Reconnected handler ahead of the server's holds that delivery while the server is disposed.
    /// </summary>
    [Fact]
    public async Task OnReconnected_ShouldDoNothing_WhenItIsDeliveredAfterTheDisposalReturned()
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        // The second session lists no agent: a reload of it would leave the agent the start loaded no longer held.
        var second = new BootingAsterisk { BootedAtLogin = true, Agents = [] };
        await using var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Connection.Reconnected += () =>
        {
            held.TrySetResult();
            hold.Task.Wait(Run.Bound);
        };
        await run.StartServerAsync();
        run.Connection.Reconnected += () => after.TrySetResult();
        var agentBefore = run.Server.Agents.GetById(BootingAsterisk.AgentId);

        first.CloseSession();
        run.ReleaseSecondPeer();
        await held.Task.WaitAsync(Run.Bound);
        await run.Server.DisposeAsync();
        hold.SetResult();
        await after.Task.WaitAsync(Run.Bound);

        // A subscriber taken after the server's handler ran: an observer that handler subscribed is ahead of it, so by the
        // time this one sees the Newchannel, that observer has been handed it too.
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = run.Connection.Subscribe(new AmiObserver(e =>
        {
            if (e is NewChannelEvent { UniqueId: StrayUid })
                seen.TrySetResult();
        }));
        (await second.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(StrayUid, StrayChannel, StrayUid)))
            .Should().BeTrue("the second session is live");
        await seen.Task.WaitAsync(Run.Bound);
        // The reload, had one run, ends on the agents it replaced; give it no chance to be read half done.
        SpinWait.SpinUntil(() => !run.Server.IsLoadInFlight, Run.Bound).Should().BeTrue();

        using (new AssertionScope())
        {
            // Both are written before the handler's first await, so they are there by the time the handler after it ran.
            run.ServerLog.Entries.Count(e => e.Line.Contains(ReloadStarted, StringComparison.Ordinal)).Should().Be(0,
                "a server already disposed neither subscribes again nor reloads");
            agentBefore.Should().NotBeNull("the start loaded the agent the first session lists");
            run.Server.Agents.GetById(BootingAsterisk.AgentId).Should().BeSameAs(agentBefore,
                "the agent the server held is still held: nothing was reloaded into it");
            run.Server.Channels.GetByUniqueId(StrayUid).Should().BeNull(
                "a Newchannel of the second session reaches no table of a disposed server");
            second.Asked("Agents").Should().Be(0, "nothing is asked of the second session");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldEndTheReloadOfAReconnect_WhenItIsReadingAList()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = hold.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);

        first.CloseSession();
        run.ReleaseSecondPeer();
        await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
        // Bounded under Run.Bound, past which the peer gives up on the answer, and the answer released either way: a
        // reload that ignores the disposal must fail here, not hang here or in the run's own disposal.
        var disposal = run.Server.DisposeAsync().AsTask();
        var returnedWithoutTheAnswer = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(3))) == disposal;
        hold.SetResult();
        await disposal.WaitAsync(Run.Bound);
        SpinWait.SpinUntil(() => !run.Server.IsLoadInFlight, Run.Bound).Should().BeTrue();

        using (new AssertionScope())
        {
            returnedWithoutTheAnswer.Should().BeTrue("the reload's read ends with the disposal, whatever Asterisk does");
            second.Asked("QueueStatus").Should().Be(0, "nothing more is asked of Asterisk once the server is disposed");
            run.ServerLog.Entries.Count(e => e.Line.Contains(StateLoaded, StringComparison.Ordinal)).Should().Be(1,
                "only the start's load completed: nothing is reconciled into a disposed server");
        }
    }

    /// <summary>
    /// The reload removes the channel the second session no longer lists, and a <c>ChannelRemoved</c> handler, inside that
    /// reload, disposes the server: blocking on it or not, the disposal returns inside the handler.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_ShouldReturnInsideTheHandler_WhenAChannelRemovedHandlerOfTheReloadCallsIt(bool blocking)
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(ListedUid, ListedChannel, ListedUid)],
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelRemoved += _ =>
        {
            var disposal = run.Server.DisposeAsync().AsTask();
#pragma warning disable VSTHRD002 // a handler that blocks on the disposal is the case under test
            returned.TrySetResult(!blocking || disposal.Wait(InsideBound));
#pragma warning restore VSTHRD002
        };

        first.CloseSession();
        run.ReleaseSecondPeer();

        (await returned.Task.WaitAsync(Run.Bound)).Should().BeTrue("the disposal called inside the reload returns inside the handler");
    }

    /// <summary>
    /// A <c>ConnectionLost</c> handler blocks on the disposal while the reload of the session before is still reading a
    /// list: the loss is delivered outside that reload, and the disposal ends it.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldReturn_WhenAConnectionLostHandlerBlocksOnItWhileAReloadIsInFlight()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new BootingAsterisk { BootedAtLogin = true };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusAnsweredAfter = hold.Task };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var losses = 0;
        run.Server.ConnectionLost += _ =>
        {
            if (Interlocked.Increment(ref losses) == 2)
            {
#pragma warning disable VSTHRD002 // a handler that blocks on the disposal is the case under test
                returned.TrySetResult(run.Server.DisposeAsync().AsTask().Wait(InsideBound));
#pragma warning restore VSTHRD002
            }
        };

        try
        {
            first.CloseSession();
            run.ReleaseSecondPeer();
            await second.AskedAtLeast("Status", 1).WaitAsync(Run.Bound);
            second.CloseSession();

            (await returned.Task.WaitAsync(Run.Bound)).Should().BeTrue("the disposal a loss handler blocks on returns");
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    /// <summary>
    /// A first disposal, from outside the reload, waits for it; a <c>ChannelRemoved</c> handler inside that reload then
    /// blocks on a second disposal, which returns without joining the first.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldReturnInsideTheHandler_WhenAChannelRemovedHandlerCallsItWhileAFirstDisposalWaitsForTheReload()
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(ListedUid, ListedChannel, ListedUid)],
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelRemoved += _ =>
        {
            entered.TrySetResult();
#pragma warning disable VSTHRD002 // a handler that blocks on the disposal is the case under test
            outer.Task.Wait(Run.Bound);
            returned.TrySetResult(run.Server.DisposeAsync().AsTask().Wait(InsideBound));
#pragma warning restore VSTHRD002
        };

        try
        {
            first.CloseSession();
            run.ReleaseSecondPeer();
            await entered.Task.WaitAsync(Run.Bound);
            var disposal = run.Server.DisposeAsync().AsTask();
            outer.SetResult();

            (await returned.Task.WaitAsync(Run.Bound)).Should().BeTrue("the second disposal returns inside the handler");
            await disposal.WaitAsync(Run.Bound);
        }
        finally
        {
            outer.TrySetResult();
        }
    }

    /// <summary>
    /// A load and a channel reconciliation the caller asks of a server already disposed still run, as they always did: the
    /// disposal ends what is in flight, not what is asked after it.
    /// </summary>
    [Fact]
    public async Task RequestInitialStateAsync_ShouldStillLoadAndReconcile_WhenTheServerIsAlreadyDisposed()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await run.Server.DisposeAsync();

        peer.StatusChannels = [new StatusChannel(ListedUid, ListedChannel, ListedUid)];
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        var loaded = run.Server.Channels.GetByUniqueId(ListedUid) is not null;
        peer.StatusChannels = [];
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            loaded.Should().BeTrue("the load asked of a disposed server ran and admitted the listed channel");
            run.Server.Channels.GetByUniqueId(ListedUid).Should().BeNull(
                "the reconciliation asked of a disposed server ran and removed the channel no longer listed");
            run.ServerLog.Entries.Count(e => e.Line.Contains(StateLoaded, StringComparison.Ordinal)).Should().Be(2,
                "the start's load and the one asked after the disposal");
            peer.Asked("Status").Should().Be(3, "the start, the load and the reconciliation each read the channels");
        }
    }

    /// <summary>
    /// A <c>ChannelRemoved</c> handler inside the reload disposes the server, which runs the releases without waiting for
    /// that reload, and starts a task that disposes again once the reload has ended. That task carries the reload's flow,
    /// yet is an ordinary caller by then: it joins the first disposal, still in its ARI release.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldJoinTheFirstDisposal_WhenATaskAHandlerOfTheReloadStartedCallsItOnceTheReloadEnded()
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(ListedUid, ListedChannel, ListedUid)],
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var ari = Substitute.For<IAriClient>();
        var ariEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ariHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ =>
        {
            ariEntered.TrySetResult();
            return new ValueTask(ariHold.Task);
        });
#pragma warning restore CA2012
        run.Server.SetAriClient(ari);
        var inside = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outlives = new TaskCompletionSource<Task<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelRemoved += _ =>
        {
            inside.TrySetResult(run.Server.DisposeAsync().AsTask());
            var reload = run.Server.ReconnectReload;
            outlives.TrySetResult(Task.Run(async () =>
            {
                await reload;
                return run.Server.DisposeAsync().AsTask();
            }));
        };

        try
        {
            first.CloseSession();
            run.ReleaseSecondPeer();
            var insideDisposal = await inside.Task.WaitAsync(Run.Bound);
            await ariEntered.Task.WaitAsync(Run.Bound);
            var joined = await (await outlives.Task.WaitAsync(Run.Bound)).WaitAsync(Run.Bound);
            // The ARI release is held, so the first disposal has not ended: a call that joined it cannot have returned.
            var returnedDuringTheRelease = joined.IsCompleted;
            ariHold.SetResult();
            await Task.WhenAll(insideDisposal, joined).WaitAsync(Run.Bound);

            using (new AssertionScope())
            {
                returnedDuringTheRelease.Should().BeFalse("a call from a task that outlives the reload joins the first disposal");
                DisposeCalls(ari).Should().Be(1, "the ARI client is released once");
            }
        }
        finally
        {
            ariHold.TrySetResult();
        }
    }

    /// <summary>
    /// The first disposal is the handler's, inside the reload, and leaves that reload out of its wait. A disposal from outside
    /// the reload joins it and still returns only after the reload has ended.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldReturnAfterTheReloadEnded_WhenItJoinsADisposalCalledInsideThatReload()
    {
        var first = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels = [new StatusChannel(ListedUid, ListedChannel, ListedUid)],
        };
        var second = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var innerReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelRemoved += _ =>
        {
#pragma warning disable VSTHRD002 // a handler that blocks on the disposal, then holds the reload, is the case under test
            // The first disposal, inside the reload: it returns here, without waiting for this reload.
            innerReturned.TrySetResult(run.Server.DisposeAsync().AsTask().Wait(InsideBound));
            // Then hold the reload: its reconciliation has not ended while this handler has not returned.
            hold.Task.Wait(Run.Bound);
#pragma warning restore VSTHRD002
        };

        try
        {
            first.CloseSession();
            run.ReleaseSecondPeer();
            (await innerReturned.Task.WaitAsync(Run.Bound)).Should().BeTrue("the disposal called inside the reload returns inside the handler");

            // The second disposal, from the test's own flow: no reload in its flow, so it joins the first.
            var outer = run.Server.DisposeAsync().AsTask();
            // The reload cannot end while the handler is held, so completing here is the defect, not a timing accident; the
            // delay only bounds how long the test looks.
            var returnedWhileTheReloadRan = await Task.WhenAny(outer, Task.Delay(TimeSpan.FromMilliseconds(500))) == outer;
            hold.SetResult();
            await outer.WaitAsync(Run.Bound);
            var loadStillInFlight = run.Server.IsLoadInFlight;

            using (new AssertionScope())
            {
                returnedWhileTheReloadRan.Should().BeFalse("a disposal from outside the reload returns only after the reload has ended");
                loadStillInFlight.Should().BeFalse("the reload had ended when the joined disposal returned");
                second.Asked("QueueStatus").Should().Be(0, "nothing more is asked of Asterisk once the server is disposed");
            }
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    private sealed class AmiObserver(Action<ManagerEvent> onNext) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The connection does not fault in these tests; nothing to do.
        }

        public void OnCompleted()
        {
            // Completed when the subscription is disposed; nothing to do.
        }
    }

    private static int DisposeCalls(object substitute) =>
        substitute.ReceivedCalls().Count(c => c.GetMethodInfo().Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync));

    private static VerbaraServer NewServer(IDisposable subscription, out IAmiConnection connection)
    {
        connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(subscription);
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable());
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}
