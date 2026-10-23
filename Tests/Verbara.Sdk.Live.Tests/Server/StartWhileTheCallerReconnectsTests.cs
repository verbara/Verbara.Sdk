using FluentAssertions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// With automatic reconnection off, a start whose load is cut short by the end of its AMI session throws the SDK's
/// not-connected exception even when the caller is already connecting the connection again: the state the load stops in,
/// <c>Connecting</c> or <c>Connected</c>, was written by the caller's own <c>ConnectAsync</c>, which raises no
/// <c>Reconnected</c>, so no reload will follow and a start that returned would leave the table stale with no signal.
/// With automatic reconnection on, the same <c>Connecting</c> or <c>Connected</c> written by the reconnect loop is not
/// the caller's: the loop raises <c>Reconnected</c> and its reload loads the state, so the start returns and logs that
/// it was interrupted.
/// </summary>
/// <remarks>
/// <para>
/// Measured live on 2026-10-18 (Asterisk 22.12, 23.6 and 24.0 rc2, 210 starts): with the caller reconnecting on the
/// loss, as the connection-state migration guide shows, 3 of them returned, each with the caller's
/// <c>Connecting</c> written 0.1 ms after the lost session's release; Live then held 42 dead channels against 0, or 0
/// queues against 6. A caller's <c>Connected</c> passes the same check today, read, not measured.
/// </para>
/// <para>
/// One clock, no timer: a real <see cref="AmiConnection"/> over <see cref="Run"/>. The first peer leaves its
/// <c>QueueStatus</c> list open after the queue's member, and the test holds the load inside the null-default
/// <c>QueueRowBuffered</c> observation, before the next read. No public snapshot event is raised while collecting. With the load held there, the test ends the
/// session, waits for <c>Disconnected</c>, and calls <c>ConnectAsync</c> as the caller; the second peer answers that
/// connect only when the test releases it. Then the load goes on, finds its session ended, and reads the state.
/// </para>
/// </remarks>
public sealed class StartWhileTheCallerReconnectsTests
{
    private const string Interrupted = "[LIVE] Initial state load interrupted";

    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheLoadStopsWhileTheCallersConnectIsConnecting()
    {
        var measured = await StartCutShortWhileTheCallerReconnectsAsync(callerConnected: false);

        measured.Should().BeEquivalentTo(
            new Measured(typeof(AmiNotConnectedException), Interrupted: false, StateAtThrow: AmiConnectionState.Connecting,
                PeerFault: null),
            "the load stopped in the Connecting the caller's own ConnectAsync wrote, which raises no Reconnected: no reload "
            + "will follow, so the start throws, as it does when it stops in Disconnecting or Disconnected, and does not "
            + $"log that the reconnect will load the state. Measured: {measured}");
    }

    [Fact]
    public async Task StartAsync_ShouldThrowNotConnected_WhenTheLoadStopsAfterTheCallersConnectHasConnected()
    {
        var measured = await StartCutShortWhileTheCallerReconnectsAsync(callerConnected: true);

        measured.Should().BeEquivalentTo(
            new Measured(typeof(AmiNotConnectedException), Interrupted: false, StateAtThrow: AmiConnectionState.Connected,
                PeerFault: null),
            "the load stopped in the Connected the caller's own ConnectAsync reached, which raises no Reconnected either: "
            + "no reload will follow, so the start throws, and does not log that the reconnect will load the state. "
            + $"Measured: {measured}");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnAndLogTheInterruption_WhenTheLoadStopsWhileTheReconnectLoopIsConnecting()
    {
        var measured = await StartCutShortWhileTheLoopReconnectsAsync(loopConnected: false);

        measured.Should().BeEquivalentTo(
            new Measured(Thrown: null, Interrupted: true, StateAtThrow: AmiConnectionState.Connecting, PeerFault: null),
            "the load stopped in the Connecting the reconnect loop wrote, not the caller: the loop's connect raises "
            + "Reconnected and its reload loads the state, so the start returns and logs that it was interrupted. "
            + $"Measured: {measured}");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnAndLogTheInterruption_WhenTheLoadStopsAfterTheReconnectLoopHasConnected()
    {
        var measured = await StartCutShortWhileTheLoopReconnectsAsync(loopConnected: true);

        measured.Should().BeEquivalentTo(
            new Measured(Thrown: null, Interrupted: true, StateAtThrow: AmiConnectionState.Connected, PeerFault: null),
            "the load stopped in the Connected the reconnect loop reached, not the caller: the loop raises Reconnected and "
            + "its reload loads the state, so the start returns and logs that it was interrupted. "
            + $"Measured: {measured}");
    }

    /// <summary>What a start measured: the exception it threw (null when it returned), and the log and peers around it.</summary>
    private sealed record Measured(Type? Thrown, bool Interrupted, AmiConnectionState StateAtThrow, string? PeerFault);

    private static async Task<Measured> StartCutShortWhileTheCallerReconnectsAsync(bool callerConnected)
    {
        var first = new BootingAsterisk { BootedAtLogin = true, QueueStatusStopsAfterMember = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        var run = await Run.ConnectAsync(first, second, autoReconnect: false);
        using var hold = new LoadHold();
        Task? connect = null;
        try
        {
            var disconnected = StateSignal(run.Connection, c => c.Current == AmiConnectionState.Disconnected);
            var callerConnecting = StateSignal(run.Connection,
                c => c.Current == AmiConnectionState.Connecting && c.ByCaller);
            run.Server.QueueRowBuffered = hold.Block;

            var start = run.Server.StartAsync();
            await hold.Held.WaitAsync(Run.Bound);

            // The load is inside its QueueStatus read. The session ends, and the caller connects again on the loss.
            first.CloseSession();
            await disconnected.WaitAsync(Run.Bound);
            connect = run.Connection.ConnectAsync().AsTask();
            await callerConnecting.WaitAsync(Run.Bound);
            if (callerConnected)
            {
                run.ReleaseSecondPeer();
                await connect.WaitAsync(Run.Bound);
            }

            hold.Release();
            var thrown = await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));
            return new Measured(
                thrown?.GetType(),
                run.ServerLog.Entries.Any(e => e.Line.Contains(Interrupted, StringComparison.Ordinal)),
                run.Connection.State,
                first.Fault?.Message ?? second.Fault?.Message);
        }
        finally
        {
            hold.Release();
            await run.DisposeAsync();

            // The caller's connect that the test never let finish ends with the run; its outcome is not under test.
            if (connect is not null)
                _ = await Record.ExceptionAsync(() => connect);
        }
    }

    /// <summary>
    /// With automatic reconnection on, the load is held inside its <c>QueueStatus</c> read while the session ends and the
    /// reconnect loop writes <c>Connecting</c> (its attempt waits for the second peer's banner) and, when
    /// <paramref name="loopConnected"/>, <c>Connected</c>; then the load goes on, finds its session ended, and reads the
    /// state the loop wrote.
    /// </summary>
    private static async Task<Measured> StartCutShortWhileTheLoopReconnectsAsync(bool loopConnected)
    {
        var first = new BootingAsterisk { BootedAtLogin = true, QueueStatusStopsAfterMember = true };
        var second = new BootingAsterisk { BootedAtLogin = true };
        var run = await Run.ConnectAsync(first, second, autoReconnect: true);
        using var hold = new LoadHold();
        try
        {
            var loopConnecting = StateSignal(run.Connection,
                c => c.Current == AmiConnectionState.Connecting && !c.ByCaller);
            var loopConnectedSignal = StateSignal(run.Connection,
                c => c.Current == AmiConnectionState.Connected && !c.ByCaller);
            run.Server.QueueRowBuffered = hold.Block;

            var start = run.Server.StartAsync();
            await hold.Held.WaitAsync(Run.Bound);

            // The load is inside its QueueStatus read. The session ends, and the reconnect loop connects again on its own.
            first.CloseSession();
            await run.SecondSocketCreated.WaitAsync(Run.Bound);
            await loopConnecting.WaitAsync(Run.Bound);
            if (loopConnected)
            {
                run.ReleaseSecondPeer();
                await loopConnectedSignal.WaitAsync(Run.Bound);
            }

            hold.Release();
            var thrown = await Record.ExceptionAsync(() => start.WaitAsync(Run.Bound));
            return new Measured(
                thrown?.GetType(),
                run.ServerLog.Entries.Any(e => e.Line.Contains(Interrupted, StringComparison.Ordinal)),
                run.Connection.State,
                first.Fault?.Message ?? second.Fault?.Message);
        }
        finally
        {
            hold.Release();
            await run.DisposeAsync();
        }
    }

    private static Task StateSignal(AmiConnection connection, Func<Verbara.Sdk.AmiConnectionStateChange, bool> match)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += change =>
        {
            if (match(change))
                signal.TrySetResult();
        };
        return signal.Task;
    }

    /// <summary>
    /// Holds the load inside a handler it raises, until the test releases it. The block is bounded by
    /// <see cref="DispatchGate.HoldBound"/>, a hang bound a healthy run never reaches.
    /// </summary>
    private sealed class LoadHold : IDisposable
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new(initialState: false);
        private int _blocked;

        public Task Held => _held.Task;

        public void Block()
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 1)
                return;

            _held.TrySetResult();
            _released.Wait(DispatchGate.HoldBound);
        }

        public void Release() => _released.Set();

        public void Dispose()
        {
            _released.Set();
            _released.Dispose();
        }
    }
}
