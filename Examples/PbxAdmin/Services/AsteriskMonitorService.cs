using System.Collections.Concurrent;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;
using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;

namespace PbxAdmin.Services;

internal static partial class MonitorServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[MONITOR] Connected: server={ServerId} host={Host} port={Port} version={Version}")]
    public static partial void Connected(ILogger logger, string serverId, string host, int port, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MONITOR] Config connection ready: server={ServerId}")]
    public static partial void ConfigConnected(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MONITOR] Config connection failed (using event connection as fallback): server={ServerId}")]
    public static partial void ConfigConnectFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MONITOR] Config connection: server={ServerId} mode={ConfigMode}")]
    public static partial void ConnectionSummary(ILogger logger, string serverId, string configMode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MONITOR] Connection lost: server={ServerId}")]
    public static partial void ConnectionLost(ILogger logger, Exception? exception, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[MONITOR] Connect failed: server={ServerId}")]
    public static partial void ConnectFailed(ILogger logger, Exception exception, string serverId);
}

public sealed class AsteriskMonitorService : IHostedService, IAsyncDisposable
{
    private readonly IAmiConnectionFactory _factory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly EventLogService _eventLog;
    private readonly ICallSessionManager _sessionManager;
    private readonly IConfiguration _config;
    private readonly ILogger<AsteriskMonitorService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ServerEntry> _servers = new();

    public IEnumerable<KeyValuePair<string, ServerEntry>> Servers => _servers;

    public AsteriskMonitorService(
        IAmiConnectionFactory factory,
        ILoggerFactory loggerFactory,
        EventLogService eventLog,
        ICallSessionManager sessionManager,
        IConfiguration config,
        ILogger<AsteriskMonitorService> logger,
        IServiceProvider serviceProvider)
    {
        _factory = factory;
        _loggerFactory = loggerFactory;
        _eventLog = eventLog;
        _sessionManager = sessionManager;
        _config = config;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Timeout for config operations (GetConfig, UpdateConfig, Command).
    /// Asterisk can be slow when config files are in non-standard paths or split across directories.
    /// </summary>
    private static readonly TimeSpan ConfigResponseTimeout = TimeSpan.FromSeconds(30);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();

        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            var configModeStr = section["ConfigMode"] ?? "File";
            var configMode = string.Equals(configModeStr, "Realtime", StringComparison.OrdinalIgnoreCase)
                ? ConfigMode.Realtime
                : ConfigMode.File;
            var options = new AmiConnectionOptions
            {
                Hostname = section["Hostname"] ?? "localhost",
                Port = int.TryParse(section["Port"], out var p) ? p : 5038,
                Username = section["Username"] ?? "",
                Password = section["Password"] ?? "",
                AutoReconnect = true
            };

            try
            {
                var connection = await _factory.CreateAndConnectAsync(options, cancellationToken);
                var serverLogger = _loggerFactory.CreateLogger<AsteriskServer>();
                var server = new AsteriskServer(connection, serverLogger);

                server.ConnectionLost += ex =>
                    MonitorServiceLog.ConnectionLost(_logger, ex, id);

                var eventLogSub = connection.Subscribe(new EventLogObserver(id, _eventLog));

                await server.StartAsync(cancellationToken);

                _sessionManager.AttachToServer(server, id);

                // Create a dedicated config connection with a longer timeout.
                // No subscriptions — this connection is silent (no event pump overhead).
                // Falls back to the event connection if the config connection fails.
                var configConnection = await CreateConfigConnectionAsync(id, options, cancellationToken);

                var effectiveConfigConn = configConnection ?? connection;
                _servers[id] = new ServerEntry(connection, effectiveConfigConn, server, eventLogSub, configMode);
                MonitorServiceLog.Connected(_logger, id, options.Hostname, options.Port, server.AsteriskVersion);
                MonitorServiceLog.ConnectionSummary(_logger, id,
                    configConnection is not null ? "dedicated (30s timeout)" : "shared (fallback to event connection)");

                var discoveryService = _serviceProvider.GetService<DialplanDiscoveryService>();
                if (discoveryService is not null)
                    _ = discoveryService.RefreshAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MonitorServiceLog.ConnectFailed(_logger, ex, id);
            }
        }
    }

    private async Task<IAmiConnection?> CreateConfigConnectionAsync(
        string serverId, AmiConnectionOptions baseOptions, CancellationToken cancellationToken)
    {
        var configOptions = new AmiConnectionOptions
        {
            Hostname = baseOptions.Hostname,
            Port = baseOptions.Port,
            Username = baseOptions.Username,
            Password = baseOptions.Password,
            AutoReconnect = baseOptions.AutoReconnect,
            DefaultResponseTimeout = ConfigResponseTimeout
        };

        try
        {
            var conn = await _factory.CreateAndConnectAsync(configOptions, cancellationToken);
            MonitorServiceLog.ConfigConnected(_logger, serverId);
            return conn;
        }
        catch (Exception ex)
        {
            MonitorServiceLog.ConfigConnectFailed(_logger, ex, serverId);
            return null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, entry) in _servers)
        {
            entry.Subscription.Dispose();
            await entry.Server.DisposeAsync();
            if (!ReferenceEquals(entry.ConfigConnection, entry.Connection))
                await entry.ConfigConnection.DisposeAsync();
            await entry.Connection.DisposeAsync();
        }
        _servers.Clear();
    }

    public ServerEntry? GetServer(string serverId) =>
        _servers.GetValueOrDefault(serverId);

    /// <param name="Connection">Primary connection for real-time events (2s timeout).</param>
    /// <param name="ConfigConnection">Dedicated connection for config operations (30s timeout). Falls back to Connection if creation failed.</param>
    /// <param name="Server">Live domain model for real-time state.</param>
    /// <param name="Subscription">Event log observer subscription.</param>
    /// <param name="ConfigMode">File or Realtime config mode.</param>
    public sealed record ServerEntry(
        IAmiConnection Connection,
        IAmiConnection ConfigConnection,
        AsteriskServer Server,
        IDisposable Subscription,
        ConfigMode ConfigMode);

    private sealed class EventLogObserver(string serverId, EventLogService eventLog)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => eventLog.Add(serverId, value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
