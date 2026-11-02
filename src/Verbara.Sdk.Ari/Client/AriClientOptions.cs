using System.ComponentModel.DataAnnotations;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Client;

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
    /// <remarks>
    /// A client with events registers this application. With <see cref="EventsEnabled"/> off the client registers nothing,
    /// and this is only the name <c>Channels.CreateAsync</c> and <c>Channels.OriginateAsync</c> without an extension send.
    /// </remarks>
    [Required]
    public string Application { get; set; } = string.Empty;

    /// <summary>Open the events WebSocket and register <see cref="Application"/>. Default: true.</summary>
    /// <remarks>
    /// With <c>false</c> the client opens no events WebSocket, registers no Stasis application, runs no reconnect loop and
    /// keeps no event buffer. Its REST resources work without any connection, its state reads
    /// <see cref="Verbara.Sdk.Enums.AriConnectionState.RestOnly"/>, and <c>Subscribe</c> throws.
    /// <para>
    /// <see cref="Application"/> stays required, and on such a client it names an application someone else has to register.
    /// Nothing checks that anyone does, and nothing can: Asterisk answers <c>200</c> to an originate into an application
    /// with no registrant and the channel is gone at once.
    /// </para>
    /// The client reads this once, when it is constructed; changing it afterwards changes nothing.
    /// </remarks>
    public bool EventsEnabled { get; set; } = true;

    /// <summary>Auto-reconnect WebSocket on disconnect. Default: true.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Initial delay before first reconnection attempt. Default: 1 second.</summary>
    /// <remarks>
    /// Accepted: from zero to <see cref="int.MaxValue"/> milliseconds (about 24.8 days, the longest delay .NET can wait).
    /// Checked only when <see cref="AutoReconnect"/> is on: by the options validator, naming this option, and by the
    /// client's constructor, which throws <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    [ReconnectRule]
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum delay between reconnection attempts. Default: 30 seconds.</summary>
    /// <remarks>
    /// Accepted: from <see cref="ReconnectInitialDelay"/> to <see cref="int.MaxValue"/> milliseconds (about 24.8 days);
    /// <see cref="Timeout.InfiniteTimeSpan"/> is not accepted. Checked only when <see cref="AutoReconnect"/> is on, as
    /// <see cref="ReconnectInitialDelay"/> is.
    /// </remarks>
    [ReconnectRule]
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Multiplier applied to the delay after each failed reconnection attempt. Default: 2.0.</summary>
    /// <remarks>
    /// Accepted: a finite number of at least 1.0 (exactly 1.0 keeps the delay constant). Checked only when
    /// <see cref="AutoReconnect"/> is on, as <see cref="ReconnectInitialDelay"/> is.
    /// </remarks>
    [ReconnectRule]
    public double ReconnectMultiplier { get; set; } = 2.0;

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    /// <remarks>
    /// Accepted: 0 or more. Checked only when <see cref="AutoReconnect"/> is on, as <see cref="ReconnectInitialDelay"/> is.
    /// </remarks>
    [ReconnectRule]
    public int MaxReconnectAttempts { get; set; }
}
