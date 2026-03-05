using System.ComponentModel.DataAnnotations;
using Asterisk.Sdk.Ari.Audio;

namespace Asterisk.Sdk.Ari.Client;

/// <summary>
/// Configuration options for the ARI client.
/// </summary>
public sealed class AriClientOptions
{
    /// <summary>Optional audio server configuration. Set to enable AudioSocket/WebSocket audio streaming.</summary>
    public Action<AudioServerOptions>? ConfigureAudioServer { get; set; }

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

    /// <summary>Initial delay before first reconnection attempt. Default: 1 second.</summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum delay between reconnection attempts. Default: 30 seconds.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Multiplier applied to the delay after each failed reconnection attempt. Default: 2.0.</summary>
    public double ReconnectMultiplier { get; set; } = 2.0;

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; set; }
}
