using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The token overload as a caller that holds only <see cref="IAmiConnection"/> sees it. A wrapper that forwards only the
/// members the interface had before the overload gets the default: the handler is subscribed through the wrapper's own
/// <c>OnEvent</c>, called with a token that never cancels, and disposing the subscription removes exactly that handler. A
/// wrapper that forwards the overload passes the connection's real token.
/// </summary>
public sealed partial class AmiConnectionTokenHandlerTests
{
    [Fact]
    public async Task Subscribe_ShouldDeliverWithATokenThatNeverCancels_WhenTheWrapperDoesNotForwardTheOverload()
    {
        const int events = 10;
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var inner = Create(factory, new DispatchLogger());
        IAmiConnection through = new PlainWrapper(inner);
        var calls = 0;
        var lastEntered = NewSignal();
        var release = NewSignal();
        var returned = 0;
        CancellationToken seen = default;
        using var subscription = through.Subscribe(async (_, ct) =>
        {
            seen = ct;
            if (Interlocked.Increment(ref calls) < events)
                return;

            lastEntered.TrySetResult();
            // Waits on the token, which never cancels, or on the test's release.
            await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, ct), release.Task); // fence-allow: SIMULATED-WORK — the handler's work, ended by its token or the test's release
            Volatile.Write(ref returned, 1);
        });
        // A probe on the inner connection learns, through the real token, when the caller's ending has begun.
        var ending = NewSignal();
        using var probe = inner.Subscribe((_, ct) =>
        {
            ct.Register(() => ending.TrySetResult());
            return ValueTask.CompletedTask;
        });
        var socket = await ConnectAsync(inner, factory, peer);

        await WritePeersAsync(socket, 0, events);
        var all = await CompletesWithinBoundAsync(lastEntered.Task);
        var dispose = inner.DisposeAsync().AsTask();
        (await CompletesWithinBoundAsync(ending.Task)).Should().BeTrue("the caller's ending has begun");
        var cancelledDuringEnding = seen.IsCancellationRequested;
        var completedWhileHeld = dispose.IsCompleted;
        release.TrySetResult();
        var ended = await CompletesWithinBoundAsync(dispose);

        using (new AssertionScope())
        {
            all.Should().BeTrue("the handler receives all ten events through the wrapper");
            Volatile.Read(ref calls).Should().Be(events);
            seen.CanBeCanceled.Should().BeFalse("the default calls the handler with a token that never cancels");
            cancelledDuringEnding.Should().BeFalse("the token reads not cancelled even while the inner connection ends");
            completedWhileHeld.Should().BeFalse("the ending waits for the handler, as before");
            ended.Should().BeTrue();
            Volatile.Read(ref returned).Should().Be(1);
        }
    }

    [Fact]
    public async Task Subscribe_ShouldRemoveOnlyItsHandler_WhenADefaultSubscriptionIsDisposed()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var inner = Create(factory, new DispatchLogger());
        IAmiConnection through = new PlainWrapper(inner);
        var xCalls = 0;
        using var x = through.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref xCalls);
            return ValueTask.CompletedTask;
        });
        var y = new Fence();
        through.OnEvent += y.HandleAsync;
        var socket = await ConnectAsync(inner, factory, peer);

        x.Dispose();
        x.Dispose();
        await WritePeersAsync(socket, 0, 3);
        await WriteSentinelAsync(socket);
        var reached = await CompletesWithinBoundAsync(y.SentinelReached);

        using (new AssertionScope())
        {
            reached.Should().BeTrue();
            Volatile.Read(ref xCalls).Should().Be(0, "X's subscription is disposed");
            y.Count.Should().Be(4, "Y, subscribed through OnEvent, receives every event");
        }
    }

    [Fact]
    public async Task Subscribe_ShouldPassTheRealToken_WhenTheWrapperForwardsTheOverload()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var inner = Create(factory, new DispatchLogger());
        IAmiConnection through = new ForwardingWrapper(inner);
        var entered = NewSignal();
        CancellationToken seen = default;
        using var subscription = through.Subscribe(async (_, ct) =>
        {
            seen = ct;
            entered.TrySetResult();
            await Task.Delay(HandlerWait, ct); // fence-allow: SIMULATED-WORK — the handler's long work, which its token ends
        });
        var socket = await ConnectAsync(inner, factory, peer);

        await WritePeersAsync(socket, 0, 1);
        (await CompletesWithinBoundAsync(entered.Task)).Should().BeTrue();
        var started = TimeProvider.System.GetTimestamp();
        var ended = await CompletesWithinBoundAsync(inner.DisposeAsync().AsTask());
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        using (new AssertionScope())
        {
            ended.Should().BeTrue();
            seen.IsCancellationRequested.Should().BeTrue("the forwarded overload hands the handler the connection's token");
            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Subscribe_ShouldThrowArgumentNullAndSubscribeNothing_WhenTheDefaultIsGivenANullHandler()
    {
        var wrapper = new CountingWrapper();
        IAmiConnection through = wrapper;

        var act = () => through.Subscribe((Func<ManagerEvent, CancellationToken, ValueTask>)null!);

        act.Should().Throw<ArgumentNullException>();
        wrapper.Adds.Should().Be(0, "the default subscribes nothing for a null handler");
    }

    /// <summary>Forwards every member, the token overload included.</summary>
    private sealed class ForwardingWrapper(AmiConnection inner) : PlainWrapper(inner), IAmiConnection
    {
        public IDisposable Subscribe(Func<ManagerEvent, CancellationToken, ValueTask> handler) => Inner.Subscribe(handler);
    }

    /// <summary>Forwards every member the interface had in 2.7.0, and nothing else.</summary>
    private class PlainWrapper(AmiConnection inner) : IAmiConnection
    {
        protected AmiConnection Inner { get; } = inner;

        public AmiConnectionState State => Inner.State;
        public string? AsteriskVersion => Inner.AsteriskVersion;
        public bool ReportsEventActionOutcome => Inner.ReportsEventActionOutcome;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => Inner.ConnectAsync(cancellationToken);
        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
            Inner.SendActionAsync(action, cancellationToken);
        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
            where TResponse : ManagerResponse => Inner.SendActionAsync<TResponse>(action, cancellationToken);
        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => Inner.SendEventGeneratingActionAsync(action, cancellationToken);
        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action, Ami.Connection.EventActionOutcome? outcome,
            CancellationToken cancellationToken = default) => Inner.SendEventGeneratingActionAsync(action, outcome, cancellationToken);
        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => Inner.Subscribe(observer);
        public event Func<ManagerEvent, ValueTask>? OnEvent { add => Inner.OnEvent += value; remove => Inner.OnEvent -= value; }
        public event Action? Reconnected { add => Inner.Reconnected += value; remove => Inner.Reconnected -= value; }
        public event Action<AmiConnectionStateChange>? StateChanged { add => Inner.StateChanged += value; remove => Inner.StateChanged -= value; }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => Inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    /// <summary>An implementation with no connection behind it that counts the handlers added to its <c>OnEvent</c>.</summary>
    private sealed class CountingWrapper : IAmiConnection
    {
        private int _adds;

        public int Adds => Volatile.Read(ref _adds);

        public AmiConnectionState State => AmiConnectionState.Disconnected;
        public string? AsteriskVersion => null;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
            where TResponse : ManagerResponse => throw new NotSupportedException();
        public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IDisposable Subscribe(IObserver<ManagerEvent> observer) => throw new NotSupportedException();
        public event Func<ManagerEvent, ValueTask>? OnEvent { add => Interlocked.Increment(ref _adds); remove { } }
        public event Action? Reconnected { add { } remove { } }
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
