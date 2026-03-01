namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// Configuration options for the ARI client.
/// </summary>
public sealed class AriClientOptions
{
    /// <summary>ARI base URL. Default: "http://localhost:8088".</summary>
    public string BaseUrl { get; set; } = "http://localhost:8088";

    /// <summary>ARI username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>ARI password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Stasis application name.</summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>Auto-reconnect WebSocket on disconnect. Default: true.</summary>
    public bool AutoReconnect { get; set; } = true;
}
