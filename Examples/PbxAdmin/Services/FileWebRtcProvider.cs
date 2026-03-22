using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace PbxAdmin.Services;

internal static partial class FileWebRtcLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[WEBRTC_FILE] Provisioned: server={ServerId} extension={Extension} file={FilePath}")]
    public static partial void Provisioned(ILogger logger, string serverId, string extension, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WEBRTC_FILE] Module reload failed after provisioning extension={Extension}")]
    public static partial void ReloadFailed(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Error, Message = "[WEBRTC_FILE] Provision failed: server={ServerId} username={Username}")]
    public static partial void ProvisionFailed(ILogger logger, Exception exception, string serverId, string username);
}

/// <summary>
/// Provisions WebRTC extensions via AMI UpdateConfig (pjsip.conf sections) followed by an
/// AMI module reload. Intended for file-based (non-Realtime) Asterisk servers such as the
/// Docker demo environment.
/// </summary>
public sealed class FileWebRtcProvider : IWebRtcExtensionProvider
{
    private readonly IConfigProviderResolver _resolver;
    private readonly SoftphoneOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileWebRtcProvider> _logger;

    /// <summary>Config filename targeted by all operations in this provider.</summary>
    private const string PjsipConf = "pjsip.conf";

    /// <summary>Default pjsip config path used in the Docker demo environment.</summary>
    private const string DefaultPjsipPath = "/etc/asterisk/pjsip.conf";

    public FileWebRtcProvider(
        IConfigProviderResolver resolver,
        IOptions<SoftphoneOptions> options,
        IConfiguration configuration,
        ILogger<FileWebRtcProvider> logger)
    {
        _resolver = resolver;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WebRtcCredentials> ProvisionAsync(string serverId, string username, CancellationToken ct = default)
    {
        var extensionId = $"{_options.ExtensionPrefix}-{username}";
        var password = Guid.NewGuid().ToString("N")[..16];
        var wssHost = _options.WssHost ?? "localhost";
        var wssPort = GetWssPort(serverId);
        var wssUrl = string.Create(CultureInfo.InvariantCulture, $"wss://{wssHost}:{wssPort}/ws");
        var filePath = GetPjsipFilePath(serverId);

        try
        {
            var configProvider = _resolver.GetProvider(serverId);
            var endpointVars = BuildEndpointVariables(extensionId);
            var authVars = BuildAuthVariables(extensionId, password);
            var aorVars = BuildAorVariables();

            // Delete existing sections first to ensure a clean upsert
            await configProvider.DeleteSectionAsync(serverId, PjsipConf, extensionId, ct);
            await configProvider.DeleteSectionAsync(serverId, PjsipConf, $"{extensionId}-auth", ct);
            await configProvider.DeleteSectionAsync(serverId, PjsipConf, $"{extensionId}-aor", ct);

            // Recreate all 3 PJSIP sections
            await configProvider.CreateSectionAsync(serverId, PjsipConf, extensionId, endpointVars, ct: ct);
            await configProvider.CreateSectionAsync(serverId, PjsipConf, $"{extensionId}-auth", authVars, ct: ct);
            await configProvider.CreateSectionAsync(serverId, PjsipConf, $"{extensionId}-aor", aorVars, ct: ct);

            if (!await configProvider.ReloadModuleAsync(serverId, "res_pjsip.so", ct))
                FileWebRtcLog.ReloadFailed(_logger, extensionId);

            FileWebRtcLog.Provisioned(_logger, serverId, extensionId, filePath);
            return new WebRtcCredentials(extensionId, password, wssUrl);
        }
        catch (Exception ex)
        {
            FileWebRtcLog.ProvisionFailed(_logger, ex, serverId, username);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string serverId, string extensionId, CancellationToken ct = default)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var section = await configProvider.GetSectionAsync(serverId, PjsipConf, extensionId, ct);
        return section is not null;
    }

    // -----------------------------------------------------------------------
    // Variable builders
    // -----------------------------------------------------------------------

    private Dictionary<string, string> BuildEndpointVariables(string extensionId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "endpoint",
            ["transport"] = "transport-wss",
            ["aors"] = extensionId,
            ["auth"] = $"{extensionId}-auth",
            ["context"] = _options.Context,
            ["disallow"] = "all",
            ["allow"] = _options.DefaultCodecs,
            ["direct_media"] = "no",
            ["webrtc"] = "yes",
            ["dtls_auto_generate_cert"] = "yes",
            ["use_avpf"] = "yes",
            ["media_encryption"] = "dtls",
            ["ice_support"] = "yes",
            ["media_use_received_transport"] = "yes",
            ["rtcp_mux"] = "yes",
        };

    private static Dictionary<string, string> BuildAuthVariables(string extensionId, string password) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "auth",
            ["auth_type"] = "userpass",
            ["username"] = extensionId,
            ["password"] = password,
        };

    private static Dictionary<string, string> BuildAorVariables() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "aor",
            ["max_contacts"] = "1",
            ["remove_existing"] = "yes",
        };

    /// <summary>
    /// Builds a human-readable INI representation for diagnostics. Not written to disk;
    /// the actual config write uses AMI UpdateConfig via <see cref="IConfigProvider"/>.
    /// </summary>
    internal string BuildPjsipIni(string extensionId, string password)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine(string.Create(ic, $"; WebRTC endpoint provisioned by PbxAdmin — {DateTimeOffset.UtcNow:u}"));
        sb.AppendLine(string.Create(ic, $"[{extensionId}]"));
        sb.AppendLine("type=endpoint");
        sb.AppendLine("transport=transport-wss");
        sb.AppendLine(string.Create(ic, $"aors={extensionId}"));
        sb.AppendLine(string.Create(ic, $"auth={extensionId}-auth"));
        sb.AppendLine(string.Create(ic, $"context={_options.Context}"));
        sb.AppendLine("disallow=all");
        sb.AppendLine(string.Create(ic, $"allow={_options.DefaultCodecs}"));
        sb.AppendLine("direct_media=no");
        sb.AppendLine("webrtc=yes");
        sb.AppendLine();

        sb.AppendLine(string.Create(ic, $"[{extensionId}-auth]"));
        sb.AppendLine("type=auth");
        sb.AppendLine("auth_type=userpass");
        sb.AppendLine(string.Create(ic, $"username={extensionId}"));
        sb.AppendLine(string.Create(ic, $"password={password}"));
        sb.AppendLine();

        sb.AppendLine(string.Create(ic, $"[{extensionId}-aor]"));
        sb.AppendLine("type=aor");
        sb.AppendLine("max_contacts=1");
        sb.AppendLine("remove_existing=yes");

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Config helpers
    // -----------------------------------------------------------------------

    private int GetWssPort(string serverId)
    {
        foreach (var section in _configuration.GetSection("Asterisk:Servers").GetChildren())
        {
            var id = section["Id"] ?? "default";
            if (string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
            {
                var port = section["WssPort"];
                if (port is not null && int.TryParse(port, out var p))
                    return p;
            }
        }

        return _options.WssPort;
    }

    private string GetPjsipFilePath(string serverId)
    {
        foreach (var section in _configuration.GetSection("Asterisk:Servers").GetChildren())
        {
            var id = section["Id"] ?? "default";
            if (string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                return section["PjsipConfigPath"] ?? DefaultPjsipPath;
        }

        return DefaultPjsipPath;
    }
}
