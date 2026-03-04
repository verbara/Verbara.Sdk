using System.Collections.Concurrent;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Live.Server;

namespace DashboardExample.Services;

internal static partial class MonitorServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {ServerId} ({Host}:{Port}) — Asterisk {Version}")]
    public static partial void Connected(ILogger logger, string serverId, string host, int port, string? version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection lost to server {ServerId}")]
    public static partial void ConnectionLost(ILogger logger, Exception? exception, string serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to connect to server {ServerId}")]
    public static partial void ConnectFailed(ILogger logger, Exception exception, string serverId);
}

public sealed class AsteriskMonitorService : IHostedService, IAsyncDisposable
{
    private readonly IAmiConnectionFactory _factory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly EventLogService _eventLog;
    private readonly CallFlowTracker _callFlowTracker;
    private readonly IConfiguration _config;
    private readonly ILogger<AsteriskMonitorService> _logger;
    private readonly ConcurrentDictionary<string, ServerEntry> _servers = new();

    public IEnumerable<KeyValuePair<string, ServerEntry>> Servers => _servers;

    public AsteriskMonitorService(
        IAmiConnectionFactory factory,
        ILoggerFactory loggerFactory,
        EventLogService eventLog,
        CallFlowTracker callFlowTracker,
        IConfiguration config,
        ILogger<AsteriskMonitorService> logger)
    {
        _factory = factory;
        _loggerFactory = loggerFactory;
        _eventLog = eventLog;
        _callFlowTracker = callFlowTracker;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();

        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
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
                var callFlowSub = connection.Subscribe(_callFlowTracker.CreateObserver(id));

                await server.StartAsync(cancellationToken);

                _servers[id] = new ServerEntry(connection, server, eventLogSub, callFlowSub);
                MonitorServiceLog.Connected(_logger, id, options.Hostname, options.Port, server.AsteriskVersion);
            }
            catch (Exception ex)
            {
                MonitorServiceLog.ConnectFailed(_logger, ex, id);
            }
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
            entry.CallFlowSubscription.Dispose();
            await entry.Server.DisposeAsync();
            await entry.Connection.DisposeAsync();
        }
        _servers.Clear();
    }

    public ServerEntry? GetServer(string serverId) =>
        _servers.GetValueOrDefault(serverId);

    public sealed record ServerEntry(
        IAmiConnection Connection,
        AsteriskServer Server,
        IDisposable Subscription,
        IDisposable CallFlowSubscription);

    private sealed class EventLogObserver(string serverId, EventLogService eventLog)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => eventLog.Add(serverId, value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
