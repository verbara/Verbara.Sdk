using System.ComponentModel.DataAnnotations;

namespace Verbara.Sdk.Ami.Connection;

/// <summary>
/// Configuration options for an AMI connection.
/// </summary>
public sealed class AmiConnectionOptions
{
    /// <summary>Asterisk server hostname or IP. Default: "localhost".</summary>
    [Required]
    public string Hostname { get; set; } = "localhost";

    /// <summary>AMI port. Default: 5038.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 5038;

    /// <summary>AMI username.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>AMI password (used for MD5 challenge-response).</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Enable SSL/TLS. Default: false.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Socket connection timeout. Default: 5 seconds.</summary>
    /// <remarks>
    /// Accepted: more than zero and at most <see cref="int.MaxValue"/> milliseconds (about 24.8 days); zero, a negative
    /// value and <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> are rejected by the options validator and by the
    /// <see cref="AmiConnection"/> constructor, naming this option. Checked whether or not <see cref="AutoReconnect"/> is
    /// on. The connection holds the options object it was given, so a value changed afterwards to one of those is
    /// rejected where it is used: <see cref="AmiConnection.ConnectAsync"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> naming this option before it dials, and the reconnect loop ends the
    /// connection once, carrying that exception, instead of retrying. It also bounds how long <see cref="AmiConnection.ConnectAsync"/> waits for the release of a session the
    /// connection lost on its own before it connects, so a connect can take up to twice this value: that wait, then the
    /// connect itself.
    /// </remarks>
    [ConnectTimeoutRule]
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Socket read idle timeout. Default: infinite (TimeSpan.Zero).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>Default timeout waiting for action responses. Default: 2 seconds.</summary>
    public TimeSpan DefaultResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Default timeout waiting for event-generating action completion. Default: 5 seconds.</summary>
    public TimeSpan DefaultEventTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Enable automatic reconnection when the connection is lost. Default: true.</summary>
    /// <remarks>
    /// Only a loss the caller did not ask for starts a reconnect. A caller's
    /// <see cref="IAmiConnection.DisconnectAsync"/> or <c>DisposeAsync</c> ends the connection, and reconnection
    /// with it, including a reconnect in progress: its backoff delay or connect attempt is cut short, and nothing
    /// is dialled afterwards.
    /// </remarks>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    /// <remarks>Accepted: 0 or more. Checked whether or not <see cref="AutoReconnect"/> is on.</remarks>
    [Range(0, int.MaxValue)]
    public int MaxReconnectAttempts { get; set; }

    /// <summary>Event pump buffer capacity. Default: 20,000.</summary>
    [Range(1, int.MaxValue)]
    public int EventPumpCapacity { get; set; } = Internal.AsyncEventPump.DefaultCapacity;

    /// <summary>Initial delay before the first reconnection attempt. Default: 1 second.</summary>
    /// <remarks>
    /// Accepted: from zero to <see cref="int.MaxValue"/> milliseconds (about 24.8 days, the longest delay .NET can wait).
    /// Checked only when <see cref="AutoReconnect"/> is on: by the options validator, naming this option, and by the
    /// connection's constructor, which throws <see cref="ArgumentOutOfRangeException"/>.
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

    /// <summary>Enable periodic heartbeat (Ping) to detect dead connections. Default: true.</summary>
    public bool EnableHeartbeat { get; set; } = true;

    /// <summary>Interval between heartbeat pings. Default: 30 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for a heartbeat response before considering the connection dead. Default: 10 seconds.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
