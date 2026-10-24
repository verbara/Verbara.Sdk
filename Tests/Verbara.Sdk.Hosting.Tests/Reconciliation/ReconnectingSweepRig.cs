using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The sweep over a real <see cref="AmiConnection"/>, so a test can put the server in the state between a reconnect's
/// login and the moment its <c>Reconnected</c> has been handled: a <see cref="VerbaraServer"/> on the connection, a
/// <see cref="CallSessionManager"/> attached to it, one <see cref="SweepPeer"/> per AMI session, and the sweep.
/// </summary>
/// <remarks>
/// <para>
/// Every peer lists one call on <c>Status</c>, <see cref="CallUid"/>, so the first load opens its session and no snapshot
/// ends it: the sweep is observed through the verification its span reports and the <c>Status</c> each peer was sent.
/// </para>
/// <para>
/// The notification queue that delivers <c>Reconnected</c> is held through the server's public
/// <see cref="VerbaraServer.ConnectionLost"/>, which is raised from that queue: <see cref="HoldFirstLoss"/> makes the
/// first loss's handler wait on a gate, so every later <c>Reconnected</c> waits behind it while its session is logged in.
/// </para>
/// </remarks>
internal sealed class ReconnectingSweepRig : IAsyncDisposable
{
    public const string CallUid = "1790950000.1";
    public const string CallChannel = "PJSIP/trunk-00000001";

    private readonly PipedSocketFactory _sockets = new();
    private readonly CancellationTokenSource _peerCts = new();
    private readonly ManualResetEventSlim _lossGate = new(false);
    private readonly List<IAsyncDisposable> _owned = [];
    private Task _serving = Task.CompletedTask;
    private int _losses;

    private ReconnectingSweepRig(int sessions, bool autoReconnect)
    {
        Peers = [.. Enumerable.Range(0, sessions).Select(_ => new SweepPeer())];
        Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), _sockets, ConnectionLog);
        Server = new VerbaraServer(Connection, ServerLog);
        Settings = new SessionOptions();
        Clock = new ManualClock(DateTimeOffset.UtcNow);
        Manager = new CallSessionManager(Options.Create(Settings), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore(), Clock);
        Manager.AttachToServer(Server, SweepRig.ServerId);
    }

    public IReadOnlyList<SweepPeer> Peers { get; }

    public AmiConnection Connection { get; }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public ManualClock Clock { get; }

    public SessionOptions Settings { get; }

    public SignalingLogger<AmiConnection> ConnectionLog { get; } = new();

    public SignalingLogger<VerbaraServer> ServerLog { get; } = new();

    /// <summary>
    /// Connects over the first peer, starts the server (its first load opens the call's session), and ages the session
    /// past every timeout, so it is the sweep's one candidate.
    /// </summary>
    public static async Task<ReconnectingSweepRig> StartAsync(int sessions, bool autoReconnect = true)
    {
        var rig = new ReconnectingSweepRig(sessions, autoReconnect);
        rig._serving = rig.ServeAsync(rig._peerCts.Token);
        try
        {
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(SweepRig.Bound);
            await rig.Server.StartAsync().WaitAsync(SweepRig.Bound);
            var session = rig.Manager.GetByLinkedId(CallUid)
                ?? throw new InvalidOperationException("Premise: the first load opened no session for the listed call.");
            SweepRig.CreatedAtField.SetValue(session, DateTimeOffset.UtcNow - rig.Settings.DialingTimeout - TimeSpan.FromHours(1));
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    /// <summary>Holds the first loss's <see cref="VerbaraServer.ConnectionLost"/> handler, and so the notification queue.</summary>
    public void HoldFirstLoss() => Server.ConnectionLost += _ =>
    {
        if (Interlocked.Increment(ref _losses) == 1)
            _lossGate.Wait(SweepRig.Bound * 3);
    };

    /// <summary>Releases the held loss handler; every queued <c>Reconnected</c> is then delivered.</summary>
    public void ReleaseLoss() => _lossGate.Set();

    /// <summary>Completes once the connection has logged in <paramref name="sessions"/> sessions.</summary>
    public Task LoggedIn(int sessions) => ConnectionLog.Logged("[AMI] Connected", sessions);

    /// <summary>Builds the sweep over this rig's manager and server. Disposed with the rig.</summary>
    public SessionReconciliationService BuildSweep()
    {
        var sweep = new SessionReconciliationService(Manager, Server, Options.Create(Settings),
            NullLogger<SessionReconciliationService>.Instance, Clock);
        _owned.Add(new DisposeOnly(sweep));
        return sweep;
    }

    public async ValueTask DisposeAsync()
    {
        _lossGate.Set();
        foreach (var owned in _owned)
            await owned.DisposeAsync();
        await Server.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);
        await Connection.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);
        await _peerCts.CancelAsync();
        await _serving.WaitAsync(SweepRig.Bound);
        await Manager.DisposeAsync();
        _peerCts.Dispose();
        _lossGate.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var served = new List<Task>();
        try
        {
            foreach (var peer in Peers)
            {
                var socket = await _sockets.NextAsync(cancellationToken);
                served.Add(Task.Run(() => peer.ServeAsync(socket, cancellationToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended while a socket was awaited.
        }

        await Task.WhenAll(served);
    }

    private sealed class DisposeOnly(IDisposable owned) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owned.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Plays Asterisk on one AMI session: the login, then <c>Status</c> listing <see cref="ReconnectingSweepRig.CallUid"/>, an
/// empty <c>QueueStatus</c>, <c>Agents</c> and <c>BridgeList</c>, <c>No active conferences.</c> for the conference
/// listings, and <c>Success</c> for anything else. It counts every <c>Status</c> it is sent.
/// </summary>
internal sealed class SweepPeer
{
    private readonly ConcurrentDictionary<string, int> _asks = new(StringComparer.OrdinalIgnoreCase);
    private PipedSocket? _socket;

    public int Asked(string action) => _asks.TryGetValue(action, out var count) ? count : 0;

    /// <summary>Ends the session from the peer's side.</summary>
    public void CloseSession() =>
        (Volatile.Read(ref _socket) ?? throw new InvalidOperationException("The peer serves no session yet.")).CloseFromPeer();

    public async Task ServeAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _socket, socket);
        try
        {
            await socket.CompleteLoginAsync(cancellationToken);
            while (await socket.ReadActionAsync(cancellationToken) is { } action)
                await AnswerAsync(socket, action);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed the socket before the login finished.
        }
    }

    private async Task AnswerAsync(PipedSocket socket, string action)
    {
        var end = action.IndexOf("\r\n", StringComparison.Ordinal);
        var name = (end >= 0 ? action[..end] : action).Replace("Action:", "", StringComparison.OrdinalIgnoreCase).Trim();
        _asks.AddOrUpdate(name, 1, static (_, count) => count + 1);
        var id = PipedSocket.ActionIdOf(action);
        switch (name.ToUpperInvariant())
        {
            case "STATUS":
                await socket.RespondAsync("Success", id, [new("EventList", "start")]);
                await socket.WriteEventAsync("Status",
                [
                    new("ActionID", id), new("Channel", ReconnectingSweepRig.CallChannel), new("ChannelState", "4"),
                    new("ChannelStateDesc", "Ring"), new("CallerIDNum", "5551234"), new("Context", "from-trunk"),
                    new("Exten", "100"), new("Uniqueid", ReconnectingSweepRig.CallUid),
                    new("Linkedid", ReconnectingSweepRig.CallUid),
                ]);
                await socket.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "1")]);
                break;
            case "QUEUESTATUS":
            case "AGENTS":
            case "BRIDGELIST":
                await socket.RespondAsync("Success", id, [new("EventList", "start")]);
                await socket.WriteEventAsync(name + "Complete",
                    [new("ActionID", id), new("EventList", "Complete"), new("ListItems", 0.ToString(CultureInfo.InvariantCulture))]);
                break;
            case "CONFBRIDGELISTROOMS":
            case "CONFBRIDGELIST":
                await socket.RespondAsync("Error", id, [new("Message", "No active conferences.")]);
                break;
            default:
                await socket.RespondAsync("Success", id);
                break;
        }
    }
}
