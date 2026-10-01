using System.Collections.Concurrent;
using Verbara.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Bridges;

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

    // Destroyed bridges in the order they were destroyed, each with its own destruction time. Release reads only the
    // entry's timestamp, so no lookup can fail and stall the head. A plain Queue under a lock keeps peek-then-dequeue
    // one atomic decision.
    private readonly Queue<(string Id, AsteriskBridge Bridge, DateTimeOffset DestroyedAt)> _destroyedOrder = new();
    private readonly Lock _destroyedOrderLock = new();

    // Every bridge created since the last Clear(), destroyed or not (BridgeCount), and the ones not yet destroyed
    // (ActiveBridgeCount).
    private int _createdSinceClear;
    private int _active;

    // How long a destroyed bridge stays reachable through GetById; it is released on the first bridge create or
    // destroy after that, with no timer. Ten minutes, mirroring SessionOptions.CompletedRetention's default; internal,
    // not a public option. Settable by tests (via InternalsVisibleTo).
    internal TimeSpan DestroyedRetention { get; set; } = TimeSpan.FromMinutes(10);

    // The clock that stamps a bridge's destruction. Settable by tests (via InternalsVisibleTo) to drive the retention
    // on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

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

    /// <summary>
    /// Total number of bridges created since the last <see cref="Clear"/> (active + destroyed). A destroyed bridge
    /// stays reachable through <see cref="GetById"/> for 10 minutes after its destruction and is released on a later
    /// bridge create or destroy; it is still counted here after its release. For the bridges not yet destroyed, read
    /// <see cref="ActiveBridgeCount"/>.
    /// </summary>
    public int BridgeCount => Volatile.Read(ref _createdSinceClear);

    /// <summary>Number of bridges that have not been destroyed — the size of <see cref="ActiveBridges"/>, read in O(1).</summary>
    public int ActiveBridgeCount => Volatile.Read(ref _active);

    /// <summary>
    /// Returns the bridge with the given ID, or <c>null</c> if not found. A destroyed bridge is found for 10 minutes
    /// after its destruction, until a later bridge create or destroy releases it.
    /// </summary>
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
            Interlocked.Increment(ref _createdSinceClear);
            Interlocked.Increment(ref _active);
            LiveMetrics.BridgesCreated.Add(1);
            ReleaseExpired();
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

        DateTimeOffset destroyedAt;
        bool first;
        lock (bridge.SyncRoot)
        {
            // A repeated BridgeDestroy inside the retention neither decrements ActiveBridgeCount nor queues again.
            first = bridge.DestroyedAt is null;
            destroyedAt = TimeProvider.GetUtcNow();
            bridge.DestroyedAt = destroyedAt;
            foreach (var channelId in bridge.Channels.Keys)
                _bridgeByChannel.TryRemove(channelId, out _);
        }

        if (first)
            Interlocked.Decrement(ref _active);
        LiveMetrics.BridgesDestroyed.Add(1);
        try
        {
            BridgeDestroyed?.Invoke(bridge);
        }
        finally
        {
            // In a finally: the event pump swallows a subscriber's exception, and a throwing subscriber must not keep
            // the bridge held.
            if (first)
            {
                lock (_destroyedOrderLock)
                    _destroyedOrder.Enqueue((bridgeId, bridge, destroyedAt));
            }

            ReleaseExpired();
        }
    }

    // Releases every destroyed bridge whose own destruction time is at least DestroyedRetention ago. An entry is removed
    // from _bridges only if its id still maps to the same bridge object.
    private void ReleaseExpired()
    {
        var cutoff = TimeProvider.GetUtcNow() - DestroyedRetention;
        lock (_destroyedOrderLock)
        {
            while (_destroyedOrder.TryPeek(out var head) && head.DestroyedAt <= cutoff)
            {
                _destroyedOrder.Dequeue();
                _bridges.TryRemove(new KeyValuePair<string, AsteriskBridge>(head.Id, head.Bridge));
            }
        }
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

    /// <summary>Clears all bridge and channel state and resets both counts (used on reconnect).</summary>
    public void Clear()
    {
        _bridges.Clear();
        _bridgeByChannel.Clear();
        lock (_destroyedOrderLock)
            _destroyedOrder.Clear();
        Interlocked.Exchange(ref _createdSinceClear, 0);
        Interlocked.Exchange(ref _active, 0);
    }
}
