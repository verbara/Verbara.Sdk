using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness, a
/// <see cref="VerbaraServer"/> on it, and the <see cref="BootingAsterisk"/> peers that play Asterisk for it.
/// </summary>
/// <remarks>
/// <para>
/// The first peer serves the connect. When the session ends and <c>AutoReconnect</c> is on, the reconnect loop
/// creates a second socket; the second peer serves it only once the test calls <see cref="ReleaseSecondPeer"/>.
/// Until then the connect attempt waits for its banner, so the connection stays <c>Reconnecting</c> or
/// <c>Connecting</c> for as long as the test holds it. A socket beyond the second, or the second one when there is
/// no second peer, is never served.
/// </para>
/// <para>
/// Nothing here waits on the wall clock. Every wait is bounded by <see cref="Bound"/>, a hang bound that a healthy
/// run never reaches, and ends on the signal it waits for: the connect, the start, <see cref="Reconnected"/>, a log
/// line on <see cref="ConnectionLog"/> or <see cref="ServerLog"/>, or a timer on <see cref="Clock"/>. The connection's
/// heartbeat is off and its event-action timeout is disabled, so no timer inside the connection ends a load either.
/// </para>
/// </remarks>
internal sealed class Run : IAsyncDisposable
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly PipedSocketFactory _sockets = new();
    private readonly CancellationTokenSource _peerCts = new();
    private readonly TaskCompletionSource _secondPeerReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _peers = Task.CompletedTask;
    private Task _firstServed = Task.CompletedTask;

    private Run(BootingAsterisk firstPeer, BootingAsterisk? secondPeer, bool autoReconnect, TimeSpan reconnectInitialDelay)
    {
        FirstPeer = firstPeer;
        SecondPeer = secondPeer;
        Connection = new AmiConnection(
            Microsoft.Extensions.Options.Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = autoReconnect,
                ReconnectInitialDelay = reconnectInitialDelay,
                // A limit, never a wait: a connect attempt the test holds must not give up while the test holds it.
                ConnectionTimeout = TimeSpan.FromMinutes(1),
                DefaultResponseTimeout = TimeSpan.FromMinutes(1),
                // No wall-clock bound inside a load: the test's own bound reports a load that hangs.
                DefaultEventTimeout = TimeSpan.Zero,
            }),
            _sockets,
            ConnectionLog);
        Server = new VerbaraServer(Connection, ServerLog);
        // VerbaraServer has no clock seam yet, so nothing on it reads Clock. Once it has one, the rig assigns Clock
        // to it here, and the load's interval and budget run on the manual clock.
        Connection.Reconnected += () => _reconnected.TrySetResult();
    }

    public AmiConnection Connection { get; }

    public VerbaraServer Server { get; }

    /// <summary>The manual clock the server's load waits on.</summary>
    public FakeTimeProvider Clock { get; } = new();

    /// <summary>The connection's log: <c>[AMI] Reconnecting</c> is written once the state is <c>Reconnecting</c>.</summary>
    public SignalingLogger<AmiConnection> ConnectionLog { get; } = new();

    /// <summary>The server's log: <c>[LIVE] State loaded</c> is written when a load completes.</summary>
    public SignalingLogger<VerbaraServer> ServerLog { get; } = new();

    public BootingAsterisk FirstPeer { get; }

    public BootingAsterisk? SecondPeer { get; }

    /// <summary>Completes when the connection raises <see cref="AmiConnection.Reconnected"/>.</summary>
    public Task Reconnected => _reconnected.Task;

    /// <summary>
    /// Connects over <paramref name="firstPeer"/>, without starting the server. The reconnect's socket, when there is
    /// one, is served by <paramref name="secondPeer"/> once the test calls <see cref="ReleaseSecondPeer"/>.
    /// </summary>
    public static async Task<Run> ConnectAsync(BootingAsterisk firstPeer, BootingAsterisk? secondPeer = null,
        bool autoReconnect = false, TimeSpan? reconnectInitialDelay = null)
    {
        var run = new Run(firstPeer, secondPeer, autoReconnect, reconnectInitialDelay ?? TimeSpan.FromMilliseconds(1));
        run._peers = run.ServePeersAsync(run._peerCts.Token);
        try
        {
            await run.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            return run;
        }
        catch
        {
            // The caller never receives the run, so it is released here before the failure is reported.
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// <see cref="ConnectAsync"/>, then <see cref="StartServerAsync"/>. A start that throws releases the run before
    /// the exception reaches the caller, so a test that expects the start to throw connects with
    /// <see cref="ConnectAsync"/> and calls <see cref="StartServerAsync"/> itself.
    /// </summary>
    public static async Task<Run> StartAsync(BootingAsterisk firstPeer, BootingAsterisk? secondPeer = null,
        bool autoReconnect = false, TimeSpan? reconnectInitialDelay = null)
    {
        var run = await ConnectAsync(firstPeer, secondPeer, autoReconnect, reconnectInitialDelay);
        try
        {
            await run.StartServerAsync();
            return run;
        }
        catch
        {
            // The caller never receives the run, so it is released here before the failure is reported.
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts the server: its first load, bounded by <see cref="Bound"/>.</summary>
    public Task StartServerAsync() => Server.StartAsync().WaitAsync(Bound);

    /// <summary>Lets the second peer serve the reconnect's socket.</summary>
    public void ReleaseSecondPeer() => _secondPeerReleased.TrySetResult();

    /// <summary>
    /// The next timer created on <see cref="Clock"/>: once it is read, the code that created it is parked on it and
    /// the test may advance the clock. Throws <see cref="TimeoutException"/> when none is created within
    /// <see cref="Bound"/>.
    /// </summary>
    public Task<FakeTimeProvider.FakeTimer> NextTimerAsync() =>
        Clock.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Connection.DisposeAsync();
        await _peerCts.CancelAsync();
        await _peers.WaitAsync(Bound);
        await _firstServed.WaitAsync(Bound);
        _peerCts.Dispose();
    }

    private async Task ServePeersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var first = await _sockets.NextAsync(cancellationToken);
            _firstServed = FirstPeer.ServeAsync(first, cancellationToken);

            // The reconnect loop's socket. Until it is served the attempt waits for its banner.
            var second = await _sockets.NextAsync(cancellationToken);
            if (SecondPeer is not null)
            {
                await _secondPeerReleased.Task.WaitAsync(cancellationToken);
                await SecondPeer.ServeAsync(second, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended while a socket was awaited or held; the connection's own ending has released it.
        }
    }
}
