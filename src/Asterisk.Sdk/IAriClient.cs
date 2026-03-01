namespace Asterisk.Sdk;

/// <summary>
/// Represents an async client for the Asterisk REST Interface (ARI).
/// </summary>
public interface IAriClient : IAsyncDisposable
{
    /// <summary>Whether the WebSocket event connection is active.</summary>
    bool IsConnected { get; }

    /// <summary>Connect to the ARI WebSocket event stream.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnect from the ARI WebSocket event stream.</summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribe to ARI events via IObservable.</summary>
    IDisposable Subscribe(IObserver<AriEvent> observer);

    /// <summary>Access channel operations.</summary>
    IAriChannelsResource Channels { get; }

    /// <summary>Access bridge operations.</summary>
    IAriBridgesResource Bridges { get; }
}

/// <summary>
/// Base class for all ARI events received via WebSocket.
/// </summary>
public class AriEvent
{
    public string? Type { get; set; }
    public string? Application { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string? RawJson { get; set; }
}

/// <summary>
/// ARI channel operations.
/// </summary>
public interface IAriChannelsResource
{
    ValueTask<AriChannel> CreateAsync(string endpoint, string? app = null, CancellationToken cancellationToken = default);
    ValueTask<AriChannel> GetAsync(string channelId, CancellationToken cancellationToken = default);
    ValueTask HangupAsync(string channelId, CancellationToken cancellationToken = default);
    ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// ARI bridge operations.
/// </summary>
public interface IAriBridgesResource
{
    ValueTask<AriBridge> CreateAsync(string? type = null, string? name = null, CancellationToken cancellationToken = default);
    ValueTask<AriBridge> GetAsync(string bridgeId, CancellationToken cancellationToken = default);
    ValueTask DestroyAsync(string bridgeId, CancellationToken cancellationToken = default);
    ValueTask AddChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default);
    ValueTask RemoveChannelAsync(string bridgeId, string channelId, CancellationToken cancellationToken = default);
}

/// <summary>ARI channel state.</summary>
public enum AriChannelState
{
    Down,
    Rsrvd,
    OffHook,
    Dialing,
    Ring,
    Ringing,
    Up,
    Busy,
    DialingOffhook,
    PreRing,
    Unknown
}

/// <summary>ARI channel model.</summary>
public sealed class AriChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AriChannelState State { get; set; } = AriChannelState.Unknown;
}

/// <summary>ARI bridge model.</summary>
public sealed class AriBridge
{
    public string Id { get; set; } = string.Empty;
    public string Technology { get; set; } = string.Empty;
    public string BridgeType { get; set; } = string.Empty;
    public IReadOnlyList<string> Channels { get; set; } = [];
}
