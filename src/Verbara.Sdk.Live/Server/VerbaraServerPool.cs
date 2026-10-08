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
    /// <exception cref="InvalidOperationException">A server with <paramref name="serverId"/> is already in the pool.</exception>
    public async ValueTask<VerbaraServer> AddServerAsync(
        string serverId,
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        // Refused before a connection is made: a duplicate costs no AMI login. The TryAdd below still guards two adds of
        // the same id that both pass this check.
        if (_servers.ContainsKey(serverId))
            throw new InvalidOperationException($"Server '{serverId}' already exists in pool");

        var connection = await _connectionFactory.CreateAndConnectAsync(options, cancellationToken);
        var logger = _loggerFactory.CreateLogger<VerbaraServer>();
        var server = new VerbaraServer(connection, logger);

        if (!_servers.TryAdd(serverId, server))
        {
            await server.DisposeAsync();
            await connection.DisposeAsync();
            throw new InvalidOperationException($"Server '{serverId}' already exists in pool");
        }

        // Subscribe to agent events to maintain routing table
        server.Agents.AgentLoggedIn += a => _agentRouting[a.AgentId] = serverId;
        server.Agents.AgentLoggedOff += a => _agentRouting.TryRemove(a.AgentId, out _);

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
            if (_servers.TryRemove(new KeyValuePair<string, VerbaraServer>(serverId, server)))
            {
                await ReleaseAsync(serverId, server);
                DropRoutesTo(serverId);
            }
            throw;
        }

        // Index existing agents after initial state load
        foreach (var agent in server.Agents.Agents)
        {
            _agentRouting[agent.AgentId] = serverId;
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
    /// <exception cref="InvalidOperationException">A server with <paramref name="serverId"/> is already in the pool.</exception>
    public void AddExistingServer(string serverId, VerbaraServer server)
    {
        if (!_servers.TryAdd(serverId, server))
            throw new InvalidOperationException($"Server '{serverId}' already exists in the pool.");

        // Subscribe to agent events to maintain routing table
        server.Agents.AgentLoggedIn += a => _agentRouting[a.AgentId] = serverId;
        server.Agents.AgentLoggedOff += a => _agentRouting.TryRemove(a.AgentId, out _);

        // Index existing agents
        foreach (var agent in server.Agents.Agents)
        {
            _agentRouting[agent.AgentId] = serverId;
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
        if (_servers.TryRemove(serverId, out var server))
        {
            // The server goes before its routes, so that a login dispatched meanwhile cannot write a route back once
            // the server has unsubscribed.
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
    /// Get a server by its ID.
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
        if (_servers.TryRemove(serverId, out var server))
            await ReleaseAsync(serverId, server);
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
