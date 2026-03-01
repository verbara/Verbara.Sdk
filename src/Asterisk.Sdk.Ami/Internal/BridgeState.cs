using System.Collections.Concurrent;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Tracks the state of active bridges in real-time.
/// Updated from BridgeCreate, BridgeEnter, BridgeLeave, BridgeDestroy events.
/// </summary>
public sealed class ActiveBridgeTracker
{
    private readonly ConcurrentDictionary<string, BridgeInfo> _bridges = new();

    public IReadOnlyCollection<BridgeInfo> ActiveBridges => _bridges.Values.ToList().AsReadOnly();

    public BridgeInfo? GetBridge(string bridgeUniqueId) =>
        _bridges.GetValueOrDefault(bridgeUniqueId);

    public void OnBridgeCreated(string bridgeUniqueId, string? bridgeType, string? bridgeTechnology)
    {
        _bridges[bridgeUniqueId] = new BridgeInfo
        {
            BridgeUniqueId = bridgeUniqueId,
            BridgeType = bridgeType,
            BridgeTechnology = bridgeTechnology
        };
    }

    public void OnChannelEntered(string bridgeUniqueId, string channel)
    {
        if (_bridges.TryGetValue(bridgeUniqueId, out var bridge))
        {
            bridge.Channels.Add(channel);
        }
    }

    public void OnChannelLeft(string bridgeUniqueId, string channel)
    {
        if (_bridges.TryGetValue(bridgeUniqueId, out var bridge))
        {
            bridge.Channels.Remove(channel);
        }
    }

    public void OnBridgeDestroyed(string bridgeUniqueId)
    {
        _bridges.TryRemove(bridgeUniqueId, out _);
    }

    public void Clear() => _bridges.Clear();
}

/// <summary>
/// Information about an active bridge.
/// </summary>
public sealed class BridgeInfo
{
    public string BridgeUniqueId { get; init; } = string.Empty;
    public string? BridgeType { get; init; }
    public string? BridgeTechnology { get; init; }
    public HashSet<string> Channels { get; } = [];
}
