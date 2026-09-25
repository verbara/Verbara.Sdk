using System.Collections.Concurrent;
using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Channels;

internal static partial class ChannelManagerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] New: unique_id={UniqueId} name={ChannelName} state={State}")]
    public static partial void NewChannel(ILogger logger, string uniqueId, string channelName, ChannelState state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] State changed: unique_id={UniqueId} state={NewState}")]
    public static partial void StateChanged(ILogger logger, string uniqueId, ChannelState newState);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Hangup: unique_id={UniqueId} cause={Cause}")]
    public static partial void Hangup(ILogger logger, string uniqueId, HangupCause cause);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Renamed: unique_id={UniqueId} new_name={NewName}")]
    public static partial void Renamed(ILogger logger, string uniqueId, string newName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Linked: unique_id_1={UniqueId1} unique_id_2={UniqueId2}")]
    public static partial void Linked(ILogger logger, string uniqueId1, string uniqueId2);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Unlinked: unique_id_1={UniqueId1} unique_id_2={UniqueId2}")]
    public static partial void Unlinked(ILogger logger, string uniqueId1, string uniqueId2);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Removed by reload: unique_id={UniqueId} name={ChannelName}")]
    public static partial void RemovedByReload(ILogger logger, string uniqueId, string channelName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Reconciled: snapshot={SnapshotCount} added={Added} removed={Removed} newer_than_snapshot={NewerThanSnapshot}")]
    public static partial void Reconciled(ILogger logger, int snapshotCount, int added, int removed, int newerThanSnapshot);
}

/// <summary>
/// Tracks all active Asterisk channels in real-time from AMI events.
/// Uses dual indices (UniqueId + Name) for O(1) lookups.
/// All state mutations are protected by per-entity locks for atomic updates.
/// </summary>
public sealed class ChannelManager
{
    private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByUniqueId = new();
    private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByName = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Counts admissions. Monotonic, never reset — not even by <see cref="Clear"/> — because its
    /// only job is to order one admission against another (ADR-0062, design D6).
    /// </summary>
    private long _admissions;

    public event Action<AsteriskChannel>? ChannelAdded;
    public event Action<AsteriskChannel>? ChannelRemoved;
    public event Action<AsteriskChannel>? ChannelStateChanged;

    public ChannelManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskChannel> ActiveChannels => _channelsByUniqueId.Values;

    public int ChannelCount => _channelsByUniqueId.Count;

    public AsteriskChannel? GetByUniqueId(string uniqueId) =>
        _channelsByUniqueId.GetValueOrDefault(uniqueId);

    /// <summary>O(1) lookup by channel name via secondary index.</summary>
    public AsteriskChannel? GetByName(string name) =>
        _channelsByName.GetValueOrDefault(name);

    /// <summary>
    /// Read the admission mark a reload's snapshot is about to be taken at. Every channel admitted
    /// from now on carries a stamp greater than the returned value, so
    /// <see cref="ReconcileWithSnapshot"/> can tell "the snapshot omits it" from "the snapshot is
    /// older than it" (ADR-0062, design D6).
    /// <para>
    /// The caller MUST read this <c>before</c> it issues the request whose answer it will
    /// reconcile. Reading it afterwards would place every channel that arrived during the read at
    /// or below the mark and hand the reconciliation the power to end a call that is up.
    /// </para>
    /// </summary>
    internal long CaptureAdmissionMark() => Interlocked.Read(ref _admissions);

    /// <summary>Handle a NewChannel event.</summary>
    public void OnNewChannel(string uniqueId, string channelName, ChannelState state,
        string? callerIdNum = null, string? callerIdName = null,
        string? context = null, string? exten = null, int priority = 1,
        string? linkedId = null) =>
        Admit(uniqueId, channelName, state, callerIdNum, callerIdName, context, exten, priority,
            linkedId, fromSnapshot: false);

    /// <summary>
    /// The one place a channel enters the table. <see cref="OnNewChannel"/> is the live
    /// <c>NewChannel</c> route into it and <see cref="ReconcileWithSnapshot"/> the reload's; the
    /// only difference between them is <paramref name="fromSnapshot"/>, which becomes
    /// <see cref="AsteriskChannel.AdmittedFromSnapshot"/>.
    /// <para>
    /// Private, and a separate method rather than an extra parameter on <see cref="OnNewChannel"/>,
    /// because <see cref="OnNewChannel"/> is public API that two downstream repos consume: adding
    /// even an optional parameter to it would change its signature and move
    /// <c>PublicAPI.Shipped.txt</c>, which this change does not do (ADR-0062).
    /// </para>
    /// </summary>
    private void Admit(string uniqueId, string channelName, ChannelState state,
        string? callerIdNum, string? callerIdName,
        string? context, string? exten, int priority,
        string? linkedId, bool fromSnapshot)
    {
        // Stamped before the channel is published to either index, so a channel visible to a
        // concurrent reconciliation always carries the mark that ordered it. Channels arrive on the
        // AMI observer thread while a reload reads its snapshot, so the counter is interlocked.
        var admissionMark = Interlocked.Increment(ref _admissions);

        var channel = new AsteriskChannel
        {
            UniqueId = uniqueId,
            Name = channelName,
            State = state,
            CallerIdNum = callerIdNum,
            CallerIdName = callerIdName,
            Context = context,
            Extension = exten,
            Priority = priority,
            LinkedId = linkedId,
            AdmissionMark = admissionMark,
            AdmittedFromSnapshot = fromSnapshot
        };

        _channelsByUniqueId[uniqueId] = channel;
        _channelsByName[channelName] = channel;
        LiveMetrics.ChannelsCreated.Add(1);
        ChannelManagerLog.NewChannel(_logger, uniqueId, channelName, state);
        ChannelAdded?.Invoke(channel);
    }

    /// <summary>Handle a NewState event (channel state changed).</summary>
    public void OnNewState(string uniqueId, ChannelState newState, string? channelName = null)
    {
        if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
        {
            lock (channel.SyncRoot)
            {
                channel.State = newState;
                if (channelName is not null)
                {
                    _channelsByName.TryRemove(channel.Name, out _);
                    channel.Name = channelName;
                    _channelsByName[channelName] = channel;
                }
            }
            ChannelManagerLog.StateChanged(_logger, uniqueId, newState);
            ChannelStateChanged?.Invoke(channel);
        }
    }

    /// <summary>Handle a Hangup event.</summary>
    public void OnHangup(string uniqueId, HangupCause cause = HangupCause.NormalClearing)
    {
        if (_channelsByUniqueId.TryRemove(uniqueId, out var channel))
        {
            _channelsByName.TryRemove(channel.Name, out _);
            lock (channel.SyncRoot)
            {
                channel.HangupCause = cause;
                channel.State = ChannelState.Down;
            }
            LiveMetrics.ChannelsDestroyed.Add(1);
            ChannelManagerLog.Hangup(_logger, uniqueId, cause);
            ChannelRemoved?.Invoke(channel);
        }
    }

    /// <summary>Handle a Rename event.</summary>
    public void OnRename(string uniqueId, string newName)
    {
        if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
        {
            lock (channel.SyncRoot)
            {
                _channelsByName.TryRemove(channel.Name, out _);
                channel.Name = newName;
                _channelsByName[newName] = channel;
            }
            ChannelManagerLog.Renamed(_logger, uniqueId, newName);
        }
    }

    /// <summary>Handle channel link (bridge).</summary>
    public void OnLink(string uniqueId1, string uniqueId2)
    {
        var ch1 = GetByUniqueId(uniqueId1);
        var ch2 = GetByUniqueId(uniqueId2);
        if (ch1 is not null && ch2 is not null)
        {
            ch1.LinkedChannel = ch2;
            ch2.LinkedChannel = ch1;
            ChannelManagerLog.Linked(_logger, uniqueId1, uniqueId2);
        }
    }

    /// <summary>Handle channel unlink.</summary>
    public void OnUnlink(string uniqueId1, string uniqueId2)
    {
        var ch1 = GetByUniqueId(uniqueId1);
        var ch2 = GetByUniqueId(uniqueId2);
        if (ch1 is not null) ch1.LinkedChannel = null;
        if (ch2 is not null) ch2.LinkedChannel = null;
        ChannelManagerLog.Unlinked(_logger, uniqueId1, uniqueId2);
    }

    public event Action<AsteriskChannel>? ChannelDialBegin;
    public event Action<AsteriskChannel>? ChannelDialEnd;
    public event Action<AsteriskChannel>? ChannelHeld;
    public event Action<AsteriskChannel>? ChannelUnheld;

    public void OnDialBegin(string uniqueId, string destUniqueId, string destChannel, string? dialString)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.DialedChannel = destChannel;
        }
        ChannelDialBegin?.Invoke(channel);
    }

    public void OnDialEnd(string uniqueId, string? dialStatus)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.DialStatus = dialStatus;
        }
        ChannelDialEnd?.Invoke(channel);
    }

    public void OnHold(string uniqueId, string? musicClass)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.IsOnHold = true;
            channel.HoldMusicClass = musicClass;
        }
        ChannelHeld?.Invoke(channel);
    }

    public void OnUnhold(string uniqueId)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.IsOnHold = false;
            channel.HoldMusicClass = null;
        }
        ChannelUnheld?.Invoke(channel);
    }

    /// <summary>Get channels filtered by state (lazy, zero-alloc).</summary>
    public IEnumerable<AsteriskChannel> GetChannelsByState(ChannelState state) =>
        _channelsByUniqueId.Values.Where(c => c.State == state);

    /// <summary>
    /// Get channels filtered by technology prefix (lazy; no collection is materialized).
    /// Example: "WebSocket", "PJSIP", "AudioSocket".
    /// </summary>
    public IEnumerable<AsteriskChannel> GetChannelsByTechnology(string technology)
    {
        var prefix = string.Concat(technology, "/");
        return _channelsByName
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(static kvp => kvp.Value);
    }

    /// <summary>Count channels by technology without materializing a collection.</summary>
    public int CountChannelsByTechnology(string technology)
    {
        var prefix = string.Concat(technology, "/");
        return _channelsByName.Count(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reconcile the tracked channel table against a <c>complete</c> reload snapshot: raise
    /// <see cref="ChannelAdded"/> for every snapshot entry not already held, and
    /// <see cref="ChannelRemoved"/> for every held channel the snapshot does not contain.
    /// <para>
    /// A channel the snapshot still contains is kept exactly as it is — the same instance, so its
    /// <see cref="AsteriskChannel.LinkedId"/>, <see cref="AsteriskChannel.LinkedChannel"/>,
    /// <see cref="AsteriskChannel.IsOnHold"/>, <see cref="AsteriskChannel.DialedChannel"/>,
    /// <see cref="AsteriskChannel.ExtensionHistory"/> and <see cref="AsteriskChannel.CreatedAt"/>
    /// all survive — and no event is raised for it. An identical snapshot therefore raises nothing
    /// at all.
    /// </para>
    /// <para>
    /// A held channel's <see cref="AsteriskChannel.State"/> is <b>not</b> refreshed from the
    /// snapshot, and that is a decision rather than an omission. It was first taken because the
    /// snapshot's state was always <see cref="ChannelState.Unknown"/> — the reload read a header no
    /// Asterisk version sends — and refreshing would have overwritten a genuine <c>Up</c> with it.
    /// ADR-0062 design D5 fixed the header, and the decision was re-taken on the same evidence that
    /// produced D6: the snapshot is older than the events that arrived while it was being read, and
    /// the admission mark orders <em>admissions</em> only, so nothing here can tell a snapshot state
    /// from a <c>NewState</c> that overtook it. Refreshing would hand the reload the power to push a
    /// call that answered during the read back to <c>Ringing</c> — the state defect D6 forbids for
    /// existence, applied to state. The price is stated plainly: a call whose state changed while
    /// the link was down keeps the last state the SDK observed live, and the reload never corrects
    /// it. That is stale, but it is always a state Asterisk really reported, never an invented one.
    /// Refreshing safely needs a per-channel mutation mark, which is a design extension and not this
    /// method's to take.
    /// </para>
    /// <para>
    /// The parameter is an <see cref="IReadOnlyCollection{T}"/> rather than an
    /// <see cref="IEnumerable{T}"/> on purpose: only a snapshot that was read to completion may be
    /// reconciled. Absence from an unfinished snapshot is not evidence that a channel is gone, and
    /// a caller streaming a lazy sequence in here could end a live call by mistake. Buffering the
    /// snapshot is the caller's job.
    /// </para>
    /// <para>
    /// A channel removed here left because the snapshot proved Asterisk no longer has it, not
    /// because a <c>Hangup</c> was observed. It therefore carries
    /// <see cref="AsteriskChannel.RemovedByReload"/> and is given no hangup cause — see
    /// <c>ADR-0062</c>, design D2 and D3.
    /// </para>
    /// <para>
    /// Absence from the snapshot is evidence only about channels the snapshot could have contained.
    /// Live events resume before a reload completes, so a call that starts while the snapshot is
    /// being read is admitted to this table and is legitimately missing from it; the snapshot is
    /// older than that channel and says nothing about it. <paramref name="admittedThrough"/> is
    /// where that line is drawn — see <see cref="CaptureAdmissionMark"/> (ADR-0062, design D6).
    /// </para>
    /// </summary>
    /// <param name="snapshot">Every channel the reload reported, already materialized.</param>
    /// <param name="admittedThrough">
    /// The admission mark read from <see cref="CaptureAdmissionMark"/> <c>before</c> the snapshot
    /// was requested. A held channel stamped above it is skipped, not removed. There is deliberately
    /// no default: a caller that cannot say when its snapshot was taken cannot be allowed to end
    /// calls with it.
    /// </param>
    internal void ReconcileWithSnapshot(
        IReadOnlyCollection<ChannelSnapshotEntry> snapshot, long admittedThrough)
    {
        var present = new HashSet<string>(snapshot.Count, StringComparer.Ordinal);
        foreach (var entry in snapshot)
            present.Add(entry.UniqueId);

        // Removals first, so the difference is computed against the table as it was held rather
        // than against a table already carrying the snapshot's admissions. The name index is
        // protected independently, by the instance-identity check in RemoveByReload: measured
        // 2026-09-25, either mechanism alone keeps a reused name pointing at the right channel, and
        // only removing both turns the two name-index tests red.
        // ConcurrentDictionary.Values hands back a snapshot, so removing inside the loop is safe.
        var removed = 0;
        var newerThanSnapshot = 0;
        foreach (var held in _channelsByUniqueId.Values)
        {
            if (present.Contains(held.UniqueId))
                continue;

            // The snapshot was requested before this channel was admitted, so it could not have
            // reported it and its silence is not evidence. Removing it here would end a call that
            // is up — the worst outcome this reconciliation can produce, and worse than the ghost
            // sessions it exists to remove (ADR-0062, design D6).
            if (held.AdmissionMark > admittedThrough)
            {
                newerThanSnapshot++;
                continue;
            }

            if (RemoveByReload(held))
                removed++;
        }

        var added = 0;
        foreach (var entry in snapshot)
        {
            if (_channelsByUniqueId.ContainsKey(entry.UniqueId))
                continue;

            // fromSnapshot: the SDK never saw this channel start. A subscriber that opens a record
            // for it must be able to tell that from a live NewChannel, because the state the
            // snapshot reports is the only history it will ever get (ADR-0062, design D5/D6).
            Admit(entry.UniqueId, entry.Name, entry.State, entry.CallerIdNum,
                entry.CallerIdName, entry.Context, entry.Extension, entry.Priority, entry.LinkedId,
                fromSnapshot: true);
            added++;
        }

        ChannelManagerLog.Reconciled(_logger, snapshot.Count, added, removed, newerThanSnapshot);
    }

    /// <summary>
    /// Drop one channel the reload proved gone, announcing it on <see cref="ChannelRemoved"/> with
    /// <see cref="AsteriskChannel.RemovedByReload"/> set and its hangup cause left untouched.
    /// </summary>
    /// <returns><c>true</c> when this call was the one that removed the channel.</returns>
    private bool RemoveByReload(AsteriskChannel channel)
    {
        if (!_channelsByUniqueId.TryRemove(channel.UniqueId, out var gone))
            return false;

        string name;
        lock (gone.SyncRoot)
        {
            name = gone.Name;
            gone.MarkRemovedByReload();
            gone.State = ChannelState.Down;
        }

        // Drop the name index only while it still points at this instance, so a reused or renamed
        // name can never evict another channel's entry.
        _channelsByName.TryRemove(new KeyValuePair<string, AsteriskChannel>(name, gone));

        LiveMetrics.ChannelsDestroyed.Add(1);
        ChannelManagerLog.RemovedByReload(_logger, gone.UniqueId, name);
        ChannelRemoved?.Invoke(gone);
        return true;
    }

    public void Clear()
    {
        _channelsByUniqueId.Clear();
        _channelsByName.Clear();
    }
}

/// <summary>Represents a live Asterisk channel with real-time state.</summary>
public sealed class AsteriskChannel : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();

    public override string Id => UniqueId;
    public string UniqueId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ChannelState State { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public string? ConnectedLineNum { get; set; }
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int Priority { get; set; }
    public AsteriskChannel? LinkedChannel { get; set; }
    public HangupCause HangupCause { get; set; }
    public string? LinkedId { get; init; }
    public string? DialedChannel { get; set; }
    public string? DialStatus { get; set; }
    public bool IsOnHold { get; set; }
    public string? HoldMusicClass { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True when this channel left <see cref="ChannelManager"/>'s table because a completed state
    /// reload proved Asterisk no longer has it, rather than because a <c>Hangup</c> event was
    /// observed.
    /// <para>
    /// When it is true, <c>HangupCause</c> carries no observation. A reload reports no cause, so a
    /// <see cref="ChannelManager.ChannelRemoved"/> subscriber MUST treat the cause as unknown and
    /// MUST NOT read the default <c>HangupCause.NotDefined</c> — cause zero — as an abnormal
    /// ending. Internal on purpose: this change adds no public API, and the consumer-visible
    /// marker belongs to the session the removal ends (<c>ADR-0062</c>, design D3).
    /// </para>
    /// </summary>
    internal bool RemovedByReload { get; private set; }

    /// <summary>
    /// Orders this channel's admission against every other one: the value
    /// <see cref="ChannelManager"/>'s admission counter reached when this channel was admitted.
    /// Monotonic and assigned once, so a stamp greater than a mark captured before a reload's
    /// snapshot was requested means the snapshot is older than the channel and says nothing about
    /// it (<c>ADR-0062</c>, design D6).
    /// <para>
    /// A counter and not a timestamp on purpose: <see cref="CreatedAt"/> is
    /// <c>DateTimeOffset.UtcNow</c> at construction and is not injectable, so two admissions
    /// microseconds apart can carry the same instant and the comparison would be intermittent.
    /// Zero means "admitted by something other than <see cref="ChannelManager.OnNewChannel"/>",
    /// which orders before every mark — the direction that keeps a reload able to end a call it
    /// really did prove gone.
    /// </para>
    /// </summary>
    internal long AdmissionMark { get; init; }

    /// <summary>
    /// True when this channel entered <see cref="ChannelManager"/>'s table out of a <c>Status</c>
    /// snapshot — a first load or a post-reconnect reload, both of which travel
    /// <c>VerbaraServer.RequestInitialStateAsync</c> and
    /// <see cref="ChannelManager.ReconcileWithSnapshot"/> — rather than from a live
    /// <c>NewChannel</c> event through <see cref="ChannelManager.OnNewChannel"/>.
    /// <para>
    /// It is the counterpart of <see cref="RemovedByReload"/> at the other end of the channel's
    /// life, and it carries the same kind of knowledge: the SDK did <b>not</b> observe this call
    /// start, so <see cref="State"/> is the only account of it that will ever arrive and no
    /// <c>NewState</c> announcing it is coming. A subscriber opening a record for the channel reads
    /// this as positive evidence that the history behind that state was never seen, instead of
    /// inferring it from a missing event (<c>ADR-0062</c>, design D5).
    /// </para>
    /// <para>
    /// Internal on purpose: this change adds no public API, and the consumer-visible marker belongs
    /// to the session the admission opens.
    /// </para>
    /// </summary>
    internal bool AdmittedFromSnapshot { get; init; }

    /// <summary>
    /// Marks this channel as removed by a state reload. One-way and idempotent: a removal is
    /// terminal, so the flag is never cleared.
    /// </summary>
    internal void MarkRemovedByReload() => RemovedByReload = true;

    /// <summary>Extension history for this channel (bounded to last 100 entries).</summary>
    public IReadOnlyList<ExtensionHistoryEntry> ExtensionHistory => _extensionHistory;

    private const int MaxExtensionHistorySize = 100;
    private readonly List<ExtensionHistoryEntry> _extensionHistory = [];

    internal void AddExtensionHistory(ExtensionHistoryEntry entry)
    {
        if (_extensionHistory.Count >= MaxExtensionHistorySize)
            _extensionHistory.RemoveAt(0);
        _extensionHistory.Add(entry);
    }
}

/// <summary>Record of an extension visited by a channel.</summary>
public sealed record ExtensionHistoryEntry(string Context, string Extension, int Priority, DateTimeOffset Timestamp);

/// <summary>
/// One channel as a completed state reload reported it, buffered for
/// <see cref="ChannelManager.ReconcileWithSnapshot"/>. Its members mirror
/// <see cref="ChannelManager.OnNewChannel"/>'s parameters, so admitting a channel out of a snapshot
/// is the same operation a live <c>NewChannel</c> event performs.
/// </summary>
internal sealed record ChannelSnapshotEntry(
    string UniqueId,
    string Name,
    ChannelState State,
    string? CallerIdNum = null,
    string? CallerIdName = null,
    string? Context = null,
    string? Extension = null,
    int Priority = 1,
    string? LinkedId = null);
