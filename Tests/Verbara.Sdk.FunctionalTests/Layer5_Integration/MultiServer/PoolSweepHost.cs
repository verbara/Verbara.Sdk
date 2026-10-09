using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.TestInfrastructure.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// A host of the consumers' multi-server shape — <c>AddVerbaraMultiServer</c> + <c>AddVerbaraSessionsMultiServer</c> —
/// over the two-server fixture, with short timeouts, whose servers join as a cluster joins them (connect, start, add to
/// the pool, attach) or through the pool's own add. Every session it opens is kept, with the endings published for it.
/// </summary>
internal sealed class PoolSweepHost : IAsyncDisposable
{
    /// <summary>
    /// The age after which the sweep verifies a held call: longer than a short call (2 s), so a call whose hangup the
    /// host receives ends by it before it can be a candidate.
    /// </summary>
    public static readonly TimeSpan DialingTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The sweep's interval, unless a host switches it off.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IHost _host;
    private readonly MultiServerFixture _lab;
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _endings = new(StringComparer.Ordinal);
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _changed = new(0, int.MaxValue);

    private PoolSweepHost(MultiServerFixture lab, bool sweepOn)
    {
        _lab = lab;
        _host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddVerbaraMultiServer();
                services.AddVerbaraSessionsMultiServer(o =>
                {
                    o.DialingTimeout = DialingTimeout;
                    o.ReconciliationInterval = sweepOn ? Interval : Timeout.InfiniteTimeSpan;
                });
            })
            .Build();
        Manager = _host.Services.GetRequiredService<ICallSessionManager>();
        Pool = _host.Services.GetRequiredService<VerbaraServerPool>();
        _subscription = Manager.Events.Subscribe(new Observer(this));
    }

    public ICallSessionManager Manager { get; }

    public VerbaraServerPool Pool { get; }

    /// <summary>Names of the hosted services the registrations added.</summary>
    public IReadOnlyList<string> HostedServices =>
        [.. _host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name)];

    /// <summary>Builds and starts the host; <paramref name="sweepOn"/> false switches the sweep off.</summary>
    public static async Task<PoolSweepHost> StartAsync(MultiServerFixture lab, bool sweepOn = true)
    {
        var host = new PoolSweepHost(lab, sweepOn);
        await host._host.StartAsync();
        return host;
    }

    /// <summary>
    /// Joins server <paramref name="server"/> ('A' or 'B') under <paramref name="id"/> as a cluster joins a node:
    /// connect as <paramref name="user"/>, start the server, add it to the pool, attach the session engine. With
    /// <paramref name="throughProxy"/>, A is reached through the fixture's AMI proxy.
    /// </summary>
    public async Task<VerbaraServer> JoinAsync(string id, char server, AmiUserClass user, bool throughProxy = false)
    {
        var factory = _host.Services.GetRequiredService<IAmiConnectionFactory>();
        var connection = await factory.CreateAndConnectAsync(Options(server, user, throughProxy));
        var verbaraServer = new VerbaraServer(connection, _host.Services.GetRequiredService<ILogger<VerbaraServer>>());
        await verbaraServer.StartAsync();
        Pool.AddExistingServer(id, verbaraServer);
        Manager.AttachToServer(verbaraServer, id);
        return verbaraServer;
    }

    /// <summary>Adds server <paramref name="server"/> to the running host with the pool's own add, then attaches it.</summary>
    public async Task<VerbaraServer> AddHotAsync(string id, char server, AmiUserClass user)
    {
        var verbaraServer = await Pool.AddServerAsync(id, Options(server, user, throughProxy: false));
        Manager.AttachToServer(verbaraServer, id);
        return verbaraServer;
    }

    /// <summary>Detaches the session engine from <paramref name="id"/> and removes it from the pool, as a cluster does.</summary>
    public async Task RemoveAsync(string id)
    {
        Manager.DetachFromServer(id);
        await Pool.RemoveServerAsync(id);
    }

    /// <summary>Originates <paramref name="count"/> calls to <paramref name="target"/> (<c>exten@context</c>) over <paramref name="server"/>'s connection.</summary>
    public static async Task OriginateAsync(VerbaraServer server, string target, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await server.Connection.SendActionAsync(new OriginateAction
            {
                Channel = $"Local/{target}/n", Application = "Wait", Data = "60", IsAsync = true,
            });
            if (!string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"originate to {target} was refused: {response.Response} {response.Message}");
        }
    }

    /// <summary>The sessions this host opened on <paramref name="serverId"/>.</summary>
    public IReadOnlyList<CallSession> SessionsOf(string serverId) =>
        [.. _sessions.Values.Where(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal))];

    /// <summary>The sessions this host opened on <paramref name="serverId"/> holding a channel that starts with <paramref name="channelPrefix"/>.</summary>
    public IReadOnlyList<CallSession> SessionsOf(string serverId, string channelPrefix) =>
        [.. SessionsOf(serverId).Where(s => HoldsChannel(s, channelPrefix))];

    /// <summary>
    /// Whether a participant's channel starts with <paramref name="channelPrefix"/>. The session engine adds participants
    /// on its own thread; a read that meets an addition is read again.
    /// </summary>
    private static bool HoldsChannel(CallSession session, string channelPrefix)
    {
        while (true)
        {
            try
            {
                return session.Participants.ToArray()
                    .Any(p => p?.Channel.StartsWith(channelPrefix, StringComparison.Ordinal) == true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The list changed under the copy: read it again.
            }
        }
    }

    /// <summary>How many <see cref="CallEndedEvent"/>s were published for <paramref name="session"/>.</summary>
    public int EndingsOf(CallSession session) => _endings.GetValueOrDefault(session.SessionId);

    /// <summary>Whether <paramref name="session"/> has ended.</summary>
    public static bool HasEnded(CallSession session) =>
        session.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut;

    /// <summary>Whether <paramref name="session"/> ended as a reload ends a call, with one ending published.</summary>
    public bool EndedAsAReload(CallSession session) =>
        HasEnded(session) && session.Metadata.GetValueOrDefault("cause") == "reload" && session.HangupCause is null
        && EndingsOf(session) == 1;

    /// <summary>Whether <paramref name="session"/> ended with its own hangup cause, with one ending published.</summary>
    public bool EndedByItsHangup(CallSession session) =>
        HasEnded(session) && session.Metadata.GetValueOrDefault("cause") != "reload" && session.HangupCause is not null
        && EndingsOf(session) == 1;

    /// <summary>Waits until <paramref name="condition"/> holds, re-read on every session event; false after <paramref name="bound"/>.</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan bound)
    {
        using var timeout = new CancellationTokenSource(bound);
        while (!condition())
        {
            try
            {
                // fence-allow: GUARD-TIMEOUT — re-reads the condition at least once a second while a sweep ends calls on its own clock; the bound only turns a hang into a red
                await _changed.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return condition();
            }
        }

        return true;
    }

    /// <summary>A short account of the sessions per server, for failure messages.</summary>
    public string Describe() =>
        string.Join("; ", _sessions.Values.GroupBy(s => s.ServerId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: " + string.Join(" ", g.Select(s =>
                $"{s.State}/{s.Metadata.GetValueOrDefault("cause") ?? "-"}/{s.HangupCause?.ToString() ?? "-"}/x{EndingsOf(s)}"))));

    private AmiConnectionOptions Options(char server, AmiUserClass user, bool throughProxy) => new()
    {
        Hostname = throughProxy ? (_lab.AmiProxy ?? throw new InvalidOperationException("No AMI proxy was started.")).Address
            : server == 'A' ? _lab.ServerAAddress : _lab.ServerBAddress,
        Port = throughProxy ? AmiPathProxy.Port : MultiServerAmiUsers.AmiPort,
        Username = user.Name,
        Password = server == 'A' ? _lab.SecretA(user) : _lab.SecretB(user),
        AutoReconnect = true,
        HeartbeatInterval = TimeSpan.FromSeconds(2),
        HeartbeatTimeout = TimeSpan.FromSeconds(2),
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(500),
        ReconnectMaxDelay = TimeSpan.FromSeconds(2),
    };

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await _host.StopAsync(stop.Token);
        _host.Dispose();
        _changed.Dispose();
    }

    private sealed class Observer(PoolSweepHost owner) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallStartedEvent && owner.Manager.GetById(value.SessionId) is { } session)
                owner._sessions.TryAdd(session.SessionId, session);
            else if (value is CallEndedEvent)
                owner._endings.AddOrUpdate(value.SessionId, 1, (_, n) => n + 1);

            owner._changed.Release();
        }

        public void OnError(Exception error)
        {
            // The manager's subject never faults.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed.
        }
    }
}
