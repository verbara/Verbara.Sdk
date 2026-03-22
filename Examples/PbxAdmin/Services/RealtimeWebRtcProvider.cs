using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PbxAdmin.Services;

internal static partial class RealtimeWebRtcLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[WEBRTC_DB] Provisioned: server={ServerId} extension={Extension}")]
    public static partial void Provisioned(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Error, Message = "[WEBRTC_DB] Provision failed: server={ServerId} username={Username}")]
    public static partial void ProvisionFailed(ILogger logger, Exception exception, string serverId, string username);
}

/// <summary>
/// Provisions WebRTC extensions via PostgreSQL Realtime tables (ps_endpoints, ps_auths, ps_aors).
/// Uses upsert so that repeated calls for the same username update credentials in-place.
/// </summary>
public sealed class RealtimeWebRtcProvider : IWebRtcExtensionProvider
{
    private readonly IConfigProviderResolver _resolver;
    private readonly AsteriskMonitorService _monitor;
    private readonly SoftphoneOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RealtimeWebRtcProvider> _logger;

    public RealtimeWebRtcProvider(
        IConfigProviderResolver resolver,
        AsteriskMonitorService monitor,
        IOptions<SoftphoneOptions> options,
        IConfiguration configuration,
        ILogger<RealtimeWebRtcProvider> logger)
    {
        _resolver = resolver;
        _monitor = monitor;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WebRtcCredentials> ProvisionAsync(string serverId, string username, CancellationToken ct = default)
    {
        var extensionId = $"{_options.ExtensionPrefix}-{username}";
        var password = Guid.NewGuid().ToString("N")[..16];
        var serverEntry = _monitor.GetServer(serverId);
        var wssHost = _options.WssHost ?? "localhost";
        var wssPort = GetWssPort(serverId);
        var scheme = _options.UseTls ? "wss" : "ws";
        var wssUrl = $"{scheme}://{wssHost}:{wssPort}/ws";

        var connStr = GetConnectionString(serverId);
        if (connStr is null)
            throw new InvalidOperationException($"No Realtime connection string found for server '{serverId}'.");

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connStr);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // Upsert ps_endpoints
            const string endpointSql = """
                INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media,
                    webrtc, dtls_auto_generate_cert, use_avpf, media_encryption, ice_support,
                    media_use_received_transport, rtcp_mux, bundle, max_audio_streams, max_video_streams)
                VALUES (@id, @transport, @aors, @auth, @context, 'all', @codecs, 'no',
                    'yes', 'yes', 'yes', 'dtls', 'yes',
                    'yes', 'yes', 'yes', 1, 1)
                ON CONFLICT (id) DO UPDATE SET
                    transport = EXCLUDED.transport,
                    aors = EXCLUDED.aors,
                    auth = EXCLUDED.auth,
                    context = EXCLUDED.context,
                    allow = EXCLUDED.allow,
                    webrtc = EXCLUDED.webrtc
                """;

            await conn.ExecuteAsync(new CommandDefinition(endpointSql, new
            {
                id = extensionId,
                transport = _options.UseTls ? "transport-wss" : "transport-ws",
                aors = extensionId,
                auth = $"{extensionId}-auth",
                context = _options.Context,
                codecs = _options.DefaultCodecs,
            }, commandTimeout: 15, cancellationToken: ct));

            // Upsert ps_auths
            const string authSql = """
                INSERT INTO ps_auths (id, auth_type, username, password)
                VALUES (@id, 'userpass', @username, @password)
                ON CONFLICT (id) DO UPDATE SET
                    username = EXCLUDED.username,
                    password = EXCLUDED.password
                """;

            await conn.ExecuteAsync(new CommandDefinition(authSql, new
            {
                id = $"{extensionId}-auth",
                username = extensionId,
                password,
            }, commandTimeout: 15, cancellationToken: ct));

            // Upsert ps_aors
            const string aorSql = """
                INSERT INTO ps_aors (id, max_contacts, remove_existing)
                VALUES (@id, 1, 'yes')
                ON CONFLICT (id) DO UPDATE SET
                    max_contacts = EXCLUDED.max_contacts,
                    remove_existing = EXCLUDED.remove_existing
                """;

            await conn.ExecuteAsync(new CommandDefinition(aorSql, new
            {
                id = extensionId,
            }, commandTimeout: 15, cancellationToken: ct));

            // Reload res_pjsip so the new extension takes effect
            if (serverEntry is not null)
            {
                await _resolver.GetProvider(serverId).ReloadModuleAsync(serverId, "res_pjsip.so", ct);
            }

            RealtimeWebRtcLog.Provisioned(_logger, serverId, extensionId);
            return new WebRtcCredentials(extensionId, password, wssUrl);
        }
        catch (Exception ex)
        {
            RealtimeWebRtcLog.ProvisionFailed(_logger, ex, serverId, username);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string serverId, string extensionId, CancellationToken ct = default)
    {
        var connStr = GetConnectionString(serverId);
        if (connStr is null) return false;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connStr);
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            var result = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM ps_endpoints WHERE id = @id",
                new { id = extensionId },
                commandTimeout: 10,
                cancellationToken: ct));

            return result > 0;
        }
        catch
        {
            return false;
        }
    }

    private string? GetConnectionString(string serverId)
    {
        var servers = _configuration.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            if (!string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            return section["RealtimeConnectionString"];
        }

        return null;
    }

    private int GetWssPort(string serverId)
    {
        var servers = _configuration.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            var id = section["Id"] ?? "default";
            if (!string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            var port = section["WssPort"];
            if (port is not null && int.TryParse(port, out var p))
                return p;
        }

        return _options.WssPort;
    }
}
