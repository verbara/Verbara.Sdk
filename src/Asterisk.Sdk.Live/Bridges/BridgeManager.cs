using System.Collections.Concurrent;
using Asterisk.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Bridges;

internal static partial class BridgeManagerLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] Duplicate BridgeCreate: bridge_id={BridgeId}")]
    public static partial void DuplicateBridgeCreate(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeEnter for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeEnter(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeLeave for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeLeave(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeDestroy for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeDestroy(ILogger logger, string bridgeId);
}

/// <summary>
/// Tracks Asterisk bridge lifecycle and channel membership in real time.
/// Maintains a reverse index from channel unique ID to its current bridge for O(1) lookup.
/// </summary>
public sealed class BridgeManager
{
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridges = new();
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridgeByChannel = new();
    private readonly ILogger _logger;

    /// <summary>Fires when a new bridge is created.</summary>
    public event Action<AsteriskBridge>? BridgeCreated;

    /// <summary>Fires when a bridge is destroyed.</summary>
    public event Action<AsteriskBridge>? BridgeDestroyed;

    /// <summary>Fires when a channel enters a bridge. Args: (bridge, channelUniqueId).</summary>
    public event Action<AsteriskBridge, string>? ChannelEntered;

    /// <summary>Fires when a channel leaves a bridge. Args: (bridge, channelUniqueId).</summary>
    public event Action<AsteriskBridge, string>? ChannelLeft;

    /// <summary>Fires when a blind or attended transfer occurs.</summary>
    public event Action<BridgeTransferInfo>? TransferOccurred;

    public BridgeManager(ILogger logger) => _logger = logger;

    /// <summary>All bridges that have not been destroyed.</summary>
    public IEnumerable<AsteriskBridge> ActiveBridges => _bridges.Values.Where(b => b.DestroyedAt is null);

    /// <summary>Total number of bridges (active + destroyed).</summary>
    public int BridgeCount => _bridges.Count;

    /// <summary>Returns the bridge with the given ID, or <c>null</c> if not found.</summary>
    public AsteriskBridge? GetById(string bridgeId) =>
        _bridges.GetValueOrDefault(bridgeId);

    /// <summary>Returns the bridge that currently contains <paramref name="uniqueId"/>, or <c>null</c>.</summary>
    public AsteriskBridge? GetBridgeForChannel(string uniqueId) =>
        _bridgeByChannel.GetValueOrDefault(uniqueId);

    /// <summary>Called when Asterisk fires a BridgeCreate event.</summary>
    public void OnBridgeCreated(string bridgeId, string? type, string? technology, string? creator, string? name)
    {
        var bridge = new AsteriskBridge
        {
            BridgeUniqueid = bridgeId,
            BridgeType = type,
            Technology = technology,
            Creator = creator,
            Name = name
        };

        if (_bridges.TryAdd(bridgeId, bridge))
        {
            LiveMetrics.BridgesCreated.Add(1);
            BridgeCreated?.Invoke(bridge);
        }
        else
        {
            BridgeManagerLog.DuplicateBridgeCreate(_logger, bridgeId);
        }
    }

    /// <summary>Called when Asterisk fires a BridgeEnter event.</summary>
    public void OnChannelEntered(string bridgeId, string uniqueId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge))
        {
            BridgeManagerLog.UnknownBridgeEnter(_logger, bridgeId);
            return;
        }

        lock (bridge.SyncRoot)
        {
            bridge.Channels.TryAdd(uniqueId, 0);
        }

        _bridgeByChannel[uniqueId] = bridge;
        ChannelEntered?.Invoke(bridge, uniqueId);
    }

    /// <summary>Called when Asterisk fires a BridgeLeave event.</summary>
    public void OnChannelLeft(string bridgeId, string uniqueId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge))
        {
            BridgeManagerLog.UnknownBridgeLeave(_logger, bridgeId);
            return;
        }

        lock (bridge.SyncRoot)
        {
            bridge.Channels.TryRemove(uniqueId, out _);
        }

        _bridgeByChannel.TryRemove(uniqueId, out _);
        ChannelLeft?.Invoke(bridge, uniqueId);
    }

    /// <summary>Called when Asterisk fires a BridgeDestroy event.</summary>
    public void OnBridgeDestroyed(string bridgeId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge))
        {
            BridgeManagerLog.UnknownBridgeDestroy(_logger, bridgeId);
            return;
        }

        lock (bridge.SyncRoot)
        {
            bridge.DestroyedAt = DateTimeOffset.UtcNow;
            foreach (var channelId in bridge.Channels.Keys)
                _bridgeByChannel.TryRemove(channelId, out _);
        }

        LiveMetrics.BridgesDestroyed.Add(1);
        BridgeDestroyed?.Invoke(bridge);
    }

    /// <summary>Called when Asterisk fires a BlindTransfer event.</summary>
    public void OnBlindTransfer(string bridgeId, string? targetChannel, string? extension, string? context)
    {
        var info = new BridgeTransferInfo(bridgeId, "Blind", targetChannel, null, null, null);
        TransferOccurred?.Invoke(info);
    }

    /// <summary>Called when Asterisk fires an AttendedTransfer event.</summary>
    public void OnAttendedTransfer(string origBridgeId, string? secondBridgeId, string? destType, string? result)
    {
        var info = new BridgeTransferInfo(origBridgeId, "Attended", null, secondBridgeId, destType, result);
        TransferOccurred?.Invoke(info);
    }

    /// <summary>Clears all bridge and channel state (used on reconnect).</summary>
    public void Clear()
    {
        _bridges.Clear();
        _bridgeByChannel.Clear();
    }
}
