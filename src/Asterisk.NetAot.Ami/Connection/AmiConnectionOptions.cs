namespace Asterisk.NetAot.Ami.Connection;

/// <summary>
/// Configuration options for an AMI connection.
/// </summary>
public sealed class AmiConnectionOptions
{
    /// <summary>Asterisk server hostname or IP. Default: "localhost".</summary>
    public string Hostname { get; set; } = "localhost";

    /// <summary>AMI port. Default: 5038.</summary>
    public int Port { get; set; } = 5038;

    /// <summary>AMI username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>AMI password (used for MD5 challenge-response).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Enable SSL/TLS. Default: false.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Socket connection timeout. Default: 5 seconds.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Socket read idle timeout. Default: infinite (TimeSpan.Zero).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>Default timeout waiting for action responses. Default: 2 seconds.</summary>
    public TimeSpan DefaultResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Default timeout waiting for event-generating action completion. Default: 5 seconds.</summary>
    public TimeSpan DefaultEventTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Enable automatic reconnection on disconnect. Default: true.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; set; }

    /// <summary>Event pump buffer capacity. Default: 20,000.</summary>
    public int EventPumpCapacity { get; set; } = Internal.AsyncEventPump.DefaultCapacity;
}
