using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Connection;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Server;

/// <summary>
/// Manages multiple VerbaraServer instances connected to different Asterisk PBX servers.
/// Provides federated agent routing so callers can locate which server owns a given agent.
/// Designed for 100K+ agents distributed across 20-50 Asterisk instances.
/// </summary>
public sealed class VerbaraServerPool : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, VerbaraServer> _servers = new();
    private readonly ConcurrentDictionary<string, string> _agentRouting = new();

    public VerbaraServerPool(
        IAmiConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory)
    {
        _connectionFactory = connectionFactory;
        _loggerFactory = loggerFactory;
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
    /// The pool creates the AMI connection and owns it together with the server:
    /// <see cref="RemoveServerAsync"/> and <see cref="DisposeAsync"/> dispose both. If
    /// <paramref name="serverId"/> is already in the pool, the server and the connection created for it are
    /// disposed before the exception is thrown, and the server already held is left untouched.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A server with <paramref name="serverId"/> is already in the pool.</exception>
    public async ValueTask<VerbaraServer> AddServerAsync(
        string serverId,
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
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

        await server.StartAsync(cancellationToken);

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
    /// Disposes the server, then the AMI connection behind it, whether the pool created that connection
    /// (<see cref="AddServerAsync"/>) or it was handed in (<see cref="AddExistingServer"/>). The connection ends
    /// for good: a reconnect in progress stops, and it does not log in again when Asterisk comes back. Awaited
    /// from inside that connection's own event dispatch, such as an <c>OnEvent</c> handler, the removal completes
    /// without waiting for that dispatch.
    /// </remarks>
    public async ValueTask RemoveServerAsync(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            // Clean up routing entries for this server's agents
            foreach (var agent in server.Agents.Agents)
            {
                _agentRouting.TryRemove(agent.AgentId, out _);
            }
            // Read before the server is disposed. The server goes first, so it has unhooked the
            // connection before the connection ends.
            var connection = server.Connection;
            await server.DisposeAsync();
            await connection.DisposeAsync();
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
    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            var connection = server.Connection;
            await server.DisposeAsync();
            await connection.DisposeAsync();
        }
        _servers.Clear();
        _agentRouting.Clear();
    }
}
