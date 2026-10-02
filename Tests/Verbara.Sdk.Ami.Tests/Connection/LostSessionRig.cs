using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A real <see cref="AmiConnection"/> over <see cref="PipedSocketFactory"/>, on a <see cref="FakeTimeProvider"/>, whose
/// first session a test loses while it can hold the loss's release open: the peer writes one event, an
/// <see cref="AmiConnection.OnEvent"/> handler holds its delivery, and the peer then closes. The loss's release waits
/// for that delivery, so for as long as the handler is held the loss's ending is in progress.
/// </summary>
/// <remarks>
/// <para>
/// Every socket the connection creates is served: the peer completes the login and answers every Ping, unless the
/// socket refuses its connect. <see cref="AutoReconnectOff"/> is the default: the heartbeat is off, and
/// <see cref="AmiConnectionOptions.ConnectionTimeout"/> is <see cref="ConnectionTimeout"/>, which no other timer on the
/// fake clock shares (the stuck-notification watchdog's is 30 s), so a test finds the connect's wait for a lost release
/// by its due time in <see cref="FakeTimeProvider.TimersCreated"/> before it moves the clock.
/// </para>
/// <para>
/// Nothing here waits on the wall clock: every wait is bounded by <see cref="Bound"/>, a hang bound that a healthy run
/// never reaches, and ends on the signal it waits for.
/// </para>
/// </remarks>
internal sealed class LostSessionRig : IAsyncDisposable
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The connection's <see cref="AmiConnectionOptions.ConnectionTimeout"/>, on the fake clock.</summary>
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(7);

    /// <summary>The message of the exception a caller's connect gets when it meets an ending it cannot wait for.</summary>
    public const string EndedDuringTheConnect = "The connection was ended during the connect.";

    private readonly CancellationTokenSource _peerCts = new();
    private readonly ConcurrentQueue<Task> _serving = new();
    private readonly ConcurrentQueue<AmiConnectionStateChange> _changes = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmiConnectionStateChange> _lossDisconnecting =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmiConnectionStateChange> _lossDisconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private int _held;
    private volatile bool _holdArmed;
    private bool _disposed;

    /// <param name="configure">Changes the options before the connection is built; <see langword="null"/> keeps them.</param>
    public LostSessionRig(Action<AmiConnectionOptions>? configure = null)
    {
        var options = AutoReconnectOff();
        configure?.Invoke(options);
        Connection = new AmiConnection(Options.Create(options), Sockets, Log, Clock);
        // First in the invocation list, so a change is recorded before any handler a test adds sees it.
        Connection.StateChanged += Record;
        _accepting = Task.Run(() => AcceptAsync(_peerCts.Token), CancellationToken.None);
    }

    public FakeTimeProvider Clock { get; } = new();

    public PipedSocketFactory Sockets { get; } = new();

    public SignalingLogger<AmiConnection> Log { get; } = new();

    public AmiConnection Connection { get; }

    /// <summary>The first session's socket, once <see cref="ConnectFirstAsync"/> has returned.</summary>
    public PipedSocket First => Sockets.Created[0];

    /// <summary>Every change delivered to <see cref="AmiConnection.StateChanged"/> so far, in order.</summary>
    public IReadOnlyList<AmiConnectionStateChange> Changes => [.. _changes];

    /// <summary>Delivered when a change to <see cref="AmiConnectionState.Disconnecting"/> not made by the caller is.</summary>
    public Task<AmiConnectionStateChange> LossDisconnecting => _lossDisconnecting.Task;

    /// <summary>Delivered when a change to <see cref="AmiConnectionState.Disconnected"/> not made by the caller is.</summary>
    public Task<AmiConnectionStateChange> LossDisconnected => _lossDisconnected.Task;

    /// <summary>Completes once the held delivery has begun.</summary>
    public Task Holding => _holding.Task;

    /// <summary>Valid options with <c>AutoReconnect</c> and the heartbeat off and <see cref="ConnectionTimeout"/> set.</summary>
    public static AmiConnectionOptions AutoReconnectOff() => new()
    {
        Hostname = "localhost",
        Username = "admin",
        Password = "secret",
        EnableHeartbeat = false,
        AutoReconnect = false,
        ConnectionTimeout = ConnectionTimeout,
        DefaultResponseTimeout = TimeSpan.FromMinutes(1),
        DefaultEventTimeout = TimeSpan.Zero,
    };

    /// <summary>
    /// Holds the delivery of the first event the connection dispatches until <see cref="OpenTheGate"/>; every later event
    /// is delivered at once.
    /// </summary>
    public void HoldTheFirstEvent() => HoldTheFirstEvent(() => _gate.Task);

    /// <summary>
    /// Holds the delivery of the first event the connection dispatches until <paramref name="whileHeld"/> completes,
    /// running inside that dispatch; every later event is delivered at once. Bounded: a hold that outlives three times
    /// <see cref="Bound"/> ends with a fault the connection logs, so a defect fails the test instead of hanging it.
    /// </summary>
    public void HoldTheFirstEvent(Func<Task> whileHeld)
    {
        _holdArmed = true;
        Connection.OnEvent += async _ =>
        {
            if (Interlocked.Exchange(ref _held, 1) != 0)
                return;

            _holding.TrySetResult();
            await whileHeld().WaitAsync(Bound * 3);
        };
    }

    /// <summary>Releases the delivery <see cref="HoldTheFirstEvent()"/> holds.</summary>
    public void OpenTheGate() => _gate.TrySetResult();

    /// <summary>Connects the first session; its peer logs it in.</summary>
    public Task ConnectFirstAsync() => Connection.ConnectAsync().AsTask().WaitAsync(Bound);

    /// <summary>
    /// The peer writes one event and, once its delivery is held, closes the first session: a loss nobody asked for.
    /// Without a hold the peer closes at once.
    /// </summary>
    public async Task LoseTheFirstSessionAsync()
    {
        if (!_holdArmed)
        {
            First.CloseFromPeer();
            return;
        }

        (await First.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer writes the event whose delivery is held");
        await Holding.WaitAsync(Bound);
        First.CloseFromPeer();
    }

    /// <summary>
    /// Whether <paramref name="connect"/> is parked on its wait for the lost release: <see langword="true"/> once a timer
    /// due after <see cref="ConnectionTimeout"/> exists on the clock while the call is still running,
    /// <see langword="false"/> when the call completes first. Throws <see cref="TimeoutException"/> when neither happens
    /// within <see cref="Bound"/>.
    /// </summary>
    public async Task<bool> ParkedAsync(Task connect)
    {
        var timer = WaitTimerAsync();
        var first = await Task.WhenAny(timer, connect).WaitAsync(Bound);
        return first == timer && !connect.IsCompleted;
    }

    /// <summary>
    /// Counts, without waiting, the timers due after <see cref="ConnectionTimeout"/> created since the last read of
    /// <see cref="FakeTimeProvider.TimersCreated"/>. Reads them off the clock, so a later <see cref="ParkedAsync"/> sees
    /// only newer ones.
    /// </summary>
    public int WaitTimersCreatedSoFar()
    {
        var count = 0;
        while (Clock.TimersCreated.TryRead(out var timer))
        {
            if (timer.DueTime == ConnectionTimeout)
                count++;
        }

        return count;
    }

    /// <summary>Moves the clock to one tick before <see cref="ConnectionTimeout"/> has passed since the start.</summary>
    public void AdvanceToJustBeforeTheBound() => Clock.Advance(ConnectionTimeout - TimeSpan.FromTicks(1));

    /// <summary>Sockets created and sockets the connection disposed, to compare after its disposal.</summary>
    public (int Created, int Released) SocketCounts()
    {
        var created = Sockets.Created;
        return (created.Count, created.Count(socket => socket.DisposeCount > 0));
    }

    /// <summary>
    /// Disposes the connection within <see cref="Bound"/> and waits until every notification queued by then has been
    /// delivered, so <see cref="Changes"/> is complete.
    /// </summary>
    public async Task EndAndDrainAsync()
    {
        await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await Connection.PendingNotifications.WaitAsync(Bound);
    }

    /// <summary>What a call ended with, as a line a failure message can carry.</summary>
    public static string Describe(Exception? outcome) =>
        outcome is null ? "returned without an exception" : $"{outcome.GetType().Name}: \"{outcome.Message}\"";

    /// <summary>The exception <paramref name="call"/> ended with, or <see langword="null"/>; bounded by <see cref="Bound"/>.</summary>
    public static async Task<Exception?> OutcomeAsync(Task call)
    {
        try
        {
            await call.WaitAsync(Bound);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // What the call ended with is what the tests assert: recorded, not rethrown.
            return ex;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        OpenTheGate();
        try
        {
            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        }
        finally
        {
            await _peerCts.CancelAsync();
            await _accepting.WaitAsync(Bound);
            await Task.WhenAll(_serving).WaitAsync(Bound);
            _peerCts.Dispose();
        }
    }

    private void Record(AmiConnectionStateChange change)
    {
        _changes.Enqueue(change);
        if (change.ByCaller)
            return;

        if (change.Current == AmiConnectionState.Disconnecting)
            _lossDisconnecting.TrySetResult(change);
        else if (change.Current == AmiConnectionState.Disconnected)
            _lossDisconnected.TrySetResult(change);
    }

    private async Task WaitTimerAsync()
    {
        while (true)
        {
            var timer = await Clock.TimersCreated.ReadAsync(_peerCts.Token);
            if (timer.DueTime == ConnectionTimeout)
                return;
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var socket = await Sockets.NextAsync(cancellationToken);
                if (!socket.RefusesConnect)
                    _serving.Enqueue(ServeAsync(socket, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The rig is being disposed; every socket created by then is being served or was refused.
        }
    }

    private static async Task ServeAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            await socket.CompleteLoginAsync(cancellationToken);
            await socket.AnswerPingsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The session ended under the peer (the connection released the socket, or the rig was disposed): a peer
            // has nothing more to do, and how the connection ended is what the tests assert.
        }
    }
}
