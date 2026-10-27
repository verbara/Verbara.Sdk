using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Connection;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Server;

internal static partial class VerbaraServerPoolLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "[POOL] Server dispose failed: server={ServerId}")]
    public static partial void ServerDisposeFailed(ILogger logger, string serverId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[POOL] AMI connection dispose failed: server={ServerId}")]
    public static partial void ConnectionDisposeFailed(ILogger logger, string serverId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[POOL] Membership subscriber failed: event={Notification}, server={ServerId}")]
    public static partial void MembershipSubscriberFailed(ILogger logger, string notification, string serverId, Exception exception);
}

/// <summary>
/// Manages multiple VerbaraServer instances connected to different Asterisk PBX servers.
/// Provides federated agent routing so callers can locate which server owns a given agent.
/// Designed for 100K+ agents distributed across 20-50 Asterisk instances.
/// </summary>
public sealed class VerbaraServerPool : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _membershipLock = new();
    private readonly ConcurrentDictionary<string, VerbaraServer> _servers = new();
    private readonly ConcurrentDictionary<string, string> _agentRouting = new();

    public VerbaraServerPool(
        IAmiConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory)
    {
        _connectionFactory = connectionFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VerbaraServerPool>();
    }

    /// <summary>Number of servers in the pool.</summary>
    public int ServerCount => _servers.Count;

    /// <summary>Total agents tracked across all servers.</summary>
    public int TotalAgentCount => _servers.Values.Sum(s => s.Agents.AgentCount);

    /// <summary>All servers in the pool.</summary>
    public IEnumerable<KeyValuePair<string, VerbaraServer>> Servers => _servers;

    /// <summary>Raised after a server has started successfully and is still the current instance in the pool.</summary>
    /// <remarks>
    /// <para>
    /// For one id, the old instance's removal finishes before its replacement's addition. An instance is never
    /// announced as added after its removal; a failed or removed start can have a removal alone.
    /// Notifications run synchronously under the pool membership lock. A handler must not wait on another thread
    /// calling the pool. Add, remove and disposal requested on the notifying thread throw
    /// <see cref="InvalidOperationException"/> before changing membership or routing. A handler that must change the
    /// pool schedules the change off the handler; the membership lock then orders it. Each failing handler is logged
    /// once at Warning with its exception, and the other handlers still run.
    /// </para>
    /// <para>
    /// Follow the pool by instance, never by id alone. Initialize one subscriber-local lock and per-instance ownership
    /// records. Hold that lock while subscribing to both membership events, then release it before walking
    /// <see cref="Servers"/>. Both the added callback and each walk candidate use the same installation routine:
    /// under that local lock, re-check <c>ReferenceEquals(pool.GetServer(id), instance)</c>, skip an absent, replaced
    /// or already-owned instance, then install subscriptions and record ownership before releasing the lock. If
    /// installation fails, undo all partial subscriptions and ownership before leaving the lock. The removed callback
    /// uses the same lock to uninstall and drop only that exact owned instance; ignore an unowned removal only within
    /// this complete protocol. A walker can see a server still starting and receive its removal without an addition.
    /// </para>
    /// <para>
    /// <see cref="GetServer"/> is a lock-free current-instance read. While holding the subscriber lock, never acquire
    /// the membership lock, mutate the pool, await a pool operation or wait on another thread calling the pool.
    /// Installation and removal use only synchronous instance subscription operations. The only nested lock order
    /// is pool membership then subscriber. This closes both removal-before-recheck and removal-during-installation
    /// windows and makes duplicate walk/added sightings harmless.
    /// </para>
    /// </remarks>
    public event Action<string, VerbaraServer>? ServerAdded;

    /// <summary>Raised for every actual departure, including failed starts, before the instance is disposed.</summary>
    /// <remarks>
    /// <para>
    /// For one id, the old instance's removal finishes before its replacement's addition. An instance is never
    /// announced as added after its removal; a failed or removed start can have a removal alone.
    /// Notifications run synchronously under the pool membership lock. A handler must not wait on another thread
    /// calling the pool. Add, remove and disposal requested on the notifying thread throw
    /// <see cref="InvalidOperationException"/> before changing membership or routing. A handler that must change the
    /// pool schedules the change off the handler; the membership lock then orders it. Each failing handler is logged
    /// once at Warning with its exception, and the other handlers still run.
    /// </para>
    /// <para>
    /// Follow the pool by instance, never by id alone. Initialize one subscriber-local lock and per-instance ownership
    /// records. Hold that lock while subscribing to both membership events, then release it before walking
    /// <see cref="Servers"/>. Both the added callback and each walk candidate use the same installation routine:
    /// under that local lock, re-check <c>ReferenceEquals(pool.GetServer(id), instance)</c>, skip an absent, replaced
    /// or already-owned instance, then install subscriptions and record ownership before releasing the lock. If
    /// installation fails, undo all partial subscriptions and ownership before leaving the lock. The removed callback
    /// uses the same lock to uninstall and drop only that exact owned instance; ignore an unowned removal only within
    /// this complete protocol. A walker can see a server still starting and receive its removal without an addition.
    /// </para>
    /// <para>
    /// <see cref="GetServer"/> is a lock-free current-instance read. While holding the subscriber lock, never acquire
    /// the membership lock, mutate the pool, await a pool operation or wait on another thread calling the pool.
    /// Installation and removal use only synchronous instance subscription operations. The only nested lock order
    /// is pool membership then subscriber. This closes both removal-before-recheck and removal-during-installation
    /// windows and makes duplicate walk/added sightings harmless.
    /// </para>
    /// </remarks>
    public event Action<string, VerbaraServer>? ServerRemoved;

    /// <summary>
    /// Add a new Asterisk server to the pool, connect, and start tracking its state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool creates the AMI connection and owns it together with the server:
    /// <see cref="RemoveServerAsync"/> and <see cref="DisposeAsync"/> dispose both. If
    /// <paramref name="serverId"/> is already in the pool, the add is refused before any connection is created, and
    /// the server already held is left untouched. Two adds of the same id that both find it free each connect; the one
    /// that finds the id taken once connected disposes the server and the connection created for it before it throws.
    /// </para>
    /// <para>
    /// If the server's start throws — the token is cancelled during the state load, or the AMI session ends under the
    /// load and the connection will not come back — the server is removed from the pool with the agent routes it
    /// recorded, the server and its connection are disposed, and the start's exception reaches the caller unchanged;
    /// the id is free again. A failure while disposing them is logged and does not replace the start's exception. A
    /// server that a <see cref="RemoveServerAsync"/> or <see cref="DisposeAsync"/> took out while it was starting is
    /// released by that call, not again by the add.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The id is empty, white space or the reserved name <c>status-refused</c>.</exception>
    /// <exception cref="InvalidOperationException">The id is already held, or a membership handler requested this change
    /// on the notifying thread. No connection is created for an invalid id or a reentrant change.</exception>
    public async ValueTask<VerbaraServer> AddServerAsync(
        string serverId,
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        RefuseReentrantMembershipChange();
        ValidateServerId(serverId);
        // A duplicate costs no AMI login. The second check still guards concurrent connected adds.
        lock (_membershipLock)
        {
            if (_servers.ContainsKey(serverId))
                throw new InvalidOperationException($"Server '{serverId}' already exists in pool");
        }

        var connection = await _connectionFactory.CreateAndConnectAsync(options, cancellationToken);
        var logger = _loggerFactory.CreateLogger<VerbaraServer>();
        var server = new VerbaraServer(connection, logger);

        bool takenIn;
        lock (_membershipLock)
        {
            takenIn = _servers.TryAdd(serverId, server);
            if (takenIn)
                TrackAgentRouting(serverId, server);
        }
        if (!takenIn)
        {
            await server.DisposeAsync();
            await connection.DisposeAsync();
            throw new InvalidOperationException($"Server '{serverId}' already exists in pool");
        }

        try
        {
            await server.StartAsync(cancellationToken);
        }
        catch
        {
            // Whatever the start threw, the server must not stay: the caller never receives it, so nothing else would
            // remove it. Only this instance is removed; if a removal or the pool's disposal already took it out, that
            // call releases it and it is not released twice. The server goes before its routes, so that a login
            // dispatched meanwhile cannot write a route back once the server has unsubscribed.
            bool removed;
            lock (_membershipLock)
            {
                removed = _servers.TryRemove(new KeyValuePair<string, VerbaraServer>(serverId, server));
                if (removed)
                    Announce(ServerRemoved, nameof(ServerRemoved), serverId, server);
            }
            if (removed)
            {
                await ReleaseAsync(serverId, server);
                DropRoutesTo(serverId);
            }
            throw;
        }

        lock (_membershipLock)
        {
            if (ReferenceEquals(GetServer(serverId), server))
            {
                IndexAgentRouting(serverId, server);
                Announce(ServerAdded, nameof(ServerAdded), serverId, server);
            }
        }
        return server;
    }

    /// <summary>
    /// Adds an already-connected VerbaraServer to the pool (for cluster failover).
    /// Subscribes to agent events for routing and indexes existing agents.
    /// </summary>
    /// <remarks>
    /// The pool takes ownership of the server and of the AMI connection behind it
    /// (<see cref="VerbaraServer.Connection"/>): <see cref="RemoveServerAsync"/> and <see cref="DisposeAsync"/>
    /// dispose both. Once the server has left the pool, its connection is disposed, so do not keep using it, and
    /// do not hand the same connection in behind a second server. If <paramref name="serverId"/> is already in
    /// the pool, the server is rejected, and both it and its connection stay the caller's, undisposed.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is empty, white space or the reserved name <c>status-refused</c>.</exception>
    /// <exception cref="InvalidOperationException">The id is already held, or a membership handler requested this change
    /// on the notifying thread. No connection is created for an invalid id or a reentrant change.</exception>
    public void AddExistingServer(string serverId, VerbaraServer server)
    {
        RefuseReentrantMembershipChange();
        ValidateServerId(serverId);
        lock (_membershipLock)
        {
            if (!_servers.TryAdd(serverId, server))
                throw new InvalidOperationException($"Server '{serverId}' already exists in the pool.");
            TrackAgentRouting(serverId, server);
            IndexAgentRouting(serverId, server);
            Announce(ServerAdded, nameof(ServerAdded), serverId, server);
        }
    }

    /// <summary>
    /// Remove and disconnect a server from the pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposes the server, then the AMI connection behind it, whether the pool created that connection
    /// (<see cref="AddServerAsync"/>) or it was handed in (<see cref="AddExistingServer"/>). The connection ends
    /// for good: a reconnect in progress stops, and it does not log in again when Asterisk comes back. Awaited
    /// from inside that connection's own event dispatch, such as an <c>OnEvent</c> handler, the removal completes
    /// without waiting for that dispatch.
    /// </para>
    /// <para>
    /// The server and its connection are disposed in separate attempts: when the server's disposal throws, the
    /// connection is still disposed. Each failure is logged at Error with the server's id and the exception, and is not
    /// thrown. Then the agent routes that point to this server are removed; a route another server owns for the same
    /// agent id is left in place.
    /// </para>
    /// </remarks>
    public async ValueTask RemoveServerAsync(string serverId)
    {
        RefuseReentrantMembershipChange();
        var server = TakeOut(serverId);
        if (server is not null)
        {
            await ReleaseAsync(serverId, server);
            DropRoutesTo(serverId);
        }
    }

    /// <summary>
    /// Get the server that owns a specific agent.
    /// </summary>
    public VerbaraServer? GetServerForAgent(string agentId)
    {
        if (_agentRouting.TryGetValue(agentId, out var serverId)
            && _servers.TryGetValue(serverId, out var server))
        {
            return server;
        }
        return null;
    }

    /// <summary>
    /// Get a server by its ID with a lock-free current-instance read, suitable for a pool follower's exact-instance check.
    /// </summary>
    public VerbaraServer? GetServer(string serverId) =>
        _servers.GetValueOrDefault(serverId);

    /// <summary>
    /// Disposes every server in the pool and the AMI connection behind each, whether the pool created that
    /// connection or it was handed in with <see cref="AddExistingServer"/>, then empties the pool.
    /// </summary>
    /// <remarks>
    /// Each server is taken out of the pool, then it and its connection are disposed in separate attempts, so a server
    /// or a connection whose disposal throws does not stop the disposal of its own connection or of any other server.
    /// Each such failure is logged at Error with the server's id and the exception, and is not thrown: the disposal
    /// returns normally, with no server and no agent route left in the pool. A second call disposes nothing and logs
    /// nothing.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        RefuseReentrantMembershipChange();
        // Taken out one by one before each is disposed: a second or concurrent disposal finds nothing to dispose again.
        foreach (var serverId in _servers.Keys)
        {
            await TakeOutAndReleaseAsync(serverId);
        }
        _agentRouting.Clear();
    }

    /// <summary>
    /// Takes the server under <paramref name="serverId"/> out of the pool and releases it, when the pool still holds one:
    /// a concurrent caller that took it out first releases it instead.
    /// </summary>
    private async ValueTask TakeOutAndReleaseAsync(string serverId)
    {
        var server = TakeOut(serverId);
        if (server is not null)
            await ReleaseAsync(serverId, server);
    }

    private VerbaraServer? TakeOut(string serverId)
    {
        lock (_membershipLock)
        {
            if (!_servers.TryRemove(serverId, out var server))
                return null;
            Announce(ServerRemoved, nameof(ServerRemoved), serverId, server);
            return server;
        }
    }

    private void RefuseReentrantMembershipChange()
    {
        if (_membershipLock.IsHeldByCurrentThread)
            throw new InvalidOperationException("A membership notification handler must schedule pool changes off the notifying thread.");
    }

    private static void ValidateServerId(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (string.Equals(serverId, "status-refused", StringComparison.Ordinal))
            throw new ArgumentException("The server id 'status-refused' is reserved for pool health data.", nameof(serverId));
    }

    private void Announce(Action<string, VerbaraServer>? handlers, string notification, string serverId, VerbaraServer server)
    {
        if (handlers is null)
            return;
        foreach (Action<string, VerbaraServer> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(serverId, server);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                VerbaraServerPoolLog.MembershipSubscriberFailed(_logger, notification, serverId, ex);
            }
        }
    }

    private void TrackAgentRouting(string serverId, VerbaraServer server)
    {
        server.Agents.AgentLoggedIn += a => _agentRouting[a.AgentId] = serverId;
        server.Agents.AgentLoggedOff += a => _agentRouting.TryRemove(a.AgentId, out _);
    }

    private void IndexAgentRouting(string serverId, VerbaraServer server)
    {
        foreach (var agent in server.Agents.Agents)
            _agentRouting[agent.AgentId] = serverId;
    }

    /// <summary>Removes every agent route that points to <paramref name="serverId"/>, and only those.</summary>
    private void DropRoutesTo(string serverId)
    {
        foreach (var route in _agentRouting.Where(r => string.Equals(r.Value, serverId, StringComparison.Ordinal)))
        {
            // Removed only if it still points to this server: a route re-pointed meanwhile survives.
            _agentRouting.TryRemove(route);
        }
    }

    /// <summary>
    /// Disposes a server the pool has taken out, then the AMI connection behind it, each in its own attempt. A failure
    /// of either is logged and not thrown, so that the other is still disposed and the caller's own work goes on.
    /// </summary>
    private async ValueTask ReleaseAsync(string serverId, VerbaraServer server)
    {
        // Read before the server is disposed. The server goes first, so it has unhooked the connection before the
        // connection ends.
        var connection = server.Connection;
        try
        {
            await server.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A server throws on disposal only through foreign code, such as the ARI client hung on it. Running out of
            // memory is the process's failure, not the server's, so it is not swallowed here.
            VerbaraServerPoolLog.ServerDisposeFailed(_logger, serverId, ex);
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            VerbaraServerPoolLog.ConnectionDisposeFailed(_logger, serverId, ex);
        }
    }
}
