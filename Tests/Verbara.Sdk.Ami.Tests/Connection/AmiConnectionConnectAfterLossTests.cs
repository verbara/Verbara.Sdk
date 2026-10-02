using System.Net.Sockets;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A caller who reconnects an AMI connection by hand after a loss nobody asked for — <c>AutoReconnect</c> off, or a
/// reconnect loop that gave up — connects, however it learns of the loss: a <c>StateChanged</c> handler on
/// <see cref="AmiConnectionState.Disconnecting"/> or on <see cref="AmiConnectionState.Disconnected"/>, a
/// <c>Lost</c> handler, or a thread polling <see cref="AmiConnection.State"/>. A connect made while the loss's release
/// is still in progress waits for that release and then connects; it never takes over a session the release then
/// tears down, and every socket the connection created is released once it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Each test runs a <see cref="LostSessionRig"/>: a real <see cref="AmiConnection"/> over <see cref="PipedSocket"/>s, on
/// a fake clock. The loss's release is held open by an <c>OnEvent</c> handler that holds the delivery of the event the
/// peer wrote before it closed, until the test opens its gate; the connect is seen to wait when its bound appears on the
/// fake clock as a timer due after <c>ConnectionTimeout</c>, which no other timer shares. The give-up cannot be held
/// that way (the loop's cleanup before its first attempt waits for that delivery), so its release is held inside the
/// disposal of the last refused attempt's socket instead.
/// </para>
/// <para>
/// Every test counts the sockets the connection created against the sockets it released after its disposal. Time
/// enters only as the rig's hang bound.
/// </para>
/// </remarks>
public sealed class AmiConnectionConnectAfterLossTests
{
    private static readonly TimeSpan Bound = LostSessionRig.Bound;

    /// <summary>How many in-process runs the probe of the connect made on the loss's <c>Disconnected</c> makes.</summary>
    private const int ProbeRuns = 300;

    [Fact]
    public async Task ConnectAsync_ShouldWaitForTheReleaseAndConnect_WhenAStateChangedHandlerConnectsOnTheLossDisconnecting()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        var started = NewSignal<Task>();
        rig.Connection.StateChanged += change =>
        {
            if (change is { Current: AmiConnectionState.Disconnecting, ByCaller: false })
                started.TrySetResult(rig.Connection.ConnectAsync().AsTask());
        };

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var connect = await started.Task.WaitAsync(Bound);
        rig.Connection.State.Should().Be(AmiConnectionState.Disconnecting,
            "the loss's release is still held when the handler's connect has started");

        await AssertWaitsThenConnectsAsync(rig, connect, "a StateChanged handler on the loss's Disconnecting");
    }

    [Fact]
    public async Task ConnectAsync_ShouldWaitForTheReleaseAndConnect_WhenALostHandlerConnects()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        var started = NewSignal<Task>();
        rig.Connection.Lost += _ => started.TrySetResult(rig.Connection.ConnectAsync().AsTask());

        await rig.ConnectFirstAsync();
        await rig.LoseTheFirstSessionAsync();
        var connect = await started.Task.WaitAsync(Bound);
        rig.Connection.State.Should().Be(AmiConnectionState.Disconnecting,
            "the loss's release is still held when the handler's connect has started");

        await AssertWaitsThenConnectsAsync(rig, connect, "a Lost handler");
    }

    [Fact]
    public async Task ConnectAsync_ShouldWaitForTheReleaseAndConnect_WhenAThreadPollingStateConnectsAsSoonAsItLeavesConnected()
    {
        await using var rig = new LostSessionRig();
        rig.HoldTheFirstEvent();
        await rig.ConnectFirstAsync();

        using var pollCts = new CancellationTokenSource(Bound);
        var started = NewSignal<Task>();
        var poller = new Thread(() =>
        {
            // A busy poll, as a caller that watches State would write it; bounded by pollCts so a defect fails the test.
            while (rig.Connection.State == AmiConnectionState.Connected && !pollCts.IsCancellationRequested)
                Thread.Yield();

            started.TrySetResult(rig.Connection.ConnectAsync().AsTask());
        })
        { IsBackground = true, Name = "State poller" };
        poller.Start();

        await rig.LoseTheFirstSessionAsync();
        var connect = await started.Task.WaitAsync(Bound);
        poller.Join(Bound).Should().BeTrue("the poller returns once it has started its connect");

        await AssertWaitsThenConnectsAsync(rig, connect, "a thread polling State");
    }

    /// <summary>
    /// The reconnect loop's give-up ends the connection as a loss without <c>AutoReconnect</c> does. A caller who reconnects
    /// from its <c>Disconnecting</c> waits for its release, which here is held inside the disposal of the last refused
    /// attempt's socket, then connects once the peer accepts connections again.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldWaitForTheGiveUpsReleaseAndConnect_WhenAStateChangedHandlerConnectsOnTheGiveUpsDisconnecting()
    {
        using var release = new ManualResetEventSlim(false);
        await using var rig = new LostSessionRig(options =>
        {
            options.AutoReconnect = true;
            options.MaxReconnectAttempts = 1;
            options.ReconnectInitialDelay = TimeSpan.FromMilliseconds(1);
            options.ReconnectMultiplier = 1;
            options.ReconnectMaxDelay = TimeSpan.FromMilliseconds(1);
        });
        try
        {
            // The give-up's release disposes the last refused attempt's socket: its disposal waits for the test.
            rig.Sockets.OnCreated = socket =>
            {
                if (socket.RefusesConnect)
                    socket.DuringFirstDispose = () => release.Wait(Bound);
            };
            var giveUp = NewSignal<AmiConnectionStateChange>();
            var started = NewSignal<Task>();
            rig.Connection.StateChanged += change =>
            {
                if (change is { Previous: AmiConnectionState.Reconnecting, Current: AmiConnectionState.Disconnecting, ByCaller: false })
                {
                    giveUp.TrySetResult(change);
                    started.TrySetResult(rig.Connection.ConnectAsync().AsTask());
                }
            };

            await rig.ConnectFirstAsync();
            rig.Sockets.RefuseConnects = true;
            rig.First.CloseFromPeer();
            var connect = await started.Task.WaitAsync(Bound);
            var change = await giveUp.Task.WaitAsync(Bound);

            var parked = await rig.ParkedAsync(connect);
            var completedBeforeTheRelease = connect.IsCompleted;
            rig.Sockets.RefuseConnects = false;
            release.Set();
            var outcome = await LostSessionRig.OutcomeAsync(connect);
            var stateOnReturn = rig.Connection.State;
            await rig.EndAndDrainAsync();
            var (created, released) = rig.SocketCounts();

            using (new AssertionScope())
            {
                parked.Should().BeTrue(
                    $"a connect made from the give-up's Disconnecting waits for the give-up's release; it {LostSessionRig.Describe(outcome)} before the release");
                completedBeforeTheRelease.Should().BeFalse("the connect does not complete before the give-up's release");
                outcome.Should().BeNull("once the release has finished the connect connects");
                stateOnReturn.Should().Be(AmiConnectionState.Connected);
                change.Cause.Should().BeOfType<SocketException>("the give-up carries the last refused attempt's exception");
                released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
            }
        }
        finally
        {
            release.Set();
            await rig.DisposeAsync();
        }
    }

    /// <summary>
    /// The connect made on the loss's <c>Disconnected</c> meets the ending between its two last statements: it has written
    /// <c>Disconnected</c> but not yet completed. That window cannot be held open, so this is a probe: a fixed number of
    /// in-process runs, each bounded, every one of which must connect and release every socket.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldConnectInEveryRun_WhenAStateChangedHandlerConnectsOnTheLossDisconnected()
    {
        var failures = new List<string>();
        var leaks = 0;
        for (var run = 0; run < ProbeRuns; run++)
        {
            await using var rig = new LostSessionRig();
            var started = NewSignal<Task>();
            rig.Connection.StateChanged += change =>
            {
                if (change is { Current: AmiConnectionState.Disconnected, ByCaller: false })
                    started.TrySetResult(rig.Connection.ConnectAsync().AsTask());
            };

            await rig.ConnectFirstAsync();
            await rig.LoseTheFirstSessionAsync();
            var connect = await started.Task.WaitAsync(Bound);
            var outcome = await LostSessionRig.OutcomeAsync(connect);
            var state = rig.Connection.State;
            await rig.EndAndDrainAsync();
            var (created, released) = rig.SocketCounts();

            if (outcome is not null || state != AmiConnectionState.Connected)
                failures.Add($"run {run}: {LostSessionRig.Describe(outcome)}, State {state}");
            if (released != created)
                leaks++;
        }

        using (new AssertionScope())
        {
            failures.Count.Should().Be(0,
                $"a connect made on the loss's Disconnected connects in every one of {ProbeRuns} runs; the failed runs: "
                + string.Join("; ", failures));
            leaks.Should().Be(0, $"every socket is released after DisposeAsync in every one of {ProbeRuns} runs");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="connect"/> was made while the loss's release is held: it waits for that release, then connects
    /// into a session no ending tears down, and every socket is released after <c>DisposeAsync</c>.
    /// </summary>
    private static async Task AssertWaitsThenConnectsAsync(LostSessionRig rig, Task connect, string who)
    {
        var parked = await rig.ParkedAsync(connect);
        var completedBeforeTheRelease = connect.IsCompleted;
        rig.OpenTheGate();
        var outcome = await LostSessionRig.OutcomeAsync(connect);
        var stateOnReturn = rig.Connection.State;
        Exception? ping = null;
        if (outcome is null)
            ping = await LostSessionRig.OutcomeAsync(rig.Connection.SendActionAsync(new PingAction()).AsTask());
        var stateAfterPing = rig.Connection.State;
        await rig.EndAndDrainAsync();
        var changes = rig.Changes;
        var (created, released) = rig.SocketCounts();

        var lossDisconnecting = IndexOf(changes, c => c is { Current: AmiConnectionState.Disconnecting, ByCaller: false });
        var lossDisconnected = IndexOf(changes, c => c is { Current: AmiConnectionState.Disconnected, ByCaller: false });
        // The caller's first Connecting since the loss began: the first connect's own is before it.
        var callerConnecting = IndexOf(changes, c => c is { Current: AmiConnectionState.Connecting, ByCaller: true },
            after: lossDisconnecting);
        var callerConnected = IndexOf(changes, c => c is { Current: AmiConnectionState.Connected, ByCaller: true }, after: lossDisconnected);
        var afterConnected = callerConnected >= 0 && callerConnected + 1 < changes.Count ? changes[callerConnected + 1] : null;

        using (new AssertionScope())
        {
            parked.Should().BeTrue(
                $"a connect made from {who} while the loss's release is held waits for that release; it {LostSessionRig.Describe(outcome)} before the release");
            completedBeforeTheRelease.Should().BeFalse("the connect does not complete before the release");
            outcome.Should().BeNull("once the release has finished the connect connects");
            stateOnReturn.Should().Be(AmiConnectionState.Connected, "a connect that returns leaves a session");
            ping.Should().BeNull("a Ping is answered on the new session");
            stateAfterPing.Should().Be(AmiConnectionState.Connected);
            lossDisconnecting.Should().BeGreaterThanOrEqualTo(0, "the loss is announced");
            callerConnecting.Should().BeGreaterThan(lossDisconnected,
                "the caller's Connecting is announced after the loss's Disconnected, never in its window; changes: "
                + string.Join(", ", changes.Select(c => $"{c.Previous}->{c.Current}{(c.ByCaller ? " (caller)" : "")}")));
            afterConnected.Should().NotBeNull("the caller's session is announced Connected");
            if (afterConnected is not null)
            {
                afterConnected.ByCaller.Should().BeTrue(
                    "no change leaves the caller's Connected until the caller's own DisposeAsync: no old ending tears the new session down");
            }

            released.Should().Be(created, "every socket the connection created is released after DisposeAsync");
        }
    }

    private static int IndexOf(IReadOnlyList<AmiConnectionStateChange> changes, Func<AmiConnectionStateChange, bool> match,
        int after = -1)
    {
        for (var i = after + 1; i < changes.Count; i++)
        {
            if (match(changes[i]))
                return i;
        }

        return -1;
    }

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
