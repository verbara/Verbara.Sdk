using System.ComponentModel.DataAnnotations;

namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// Configuration options for the ARI client.
/// </summary>
public sealed class AriClientOptions
{
    /// <summary>ARI base URL. Default: "http://localhost:8088".</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "http://localhost:8088";

    /// <summary>ARI username.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>ARI password.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Stasis application name.</summary>
    [Required]
    public string Application { get; set; } = string.Empty;

    /// <summary>Auto-reconnect WebSocket on disconnect. Default: true.</summary>
    public bool AutoReconnect { get; set; } = true;
}
