using System.Collections.Concurrent;
using Verbara.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Bridges;

internal static partial class BridgeManagerLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] Duplicate BridgeCreate: bridge_id={BridgeId}")]
    public static partial void DuplicateBridgeCreate(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[BRIDGE] BridgeCreate for a bridge a listing admitted: bridge_id={BridgeId}; announced as its creation, not counted again")]
    public static partial void SnapshotBridgeCreateObserved(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeEnter for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeEnter(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeLeave for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeLeave(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[BRIDGE] BridgeDestroy for unknown bridge: bridge_id={BridgeId}")]
    public static partial void UnknownBridgeDestroy(ILogger logger, string bridgeId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[BRIDGE] Bridges reconciled: listed={Listed} admitted={Admitted} removed={Removed} newer_than_snapshot={NewerThanSnapshot} destroyed_during_read={DestroyedDuringRead} removes_omitted={RemovesOmitted}")]
    public static partial void Reconciled(ILogger logger, int listed, int admitted, int removed, int newerThanSnapshot,
        int destroyedDuringRead, bool removesOmitted);
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

    // Every bridge inserted since the manager was created or last cleared, destroyed or not (BridgeCount), and the ones
    // not yet destroyed (ActiveBridgeCount).
    private int _insertedSinceClear;
    private int _active;

    // The change ordinal: every insertion, channel entry, destruction and channel leave takes the next value, so a
    // snapshot window can tell what changed after its request was sent. All manager writers hold _windowGate.
    private long _changes;

    // The snapshot windows open now (their marks), and the departures recorded for them: a bridge destroyed, or a
    // channel that left a bridge, after the oldest open window's request was sent. A snapshot read in such a window
    // may still list what departed, and must not bring it back. Lock order: _windowGate, then a bridge's SyncRoot,
    // then _destroyedOrderLock. No callback is followed by a pending membership or index publication.
    private readonly Lock _windowGate = new();
    private readonly List<long> _openMarks = [];
    private readonly Dictionary<string, long> _destroyedDuringRead = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Bridge, string Channel), long> _leftDuringRead = [];

    // How long a destroyed bridge stays reachable through GetById; it is released on the first bridge create or
    // destroy after that, with no timer. Ten minutes, mirroring SessionOptions.CompletedRetention's default; internal,
    // not a public option. Settable by tests (via InternalsVisibleTo).
    internal TimeSpan DestroyedRetention { get; set; } = TimeSpan.FromMinutes(10);

    // The clock that stamps a bridge's destruction. Settable by tests (via InternalsVisibleTo) to drive the retention
    // on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    // An inert test seam at the state changes whose subsequent publication must respect live reentry.
    internal Action<BridgeTransitionPoint, AsteriskBridge>? TransitionObserver { get; set; }

    /// <summary>
    /// Fires when a new bridge is created. Not raised for a bridge a reload only read from Asterisk's bridge listing; when
    /// Asterisk's <c>BridgeCreate</c> for such a bridge is delivered afterwards, it is raised then, once, and the bridge is
    /// not counted again.
    /// </summary>
    public event Action<AsteriskBridge>? BridgeCreated;

    /// <summary>
    /// Fires when a bridge is destroyed: on Asterisk's <c>BridgeDestroy</c>, and for a held bridge that a reload's
    /// completed bridge listing no longer reports.
    /// </summary>
    public event Action<AsteriskBridge>? BridgeDestroyed;

    /// <summary>
    /// Fires when a channel enters a bridge. Args: (bridge, channelUniqueId). Not raised for a channel a reload only read
    /// from Asterisk's channel snapshot.
    /// </summary>
    public event Action<AsteriskBridge, string>? ChannelEntered;

    /// <summary>Fires when a channel leaves a bridge. Args: (bridge, channelUniqueId).</summary>
    public event Action<AsteriskBridge, string>? ChannelLeft;

    /// <summary>Fires when a blind or attended transfer occurs.</summary>
    public event Action<BridgeTransferInfo>? TransferOccurred;

    public BridgeManager(ILogger logger) => _logger = logger;

    /// <summary>All bridges that have not been destroyed.</summary>
    public IEnumerable<AsteriskBridge> ActiveBridges => _bridges.Values.Where(b => b.DestroyedAt is null);

    /// <summary>
    /// Total number of bridges inserted since the server started (active + destroyed): each bridge Asterisk reported
    /// created, and each bridge a reload admitted from Asterisk's bridge listing that the table did not hold. A reconnect
    /// does not reset it; <see cref="Clear"/> does. A destroyed bridge stays reachable through <see cref="GetById"/> for
    /// 10 minutes after its destruction and is released on a later bridge create or destroy; it is still counted here
    /// after its release. For the bridges not yet destroyed, read <see cref="ActiveBridgeCount"/>.
    /// </summary>
    public int BridgeCount => Volatile.Read(ref _insertedSinceClear);

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
        AsteriskBridge? announced = null;
        var observedSnapshotCreation = false;
        lock (_windowGate)
        {
            if (!_bridges.TryGetValue(bridgeId, out var held))
            {
                var bridge = new AsteriskBridge
                {
                    BridgeUniqueid = bridgeId,
                    BridgeType = type,
                    Technology = technology,
                    Creator = creator,
                    Name = name,
                    InsertMark = Interlocked.Increment(ref _changes),
                };
                _bridges[bridgeId] = bridge;
                CountInsertion();
                announced = bridge;
            }
            else if (held.TryClaimObservedCreate())
            {
                // The listing already counted the insertion; this is its first observed creation.
                observedSnapshotCreation = true;
                announced = held;
            }
        }

        if (observedSnapshotCreation)
            BridgeManagerLog.SnapshotBridgeCreateObserved(_logger, bridgeId);
        if (announced is not null)
            BridgeCreated?.Invoke(announced);
        else
            BridgeManagerLog.DuplicateBridgeCreate(_logger, bridgeId);
    }

    /// <summary>Called when Asterisk fires a BridgeEnter event.</summary>
    public void OnChannelEntered(string bridgeId, string uniqueId)
    {
        AsteriskBridge? bridge;
        lock (_windowGate)
        {
            if (!_bridges.TryGetValue(bridgeId, out bridge) || bridge.DestroyedAt is not null)
            {
                BridgeManagerLog.UnknownBridgeEnter(_logger, bridgeId);
                return;
            }
            lock (bridge.SyncRoot)
            {
                bridge.Channels.TryAdd(uniqueId, 0);
                bridge.EnterMarks[uniqueId] = Interlocked.Increment(ref _changes);
                _bridgeByChannel[uniqueId] = bridge;
            }
        }

        TransitionObserver?.Invoke(BridgeTransitionPoint.LiveMembershipApplied, bridge);
        ChannelEntered?.Invoke(bridge, uniqueId);
    }

    /// <summary>Called when Asterisk fires a BridgeLeave event.</summary>
    public void OnChannelLeft(string bridgeId, string uniqueId)
    {
        AsteriskBridge? bridge;
        lock (_windowGate)
        {
            // Recorded all the same: a snapshot read now may list the channel in a bridge it admits.
            RecordLeaveLocked(bridgeId, uniqueId);
            if (!_bridges.TryGetValue(bridgeId, out bridge))
            {
                BridgeManagerLog.UnknownBridgeLeave(_logger, bridgeId);
                return;
            }
            lock (bridge.SyncRoot)
            {
                bridge.Channels.TryRemove(uniqueId, out _);
                bridge.EnterMarks.Remove(uniqueId);
                _bridgeByChannel.TryRemove(new KeyValuePair<string, AsteriskBridge>(uniqueId, bridge));
            }
        }

        ChannelLeft?.Invoke(bridge, uniqueId);
    }

    /// <summary>Called when Asterisk fires a BridgeDestroy event.</summary>
    public void OnBridgeDestroyed(string bridgeId)
    {
        AsteriskBridge? bridge;
        lock (_windowGate)
        {
            // Recorded before the lookup, under the gate an admission takes: a snapshot that still lists the bridge
            // either finds it held here (and the destruction below applies to it) or finds this record and skips it.
            RecordDestroyLocked(bridgeId);
            _bridges.TryGetValue(bridgeId, out bridge);
            if (bridge is not null && !DestroyLocked(bridgeId, bridge))
                bridge = null;
        }

        if (bridge is null)
        {
            BridgeManagerLog.UnknownBridgeDestroy(_logger, bridgeId);
            return;
        }

        AnnounceDestruction(bridge);
    }

    // Marks a held bridge destroyed: on Asterisk's BridgeDestroy, and for a bridge a completed listing omits. The first
    // destruction counts it destroyed and queues it for release; a repeated one, inside the retention, neither counts
    // nor queues again. BridgeDestroyed is raised each time, as Asterisk's event is.
    // The caller holds _windowGate. Publish lifetime, index removals, counts and retention together.
    private bool DestroyLocked(string bridgeId, AsteriskBridge bridge)
    {
        lock (bridge.SyncRoot)
        {
            if (!_bridges.TryGetValue(bridgeId, out var current) || !ReferenceEquals(current, bridge))
                return false;

            var first = bridge.DestroyedAt is null;
            var destroyedAt = TimeProvider.GetUtcNow();
            bridge.DestroyedAt = destroyedAt;
            foreach (var channelId in bridge.Channels.Keys)
                _bridgeByChannel.TryRemove(new KeyValuePair<string, AsteriskBridge>(channelId, bridge));

            if (first)
            {
                Interlocked.Decrement(ref _active);
                LiveMetrics.BridgesDestroyed.Add(1);
                // Queue before a subscriber can clear/recreate the manager. Throwing subscribers cannot pin it.
                lock (_destroyedOrderLock)
                    _destroyedOrder.Enqueue((bridgeId, bridge, destroyedAt));
            }
        }
        return true;
    }

    private void AnnounceDestruction(AsteriskBridge bridge)
    {
        try
        {
            BridgeDestroyed?.Invoke(bridge);
        }
        finally
        {
            lock (_windowGate)
                ReleaseExpired();
        }
    }

    // Counts one insertion into the table: BridgeCount, ActiveBridgeCount and live.bridges.created.
    private void CountInsertion()
    {
        Interlocked.Increment(ref _insertedSinceClear);
        Interlocked.Increment(ref _active);
        LiveMetrics.BridgesCreated.Add(1);
        ReleaseExpired();
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

    /// <summary>
    /// Opens the window a load reads its bridge snapshot in: Asterisk's channel snapshot (each channel's
    /// <c>BridgeID</c>) and its bridge listing. The caller opens it <c>before</c> it sends the first of those requests,
    /// keeps it open until <see cref="ReconcileWithSnapshot"/> has returned, and disposes it on every way out. A bridge
    /// inserted, or a channel that entered a bridge, after it opened is newer than the snapshot and is not removed by
    /// it; a bridge destroyed, or a channel that left a bridge, after it opened is not brought back by it.
    /// </summary>
    internal BridgeSnapshotWindow OpenSnapshotWindow()
    {
        lock (_windowGate)
        {
            var mark = Interlocked.Read(ref _changes);
            _openMarks.Add(mark);
            return new BridgeSnapshotWindow(this, mark);
        }
    }

    internal void CloseSnapshotWindow(BridgeSnapshotWindow window)
    {
        lock (_windowGate)
        {
            _openMarks.Remove(window.Mark);
            if (_openMarks.Count == 0)
            {
                _destroyedDuringRead.Clear();
                _leftDuringRead.Clear();
                return;
            }

            var oldest = _openMarks.Min();
            foreach (var id in _destroyedDuringRead.Where(d => d.Value <= oldest).Select(static d => d.Key).ToList())
                _destroyedDuringRead.Remove(id);
            foreach (var key in _leftDuringRead.Where(d => d.Value <= oldest).Select(static d => d.Key).ToList())
                _leftDuringRead.Remove(key);
        }
    }

    /// <summary>
    /// Reconciles the bridge table against a completed snapshot read in <paramref name="window"/>: the bridges
    /// Asterisk's bridge listing reported, and the channels its channel snapshot placed in each.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>A listed bridge the table does not hold is admitted silently: no <see cref="BridgeCreated"/>
    /// and no <see cref="ChannelEntered"/>. It is an insertion, so it advances <see cref="BridgeCount"/>,
    /// <see cref="ActiveBridgeCount"/> and <c>live.bridges.created</c>. A listed bridge already held advances
    /// nothing.</description></item>
    /// <item><description>When <paramref name="removeOmitted"/> is set, a held bridge the listing omits is removed as
    /// a destruction: <see cref="BridgeDestroyed"/> is raised and <c>live.bridges.destroyed</c> advances.</description></item>
    /// <item><description>A listed bridge's channels become the ones the snapshot placed in it, silently; a channel
    /// is taken out only when <paramref name="removeOmitted"/> is set.</description></item>
    /// <item><description>Nothing newer than the window's request is removed, and nothing that departed after it is
    /// brought back.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="window">The window opened before the snapshot's first request, still open.</param>
    /// <param name="listed">The bridges the completed listing reported.</param>
    /// <param name="members">The channel unique ids the channel snapshot placed in each bridge, by bridge id.</param>
    /// <param name="removeOmitted">
    /// Whether the snapshot is evidence of absence: <see langword="false"/> applies only the non-destructive half, for an
    /// answer whose completion cannot be told.
    /// </param>
    /// <exception cref="ObjectDisposedException"><paramref name="window"/> is already closed.</exception>
    internal void ReconcileWithSnapshot(BridgeSnapshotWindow window, IReadOnlyCollection<BridgeSnapshotEntry> listed,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> members, bool removeOmitted)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(window.IsClosed, window);

        var listedIds = new HashSet<string>(listed.Select(static b => b.Id), StringComparer.Ordinal);
        var removed = 0;
        var newer = 0;
        if (removeOmitted)
        {
            foreach (var held in _bridges.Values.Where(b => b.DestroyedAt is null && !listedIds.Contains(b.BridgeUniqueid)))
            {
                var destroyed = false;
                lock (_windowGate)
                {
                    ObjectDisposedException.ThrowIf(window.IsClosed, window);
                    if (!_bridges.TryGetValue(held.BridgeUniqueid, out var current)
                        || !ReferenceEquals(current, held) || held.DestroyedAt is not null)
                        continue;
                    if (held.InsertMark > window.Mark)
                    {
                        newer++;
                        continue;
                    }
                    RecordDestroyLocked(held.BridgeUniqueid);
                    destroyed = DestroyLocked(held.BridgeUniqueid, held);
                }

                if (destroyed)
                {
                    removed++;
                    AnnounceDestruction(held);
                }
            }
        }

        var admitted = 0;
        var destroyedDuringRead = 0;
        foreach (var entry in listed)
        {
            AsteriskBridge? bridge;
            bool inserted;
            lock (_windowGate)
            {
                ObjectDisposedException.ThrowIf(window.IsClosed, window);
                if (_destroyedDuringRead.TryGetValue(entry.Id, out var stamp) && stamp > window.Mark)
                {
                    destroyedDuringRead++;
                    continue;
                }

                if (_bridges.TryGetValue(entry.Id, out bridge))
                {
                    inserted = false;
                }
                else
                {
                    bridge = new AsteriskBridge
                    {
                        BridgeUniqueid = entry.Id,
                        BridgeType = entry.Type,
                        Technology = entry.Technology,
                        Creator = entry.Creator,
                        Name = entry.Name,
                        InsertMark = Interlocked.Increment(ref _changes),
                        AdmittedBySnapshot = true,
                    };
                    inserted = _bridges.TryAdd(entry.Id, bridge);
                    if (!inserted)
                        bridge = _bridges.GetValueOrDefault(entry.Id);
                    else
                    {
                        // A destroy must never see the new lifetime before its insertion has been counted.
                        CountInsertion();
                        admitted++;
                    }
                }
            }

            if (bridge is null)
                continue;

            // A bridge held but destroyed (still inside its retention) is not brought back.
            if (bridge.DestroyedAt is not null)
                continue;

            if (inserted)
                TransitionObserver?.Invoke(BridgeTransitionPoint.SnapshotAdmitted, bridge);

            ReconcileMembers(bridge, members.GetValueOrDefault(entry.Id) ?? [], window, removeOmitted);
        }

        BridgeManagerLog.Reconciled(_logger, listed.Count, admitted, removed, newer, destroyedDuringRead, removeOmitted);
    }

    // Makes a listed bridge's channels the ones the snapshot placed in it, raising nothing.
    private void ReconcileMembers(AsteriskBridge bridge, IReadOnlyCollection<string> listedMembers,
        BridgeSnapshotWindow window, bool removeOmitted)
    {
        var listed = listedMembers as IReadOnlySet<string> ?? new HashSet<string>(listedMembers, StringComparer.Ordinal);
        lock (_windowGate)
        {
            ObjectDisposedException.ThrowIf(window.IsClosed, window);
            if (!_bridges.TryGetValue(bridge.BridgeUniqueid, out var current)
                || !ReferenceEquals(current, bridge) || bridge.DestroyedAt is not null)
                return;

            lock (bridge.SyncRoot)
            {
                foreach (var uniqueId in listed)
                {
                    if (HasNewerOwnerLocked(uniqueId, bridge, window))
                    {
                        // A move's BridgeEnter can arrive before BridgeLeave for this old membership.
                        bridge.Channels.TryRemove(uniqueId, out _);
                        bridge.EnterMarks.Remove(uniqueId);
                        _bridgeByChannel.TryRemove(new KeyValuePair<string, AsteriskBridge>(uniqueId, bridge));
                        continue;
                    }
                    if (_leftDuringRead.TryGetValue((bridge.BridgeUniqueid, uniqueId), out var stamp)
                        && stamp > window.Mark && !bridge.Channels.ContainsKey(uniqueId))
                        continue;

                    if (bridge.Channels.TryAdd(uniqueId, 0))
                        bridge.EnterMarks[uniqueId] = Interlocked.Increment(ref _changes);
                    // A held member can still need its lookup repaired after an older live move. Preserve its mark.
                    _bridgeByChannel[uniqueId] = bridge;
                }

                if (removeOmitted)
                {
                    // Not listed, and not entered after this listing's read opened.
                    foreach (var uniqueId in bridge.Channels.Keys.Where(id => !listed.Contains(id)
                                 && !(bridge.EnterMarks.TryGetValue(id, out var entered) && entered > window.Mark)))
                    {
                        bridge.Channels.TryRemove(uniqueId, out _);
                        bridge.EnterMarks.Remove(uniqueId);
                        _bridgeByChannel.TryRemove(new KeyValuePair<string, AsteriskBridge>(uniqueId, bridge));
                    }
                }
            }
        }

        TransitionObserver?.Invoke(BridgeTransitionPoint.SnapshotMembershipApplied, bridge);
    }

    // All EnterMarks writers hold _windowGate, including for a different bridge. No second bridge lock is needed
    // to inspect the owner under that gate, and no cross-bridge lock acquisition can invert the order.
    private bool HasNewerOwnerLocked(string uniqueId, AsteriskBridge bridge, BridgeSnapshotWindow window) =>
        _bridgeByChannel.TryGetValue(uniqueId, out var owner) && !ReferenceEquals(owner, bridge)
        && owner.DestroyedAt is null
        && _bridges.TryGetValue(owner.BridgeUniqueid, out var current) && ReferenceEquals(current, owner)
        && owner.EnterMarks.TryGetValue(uniqueId, out var entered) && entered > window.Mark;

    // Records a channel's leave for the open windows. The caller holds _windowGate.
    private void RecordLeaveLocked(string bridgeId, string uniqueId)
    {
        if (_openMarks.Count > 0)
            _leftDuringRead[(bridgeId, uniqueId)] = Interlocked.Increment(ref _changes);
    }

    // Records a bridge's destruction for the open windows. The caller holds _windowGate.
    private void RecordDestroyLocked(string bridgeId)
    {
        if (_openMarks.Count > 0)
            _destroyedDuringRead[bridgeId] = Interlocked.Increment(ref _changes);
    }

    /// <summary>
    /// Clears all bridge and channel state and resets both counts. A reload no longer calls it: it reconciles the table
    /// against Asterisk's bridge listing instead.
    /// </summary>
    public void Clear()
    {
        lock (_windowGate)
        {
            _bridges.Clear();
            _bridgeByChannel.Clear();
            lock (_destroyedOrderLock)
                _destroyedOrder.Clear();
            _destroyedDuringRead.Clear();
            _leftDuringRead.Clear();
            Interlocked.Exchange(ref _insertedSinceClear, 0);
            Interlocked.Exchange(ref _active, 0);
        }
    }
}

internal enum BridgeTransitionPoint
{
    SnapshotAdmitted,
    SnapshotMembershipApplied,
    LiveMembershipApplied,
}

/// <summary>A bridge as Asterisk's bridge listing (<c>BridgeListItem</c>) reports it.</summary>
internal sealed record BridgeSnapshotEntry(string Id, string? Type, string? Technology, string? Creator, string? Name);

/// <summary>
/// The span of one load's bridge snapshot: opened by <see cref="BridgeManager.OpenSnapshotWindow"/> before the
/// snapshot's first request, handed to <see cref="BridgeManager.ReconcileWithSnapshot"/>, and disposed once that has
/// returned or the read ended without it.
/// </summary>
internal sealed class BridgeSnapshotWindow : IDisposable
{
    private readonly BridgeManager _owner;
    private int _closed;

    internal BridgeSnapshotWindow(BridgeManager owner, long mark)
    {
        _owner = owner;
        Mark = mark;
    }

    /// <summary>The manager's change ordinal when the window opened, before the snapshot's first request.</summary>
    internal long Mark { get; }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.CloseSnapshotWindow(this);
    }
}
