using System.Collections.Concurrent;
using System.Threading.Channels;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What bounds a caller's connect that waits for the release of a loss nobody asked for: the connection's
/// <c>ConnectionTimeout</c>, after which the call throws the <see cref="OperationCanceledException"/> "The connection was
/// ended during the connect." it threw before it waited; the caller's token, which ends it with the caller's own
/// <see cref="OperationCanceledException"/>; and the caller's own ending, which ends it at once with
/// <see cref="ObjectDisposedException"/>. A call made from inside the connection's own event dispatch does not wait at
/// all, and a call blocked on from a single-threaded <see cref="SynchronizationContext"/> does not hang on the wait.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs a <see cref="LostSessionRig"/> on a fake clock. The wait is seen when its bound appears on that clock
/// as a timer due after <c>ConnectionTimeout</c>; the bound is crossed by advancing the clock, never by waiting it out.
/// The "cycle" is the pattern the bound exists for: an <c>OnEvent</c> handler hands the reconnect to work that runs
/// outside the dispatch and awaits it, while the loss's release waits for that very handler.
/// </para>
/// <para>
/// Every test counts the sockets the connection created against the sockets it released after its disposal. Time
/// enters only as the rig's hang bound.
/// </para>
/// </remarks>
public sealed class AmiConnectionConnectAfterLossBoundTests
{
    private static readonly TimeSpan Bound = LostSessionRig.Bound;

    private const string EndedDuringTheConnect = LostSessionRig.EndedDuringTheConnect;

    [Fact]
    public async Task ConnectAsync_ShouldThrowAtTheBoundAndLetTheHandlerReturn_WhenAnOnEventHandlerAwaitsAWorkerThatReconnects()
    {
        await using var rig = new LostSessionRig();
        await using var cycle = new ReconnectCycle(rig);

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var job = await cycle.Posted.WaitAsync(Bound);

        var parked = await rig.ParkedAsync(job);
        var doneJustBeforeTheBound = true;
        if (parked)
        {
            rig.AdvanceToJustBeforeTheBound();
            doneJustBeforeTheBound = job.IsCompleted;
            rig.Clock.Advance(TimeSpan.FromTicks(1));
        }

        var atTheBound = await job.WaitAsync(Bound);
        var handlerReturned = await CompletesWithinBoundAsync(cycle.HandlerReturned);
        var retry = await LostSessionRig.OutcomeAsync(rig.Connection.ConnectAsync().AsTask());
        var stateAfterRetry = rig.Connection.State;
        await rig.EndAndDrainAsync();
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the worker's connect waits for the lost release, bounded; it {LostSessionRig.Describe(atTheBound)} at clock 0");
            doneJustBeforeTheBound.Should().BeFalse("the connect has not completed one tick before ConnectionTimeout");
            atTheBound.Should().BeOfType<OperationCanceledException>("past the bound the connect throws today's exception")
                .Which.Message.Should().Be(EndedDuringTheConnect);
            handlerReturned.Should().BeTrue("the handler that awaited the worker returns once the connect has thrown");
            retry.Should().BeNull("a connect made after the handler returned connects");
            stateAfterRetry.Should().Be(AmiConnectionState.Connected);
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowTheCallersCancellation_WhenTheCallersTokenIsCancelledDuringTheWait()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        // The lost socket is disposed by the loss's release before it waits for the held delivery: from here the loss's
        // ending is recorded and in progress, on today's code and on the fixed code alike.
        await rig.First.Disposed.WaitAsync(Bound);

        using var callerCts = new CancellationTokenSource();
        var connect = rig.Connection.ConnectAsync(callerCts.Token).AsTask();
        var parked = await rig.ParkedAsync(connect);
        await callerCts.CancelAsync();
        var outcome = await LostSessionRig.OutcomeAsync(connect);
        var stateAfterTheCall = rig.Connection.State;
        rig.OpenTheGate();
        var lossEnded = await CompletesWithinBoundAsync(rig.LossDisconnected);
        await rig.EndAndDrainAsync();
        var callerConnects = rig.Changes.Count(c => c is { Current: AmiConnectionState.Connecting, ByCaller: true });
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the connect waits for the lost release until its token is cancelled; it {LostSessionRig.Describe(outcome)} at once");
            outcome.Should().BeAssignableTo<OperationCanceledException>("the caller's token ends the wait");
            ((outcome as OperationCanceledException)?.CancellationToken == callerCts.Token).Should().BeTrue(
                "the exception carries the caller's own token, not the connection's");
            stateAfterTheCall.Should().Be(AmiConnectionState.Disconnecting, "the cancelled call wrote no state");
            lossEnded.Should().BeTrue("the loss's ending completes on its own once the delivery returns");
            callerConnects.Should().Be(1, "only the first connect announced a caller's Connecting");
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowObjectDisposedAtOnce_WhenTheCallerDisposesDuringTheWait()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await rig.First.Disposed.WaitAsync(Bound);

        var connect = rig.Connection.ConnectAsync().AsTask();
        var parked = await rig.ParkedAsync(connect);
        var dispose = rig.Connection.DisposeAsync().AsTask();
        var outcome = await LostSessionRig.OutcomeAsync(connect);
        var disposedBeforeTheDeliveryReturned = dispose.IsCompleted;
        rig.OpenTheGate();
        var disposed = await CompletesWithinBoundAsync(dispose);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the connect waits for the lost release until the caller's ending; it {LostSessionRig.Describe(outcome)} at once");
            outcome.Should().BeOfType<ObjectDisposedException>(
                "the caller's dispose ends the wait at once, with the clock not moved");
            disposedBeforeTheDeliveryReturned.Should().BeFalse("DisposeAsync joins the lost ending, held by the delivery");
            disposed.Should().BeTrue("DisposeAsync completes once the held delivery returns");
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    /// <summary>
    /// The handler's own connect, inside the dispatch the loss's release waits for, does not wait: it throws at once and
    /// writes no state. The connect made the moment the handler returns, from outside, waits for the release that this
    /// return lets finish, and connects.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldThrowAtOnceInsideTheDispatch_AndAConnectMadeAsTheHandlerReturnsShouldConnect()
    {
        await using var rig = new LostSessionRig();
        Exception? inDispatch = null;
        var stateAfterTheCall = AmiConnectionState.Initial;
        var waitTimersInDispatch = -1;
        // Completed by the handler as its last act. The continuation runs synchronously on the handler's thread, under
        // the test's execution context, so the retry is made outside the dispatch and before the handler has returned.
        var returning = new TaskCompletionSource();
        var retryStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrying = returning.Task.ContinueWith(
            _ => retryStarted.TrySetResult(rig.Connection.ConnectAsync().AsTask()),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        rig.HoldTheFirstEvent(async () =>
        {
            await rig.LossDisconnecting.WaitAsync(Bound);
            rig.WaitTimersCreatedSoFar();
            try
            {
                await rig.Connection.ConnectAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // What the call ended with is what the test asserts.
                inDispatch = ex;
            }

            stateAfterTheCall = rig.Connection.State;
            waitTimersInDispatch = rig.WaitTimersCreatedSoFar();
            returning.TrySetResult();
        });

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var retry = await retryStarted.Task.WaitAsync(Bound);
        await retrying.WaitAsync(Bound);
        var retryOutcome = await LostSessionRig.OutcomeAsync(retry);
        var stateAfterRetry = rig.Connection.State;
        var disposed = await CompletesWithinBoundAsync(rig.EndAndDrainAsync());
        var changes = rig.Changes;
        var lossDisconnected = changes.ToList().FindIndex(c => c is { Current: AmiConnectionState.Disconnected, ByCaller: false });
        var connectingBeforeTheLossEnded = changes.Take(lossDisconnected < 0 ? changes.Count : lossDisconnected)
            .Count(c => c is { Current: AmiConnectionState.Connecting, ByCaller: true });
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            inDispatch.Should().BeOfType<OperationCanceledException>("a connect inside the dispatch throws at once")
                .Which.Message.Should().Be(EndedDuringTheConnect);
            waitTimersInDispatch.Should().Be(0, "the call inside the dispatch never waited");
            stateAfterTheCall.Should().Be(AmiConnectionState.Disconnecting, "the call inside the dispatch wrote no state");
            connectingBeforeTheLossEnded.Should().Be(1,
                "only the first connect announced a caller's Connecting before the loss's Disconnected");
            retryOutcome.Should().BeNull(
                "a connect made from outside as the handler returns waits for the release that return lets finish, then connects");
            stateAfterRetry.Should().Be(AmiConnectionState.Connected);
            disposed.Should().BeTrue("DisposeAsync completes");
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    /// <summary>
    /// A thread that runs a single-threaded <see cref="SynchronizationContext"/> blocks on <c>ConnectAsync</c> while a
    /// loss's release is held. The wait must not need that thread to finish: past the bound the call ends with today's
    /// exception within a bounded time.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldNotHang_WhenAThreadWithASingleThreadedContextBlocksOnItDuringTheWait()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        await rig.First.Disposed.WaitAsync(Bound);

        using var context = new SingleThreadContext();
        var blocked = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                rig.Connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
                blocked.TrySetResult(null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                blocked.TrySetResult(ex);
            }
        }, null);

        var parked = await rig.ParkedAsync(blocked.Task);
        if (parked)
            rig.Clock.Advance(LostSessionRig.ConnectionTimeout);
        var ended = await CompletesWithinBoundAsync(blocked.Task);
        var outcome = ended ? await blocked.Task : null;
        rig.OpenTheGate();
        await rig.EndAndDrainAsync();
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the blocked connect waits for the lost release, bounded; it {LostSessionRig.Describe(outcome)} at clock 0");
            ended.Should().BeTrue("the blocked call ends once the clock has passed the bound: the wait does not need the blocked thread");
            LostSessionRig.Describe(outcome).Should().Be($"OperationCanceledException: \"{EndedDuringTheConnect}\"",
                "past the bound the blocked call gets the exception of a connect that meets an ending in progress");
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowObjectDisposedAndLetTheHandlerReturn_WhenTheCallerDisposesDuringTheCycle()
    {
        await using var rig = new LostSessionRig();
        await using var cycle = new ReconnectCycle(rig);

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var job = await cycle.Posted.WaitAsync(Bound);

        var parked = await rig.ParkedAsync(job);
        var clockBefore = rig.Clock.GetUtcNow();
        var dispose = rig.Connection.DisposeAsync().AsTask();
        var outcome = await job.WaitAsync(Bound);
        var handlerReturned = await CompletesWithinBoundAsync(cycle.HandlerReturned);
        var disposed = await CompletesWithinBoundAsync(dispose);
        var clockMoved = rig.Clock.GetUtcNow() != clockBefore;
        await rig.Connection.PendingNotifications.WaitAsync(Bound);
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the worker's connect waits for the lost release; it {LostSessionRig.Describe(outcome)} at clock 0");
            outcome.Should().BeOfType<ObjectDisposedException>("the caller's dispose ends the worker's wait at once");
            handlerReturned.Should().BeTrue("the handler that awaited the worker returns");
            disposed.Should().BeTrue("DisposeAsync completes without the clock moving");
            clockMoved.Should().BeFalse("nothing here advanced the clock");
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    /// <summary>
    /// The cycle closed through the notification queue: an <c>OnEvent</c> handler waits for a signal that a
    /// <c>StateChanged</c> handler sets only once its blocking connect has returned. While that connect waits, the queue
    /// it runs on delivers nothing else; past the bound the connect throws, both handlers return, and the loss's
    /// <c>Disconnected</c> is announced after them.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldThrowAtTheBound_WhenAStateChangedHandlerBlocksOnItWhileAnOnEventHandlerWaitsForThatHandler()
    {
        await using var rig = new LostSessionRig();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        rig.HoldTheFirstEvent(async () =>
        {
            await signal.Task;
            order.Enqueue("OnEvent returned");
        });
        Exception? blockedOutcome = null;
        var blockedStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connection.StateChanged += change =>
        {
            if (change is { Current: AmiConnectionState.Disconnecting, ByCaller: false })
            {
                var connect = rig.Connection.ConnectAsync().AsTask();
                blockedStarted.TrySetResult(connect);
                try
                {
                    // Blocks this notification, and the queue behind it, as a synchronous handler does. Bounded: a defect
                    // fails the test instead of hanging it.
                    connect.WaitAsync(Bound * 3).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // What the blocked call ended with is what the test asserts.
                    blockedOutcome = ex;
                }

                order.Enqueue("StateChanged handler returned");
                signal.TrySetResult();
            }
            else if (change is { Current: AmiConnectionState.Disconnected, ByCaller: false })
            {
                order.Enqueue("Disconnected announced");
            }
        };
        var lostDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connection.Lost += _ =>
        {
            order.Enqueue("Lost delivered");
            lostDelivered.TrySetResult();
        };

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var blocked = await blockedStarted.Task.WaitAsync(Bound);
        var parked = await rig.ParkedAsync(blocked);
        var lostDeliveredDuringTheWait = lostDelivered.Task.IsCompleted;
        if (parked)
            rig.Clock.Advance(LostSessionRig.ConnectionTimeout);
        var lossEnded = await CompletesWithinBoundAsync(rig.LossDisconnected);
        await rig.Connection.PendingNotifications.WaitAsync(Bound);
        var retry = await LostSessionRig.OutcomeAsync(rig.Connection.ConnectAsync().AsTask());
        var stateAfterRetry = rig.Connection.State;
        await rig.EndAndDrainAsync();
        var sequence = order.ToList();
        var (created, released) = rig.SocketCounts();

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"the blocked connect waits for the lost release, bounded; it {LostSessionRig.Describe(blockedOutcome)} at clock 0");
            lostDeliveredDuringTheWait.Should().BeFalse("no later notification is delivered while the blocked connect waits");
            blockedOutcome.Should().BeOfType<OperationCanceledException>("past the bound the blocked connect throws today's exception")
                .Which.Message.Should().Be(EndedDuringTheConnect);
            lossEnded.Should().BeTrue("the loss's ending completes once both handlers have returned");
            sequence.Should().ContainInOrder(
                ["StateChanged handler returned", "OnEvent returned", "Disconnected announced"],
                "both handlers return, and the loss's Disconnected is announced after them");
            retry.Should().BeNull("a connect made after that connects");
            stateAfterRetry.Should().Be(AmiConnectionState.Connected);
            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The reconnect-through-a-worker cycle: a worker started before the loss with the execution context's flow
    /// suppressed, so nothing of a dispatch flows into it, connects on request; an <c>OnEvent</c> handler holds the first
    /// event, and once the loss is announced posts the reconnect to the worker and awaits it without a token.
    /// </summary>
    private sealed class ReconnectCycle : IAsyncDisposable
    {
        private readonly Channel<TaskCompletionSource<Exception?>> _jobs = Channel.CreateUnbounded<TaskCompletionSource<Exception?>>();
        private readonly CancellationTokenSource _workerCts = new();
        private readonly TaskCompletionSource<Task<Exception?>> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _handlerReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _worker;

        public ReconnectCycle(LostSessionRig rig)
        {
            using (ExecutionContext.SuppressFlow())
                _worker = Task.Run(() => WorkAsync(rig.Connection, _jobs.Reader, _workerCts.Token), CancellationToken.None);

            rig.HoldTheFirstEvent(async () =>
            {
                try
                {
                    await rig.LossDisconnecting.WaitAsync(Bound);
                    var job = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _jobs.Writer.TryWrite(job);
                    _posted.TrySetResult(job.Task);
                    // The pattern under test awaits the worker without a token; the bound here only fails a defect.
                    await job.Task.WaitAsync(Bound * 3);
                }
                finally
                {
                    _handlerReturned.TrySetResult();
                }
            });
        }

        /// <summary>The reconnect the handler posted: what the worker's connect ended with.</summary>
        public Task<Task<Exception?>> Posted => _posted.Task;

        public Task HandlerReturned => _handlerReturned.Task;

        public async ValueTask DisposeAsync()
        {
            _jobs.Writer.TryComplete();
            await _workerCts.CancelAsync();
            await _worker.WaitAsync(Bound);
            _workerCts.Dispose();
        }

        private static async Task WorkAsync(AmiConnection connection, ChannelReader<TaskCompletionSource<Exception?>> jobs,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var job in jobs.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        await connection.ConnectAsync(CancellationToken.None);
                        job.TrySetResult(null);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        job.TrySetResult(ex);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The cycle is being disposed.
            }
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> with one thread of its own, as a UI thread has: everything posted to it runs
    /// on that thread, in order, and a callback that blocks blocks everything posted after it.
    /// </summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Single-threaded context" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
                _queue.TryAdd((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Nothing under test sends synchronously to this context.");

        /// <summary>
        /// Stops the thread once what is queued has run, waiting at most <see cref="Bound"/>. The queue is disposed only
        /// once the thread has left it; a thread still blocked is a background thread and is left to the process.
        /// </summary>
        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(Bound))
                _queue.Dispose();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A callback's failure is its own; an unhandled exception here would end the test process.
                }
            }
        }
    }
}
