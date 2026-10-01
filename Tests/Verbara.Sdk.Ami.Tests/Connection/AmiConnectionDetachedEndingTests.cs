using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// An ending started from a task that an event handler spawned and did not await. While the dispatch that spawned it
/// is still running, the ending cannot be told from one that dispatch awaits, and it does not wait for the dispatch.
/// Once that dispatch has returned, nothing waits on the ending from inside the connection, so it waits for the
/// dispatch in progress like any caller's ending does.
/// </summary>
/// <remarks>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>. Handlers are held on gates the test opens. Every wait is bounded
/// by <see cref="Bound"/> and ends on its signal, except <see cref="SettleWindow"/>, the one observation window: it gives
/// an ending that does not wait for the held dispatch the time to complete before the test opens that dispatch's gate.
/// The assertion is positive either way: what the dispose's own completion saw.
/// </para>
/// </remarks>
public sealed class AmiConnectionDetachedEndingTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the test leaves the second dispatch held after the detached dispose was called. It is the observation,
    /// not a hang bound: today that dispose completes within milliseconds, without waiting for the held dispatch.
    /// </summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Event 1's handler starts, without awaiting it, a task that disposes the connection once event 2's dispatch has
    /// begun; event 2's handler is held on a gate. A continuation on the dispose's completion records whether event 2's
    /// handler had returned by then: the dispatch in progress was waited for.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldWaitForTheDispatchInProgress_WhenStartedFromATaskAFinishedDispatchSpawned()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReturned = 0;
        var disposeCalled = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        connection.OnEvent += async evt =>
        {
            if (Interlocked.Increment(ref dispatches) == 1)
            {
                // Event 1: a detached task, which this dispatch does not await, disposes once event 2's dispatch runs.
                _ = Task.Run(async () =>
                {
                    await secondEntered.Task;
                    var dispose = connection.DisposeAsync().AsTask();
                    disposeCalled.TrySetResult(dispose.ContinueWith(
                        _ => Volatile.Read(ref secondReturned) == 1,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                });
                return;
            }

            secondEntered.TrySetResult();
            await secondGate.Task;
            Volatile.Write(ref secondReturned, 1);
        };
        var socket = await ConnectAsync(connection, factory, peerCts);

        (await socket.WriteEventAsync("UserEvent", [new("UserEvent", "one")])).Should().BeTrue("the peer sends event 1");
        (await socket.WriteEventAsync("UserEvent", [new("UserEvent", "two")])).Should().BeTrue("the peer sends event 2");
        var secondReturnedWhenDisposeCompleted = await disposeCalled.Task.WaitAsync(Bound);
        await Task.WhenAny(secondReturnedWhenDisposeCompleted, Task.Delay(SettleWindow)); // fence-allow: SETTLE — the window in which today's dispose completes without waiting for the held dispatch
        secondGate.TrySetResult();
        var waited = await secondReturnedWhenDisposeCompleted.WaitAsync(Bound);

        using (new AssertionScope())
        {
            waited.Should().BeTrue(
                "the dispatch that spawned the dispose had returned, so the dispose waits for the dispatch in progress like any caller's");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once");
        }
    }

    /// <summary>
    /// An <c>OnEvent</c> handler that awaits a task which disposes the connection: the dispose cannot wait for that
    /// dispatch, which waits for it, and it completes, today and after.
    /// </summary>
    [Fact]
    public async Task OnEventHandler_ShouldCompleteTheEnding_WhenItAwaitsATaskThatDisposes()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory);
        var ended = new TaskCompletionSource<AmiConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += async evt =>
        {
            await Task.Run(() => connection.DisposeAsync().AsTask());
            ended.TrySetResult(connection.State);
        };
        var socket = await ConnectAsync(connection, factory, peerCts);

        (await socket.WriteEventAsync("UserEvent", [new("UserEvent", "one")])).Should().BeTrue("the peer sends one event");
        var state = await ended.Task.WaitAsync(Bound);

        using (new AssertionScope())
        {
            state.Should().Be(AmiConnectionState.Disconnected, "the awaited dispose completed without waiting for the dispatch that awaits it");
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once");
        }
    }

    /// <summary>
    /// Event 1's handler starts a detached task that disposes the connection while event 2's dispatch runs; event 2's
    /// handler then calls <c>DisposeAsync</c> itself, which joins that ending from inside its dispatch. Both complete,
    /// today and after: a handler that calls the ending, rather than awaiting a stored task, never waits on itself.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldComplete_WhenALaterHandlerCallsTheEndingADetachedTaskStarted()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detachedCalled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondJoined = new TaskCompletionSource<AmiConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        connection.OnEvent += async evt =>
        {
            if (Interlocked.Increment(ref dispatches) == 1)
            {
                _ = Task.Run(async () =>
                {
                    await secondEntered.Task;
                    detachedCalled.TrySetResult(connection.DisposeAsync().AsTask());
                });
                return;
            }

            secondEntered.TrySetResult();
            await detachedCalled.Task;
            await connection.DisposeAsync();
            secondJoined.TrySetResult(connection.State);
        };
        var socket = await ConnectAsync(connection, factory, peerCts);

        (await socket.WriteEventAsync("UserEvent", [new("UserEvent", "one")])).Should().BeTrue("the peer sends event 1");
        (await socket.WriteEventAsync("UserEvent", [new("UserEvent", "two")])).Should().BeTrue("the peer sends event 2");
        var detached = await detachedCalled.Task.WaitAsync(Bound);
        var joinedState = await secondJoined.Task.WaitAsync(Bound);
        var detachedCompleted = await Completes(detached);

        using (new AssertionScope())
        {
            joinedState.Should().Be(AmiConnectionState.Disconnected, "the later handler's DisposeAsync joined the ending and returned");
            detachedCompleted.Should().BeTrue("the detached task's dispose completes once a handler has called the ending");
            socket.DisposeCount.Should().Be(1, "the socket is released exactly once");
        }
    }

    private static async Task<bool> Completes(Task task)
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

    private static AmiConnection Create(PipedSocketFactory factory) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
        }), factory, NullLogger<AmiConnection>.Instance);

    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }
}
