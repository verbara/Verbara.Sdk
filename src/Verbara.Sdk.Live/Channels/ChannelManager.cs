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

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CHANNEL] Reconciled: snapshot={SnapshotCount} added={Added} removed={Removed} newer_than_snapshot={NewerThanSnapshot} departed_during_read={DepartedDuringRead}")]
    public static partial void Reconciled(ILogger logger, int snapshotCount, int added, int removed, int newerThanSnapshot, int departedDuringRead);
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
    /// Orders admissions and departures on one scale. Every admission takes the next value, and so
    /// does every departure recorded while a read window is open, so "admitted after a window's
    /// mark" and "departed after it" are the same comparison. Monotonic and never reset — not even
    /// by <see cref="Clear"/> — because its only job is ordering.
    /// </summary>
    private long _admissions;

    /// <summary>
    /// The manager lock. It guards every change to the unique-id table — each admission's check,
    /// stamp and insert into both indices, and each removal from both indices, by <c>Hangup</c> or
    /// by a reload — together with the read windows and the departures recorded for them: a
    /// window's opening and closing, and each departure's stamp and record. So a departure that
    /// found no window open is ordered before any window opened after it, and no admission can
    /// slip between another route's check and its insert.
    /// <para>
    /// Lock order: no subscriber is ever invoked under it, and no thread ever waits on a channel's
    /// <c>SyncRoot</c> while holding it. An admitting thread takes the new channel's
    /// <c>SyncRoot</c> first and this lock inside it; a removing thread releases this lock before
    /// it takes the removed channel's <c>SyncRoot</c>.
    /// </para>
    /// </summary>
    private readonly Lock _readWindowGate = new();

    /// <summary>The mark of every read window still open, one entry per window. Guarded by <see cref="_readWindowGate"/>.</summary>
    private readonly List<long> _openReadMarks = [];

    /// <summary>
    /// The departures some open read window may still need: each channel that hung up, or that a
    /// reload removed, while at least one window was open, with the stamp its departure took.
    /// Bounded by the departures observed during the reads in progress: closing a window drops
    /// every stamp no window still open can need, and with no window open the record is empty.
    /// Guarded by <see cref="_readWindowGate"/>.
    /// </summary>
    private readonly Dictionary<string, long> _departures = new(StringComparer.Ordinal);

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
    /// Open the window a reload reads its channel snapshot in. The window carries the admission
    /// mark read now: every channel admitted from now on carries a stamp greater than it, so
    /// <see cref="ReconcileWithSnapshot"/> can tell "the snapshot omits it" from "the snapshot is
    /// older than it". While it is open, every <c>Hangup</c> and every removal by a reload is
    /// recorded with a stamp greater than it as well, held channel or not, so the reconciliation
    /// can tell "the snapshot lists it" from "the snapshot is older than its departure".
    /// <para>
    /// The caller MUST open the window <c>before</c> it issues the request whose answer it will
    /// reconcile, keep it open until that reconciliation has returned, and dispose it on every
    /// exit — completed, refused, failed or cancelled. Opening it after the request would place
    /// every channel that arrived during the read at or below the mark and hand the reconciliation
    /// the power to end a call that is up.
    /// </para>
    /// <para>
    /// What an open window costs is bounded by the read it covers: the departures observed while
    /// it is open, tens of bytes each, all dropped once no open window needs them. Over an
    /// <c>AmiConnection</c> a <c>Status</c> read lasts at most the connection's
    /// <c>DefaultEventTimeout</c>, 5 seconds by default. With that option set to
    /// <see cref="TimeSpan.Zero"/>, or over another <c>IAmiConnection</c>, how long a read may last
    /// is the consumer's choice, and a <c>Status</c> that never answers keeps its window, and the
    /// departures recorded for it, until the read ends. There is no cap: a capped record would
    /// have to forget a departure and let the stale snapshot bring that call back.
    /// </para>
    /// <para>
    /// Several windows may be open at once — a public state request can overlap the reload after a
    /// reconnect — and each keeps its own mark.
    /// </para>
    /// </summary>
    internal SnapshotReadWindow OpenReadWindow()
    {
        lock (_readWindowGate)
        {
            var mark = Interlocked.Read(ref _admissions);
            _openReadMarks.Add(mark);
            return new SnapshotReadWindow(this, mark);
        }
    }

    /// <summary>
    /// Close one window: forget its mark and drop every recorded departure no window still open can
    /// need — those stamped at or below the smallest mark still open, and all of them when none is.
    /// A departure stamped at or below a window's mark happened before that window's request was
    /// sent, so its snapshot cannot list the channel on account of it.
    /// </summary>
    internal void CloseReadWindow(SnapshotReadWindow window)
    {
        lock (_readWindowGate)
        {
            _openReadMarks.Remove(window.Mark);
            if (_openReadMarks.Count == 0)
            {
                _departures.Clear();
                return;
            }

            var oldestOpenMark = _openReadMarks.Min();
            var noLongerNeeded = _departures
                .Where(departure => departure.Value <= oldestOpenMark)
                .Select(static departure => departure.Key)
                .ToList();
            foreach (var uniqueId in noLongerNeeded)
                _departures.Remove(uniqueId);
        }
    }

    /// <summary>
    /// Record a departure for the open read windows: stamp it on the admission counter and keep it
    /// until no open window can need it. Nothing is recorded while no window is open — any window
    /// opened later reads a mark at or above this point and its request postdates the departure.
    /// The caller holds <see cref="_readWindowGate"/>.
    /// </summary>
    private void RecordDepartureLocked(string uniqueId)
    {
        if (_openReadMarks.Count == 0)
            return;

        _departures[uniqueId] = Interlocked.Increment(ref _admissions);
    }

    /// <summary>
    /// Whether <paramref name="uniqueId"/> hung up, or was removed by another reload, after
    /// <paramref name="window"/>'s request was sent: the snapshot that window reads is then older
    /// than the departure, and its entry for the channel is stale. The caller holds
    /// <see cref="_readWindowGate"/>.
    /// </summary>
    private bool DepartedDuringLocked(string uniqueId, SnapshotReadWindow window) =>
        _departures.TryGetValue(uniqueId, out var stamp) && stamp > window.Mark;

    /// <summary>
    /// Handle a NewChannel event.
    /// <para>
    /// A channel is admitted once while the table holds it, whichever route admitted it. A
    /// <c>NewChannel</c> for a <c>UniqueId</c> the table already holds — admitted out of a reload's
    /// snapshot that reached it first, or by an earlier <c>NewChannel</c> — raises nothing,
    /// replaces nothing and records nothing: the held instance, and every reference a subscriber
    /// kept to it, stays the channel's one instance until its removal.
    /// </para>
    /// </summary>
    public void OnNewChannel(string uniqueId, string channelName, ChannelState state,
        string? callerIdNum = null, string? callerIdName = null,
        string? context = null, string? exten = null, int priority = 1,
        string? linkedId = null) =>
        Admit(new ChannelSnapshotEntry(uniqueId, channelName, state, callerIdNum, callerIdName,
            context, exten, priority, linkedId), window: null);

    /// <summary>What one attempt to admit a channel did.</summary>
    private enum Admission
    {
        /// <summary>The channel entered the table and <see cref="ChannelAdded"/> was raised for it.</summary>
        Admitted,

        /// <summary>The table already held the <c>UniqueId</c>; nothing changed and nothing was raised.</summary>
        AlreadyHeld,

        /// <summary>The channel departed after the snapshot listing it was requested; it was not admitted.</summary>
        DepartedDuringRead,
    }

    /// <summary>
    /// The one place a channel enters the table, through either route: a live <c>NewChannel</c>
    /// (<paramref name="window"/> is <c>null</c>) or a reload's snapshot (the window that snapshot
    /// was read in, which becomes <see cref="AsteriskChannel.AdmittedFromSnapshot"/>).
    /// <para>
    /// Atomic per <c>UniqueId</c> against the other route, against <c>Hangup</c> and against a
    /// reload's removal: the check that the table does not hold the channel, the check that it did
    /// not depart during the snapshot's read, the admission stamp and the insert into both indices
    /// happen under the manager lock in one step. Without it two routes admitting one channel at
    /// the same time could both pass the check and announce it twice, the second instance
    /// replacing the first under every subscriber that kept it; and a departure could land between
    /// the check and the insert and leave a channel no further <c>Hangup</c> would ever remove.
    /// Contention tests reach those interleavings only some of the time, so the guarantee rests on
    /// this structure — one lock around check and insert, and every removal from the table taken
    /// under the same lock — rather than on a red rate.
    /// </para>
    /// <para>
    /// The announcement cannot be overtaken by the channel's removal. The admitting thread takes
    /// the new channel's <c>SyncRoot</c> — an object no other thread can see yet, so taking it never
    /// blocks — before the manager lock, and keeps it until <see cref="ChannelAdded"/> has returned.
    /// A <c>Hangup</c> of that channel on another thread removes it from the table under the
    /// manager lock, releases that lock, and then waits on the channel's <c>SyncRoot</c> before it
    /// sets the cause and raises <see cref="ChannelRemoved"/>, so <see cref="ChannelRemoved"/> always
    /// follows <see cref="ChannelAdded"/>. The cost: a <see cref="ChannelAdded"/> subscriber that
    /// blocks also delays that channel's removal on the thread that observed it.
    /// </para>
    /// <para>
    /// A <see cref="ChannelAdded"/> subscriber may call back into this manager — hang up another
    /// channel, for instance — because nothing it can reach waits on a <c>SyncRoot</c> under the
    /// manager lock. What it must not do is make two threads each hold one freshly admitted
    /// channel's <c>SyncRoot</c> while hanging up the other's: a subscriber that, on two threads at
    /// once, hangs up the channel the other thread is admitting would wait on each other forever.
    /// </para>
    /// </summary>
    private Admission Admit(ChannelSnapshotEntry entry, SnapshotReadWindow? window)
    {
        var channel = new AsteriskChannel
        {
            UniqueId = entry.UniqueId,
            Name = entry.Name,
            State = entry.State,
            CallerIdNum = entry.CallerIdNum,
            CallerIdName = entry.CallerIdName,
            Context = entry.Context,
            Extension = entry.Extension,
            Priority = entry.Priority,
            LinkedId = entry.LinkedId,
            AdmittedFromSnapshot = window is not null
        };

        lock (channel.SyncRoot)
        {
            lock (_readWindowGate)
            {
                if (_channelsByUniqueId.ContainsKey(entry.UniqueId))
                    return Admission.AlreadyHeld;

                if (window is not null && DepartedDuringLocked(entry.UniqueId, window))
                    return Admission.DepartedDuringRead;

                // Stamped before the channel is published to either index, so a channel visible to
                // a concurrent reconciliation always carries the mark that ordered it.
                channel.AdmissionMark = Interlocked.Increment(ref _admissions);
                _channelsByUniqueId[entry.UniqueId] = channel;
                _channelsByName[entry.Name] = channel;
            }

            LiveMetrics.ChannelsCreated.Add(1);
            ChannelManagerLog.NewChannel(_logger, entry.UniqueId, entry.Name, entry.State);
            ChannelAdded?.Invoke(channel);
        }

        return Admission.Admitted;
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

    /// <summary>
    /// Handle a Hangup event.
    /// <para>
    /// While a state reload is reading its channel snapshot, the hangup is also recorded for that
    /// read, whether or not the table holds the channel: the snapshot may have been answered before
    /// the hangup and still list the channel, and a channel the SDK never held — one that started
    /// while the connection was down — must not be admitted from it either, since no further
    /// <c>Hangup</c> would ever remove it.
    /// </para>
    /// </summary>
    public void OnHangup(string uniqueId, HangupCause cause = HangupCause.NormalClearing)
    {
        AsteriskChannel? channel;
        lock (_readWindowGate)
        {
            RecordDepartureLocked(uniqueId);
            if (!_channelsByUniqueId.TryRemove(uniqueId, out channel))
                return;
            _channelsByName.TryRemove(channel.Name, out _);
        }

        // Taken after the manager lock is released: when the channel is still being admitted on
        // another thread, this waits until its ChannelAdded has returned, so the removal is never
        // announced before the admission.
        lock (channel.SyncRoot)
        {
            channel.HangupCause = cause;
            channel.State = ChannelState.Down;
        }
        LiveMetrics.ChannelsDestroyed.Add(1);
        ChannelManagerLog.Hangup(_logger, uniqueId, cause);
        ChannelRemoved?.Invoke(channel);
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
    /// <see cref="ChannelAdded"/> for every snapshot entry not already held that did not depart
    /// while the snapshot was read, and <see cref="ChannelRemoved"/> for every held channel the
    /// snapshot does not contain.
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
    /// The header is read correctly now, and the decision still holds on other evidence: the
    /// snapshot is older than the events that arrived while it was being read, and the admission
    /// mark orders <em>admissions</em> only, so nothing here can tell a snapshot state from a
    /// <c>NewState</c> that overtook it. Refreshing would hand the reload the power to push a call
    /// that answered during the read back to <c>Ringing</c> — the defect the admission mark forbids
    /// for a channel's existence, applied to its state. The price is stated plainly: a call whose state changed while
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
    /// <see cref="AsteriskChannel.RemovedByReload"/> and is given no hangup cause: a subscriber
    /// must be able to tell an ending nobody observed from one Asterisk reported.
    /// </para>
    /// <para>
    /// Absence from the snapshot is evidence only about channels the snapshot could have contained.
    /// Live events resume before a reload completes, so a call that starts while the snapshot is
    /// being read is admitted to this table and is legitimately missing from it; the snapshot is
    /// older than that channel and says nothing about it. The mark of <paramref name="window"/> is
    /// where that line is drawn — see <see cref="OpenReadWindow"/>.
    /// </para>
    /// <para>
    /// Presence in the snapshot is likewise evidence only up to the moment Asterisk answered. A
    /// channel that hung up, or that another reload removed, after the window was opened may still
    /// be listed; it is not admitted, because no further <c>Hangup</c> would ever remove it and it
    /// would hold its call open. That holds whether or not the table held the channel when the read
    /// began, and for a departure observed while this reconciliation is already running.
    /// </para>
    /// </summary>
    /// <param name="snapshot">Every channel the reload reported, already materialized.</param>
    /// <param name="window">
    /// The window opened with <see cref="OpenReadWindow"/> <c>before</c> the snapshot was
    /// requested, and still open. A held channel stamped above its mark is skipped, not removed.
    /// There is deliberately no overload without one: a caller that cannot say when its snapshot
    /// was taken cannot be allowed to end calls with it.
    /// </param>
    /// <exception cref="ObjectDisposedException">
    /// <paramref name="window"/> is already closed: the departures it recorded may be gone, so its
    /// snapshot can no longer be told from a stale one.
    /// </exception>
    internal void ReconcileWithSnapshot(
        IReadOnlyCollection<ChannelSnapshotEntry> snapshot, SnapshotReadWindow window)
    {
        ObjectDisposedException.ThrowIf(window.IsClosed, window);
        var admittedThrough = window.Mark;

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
        foreach (var held in _channelsByUniqueId.Values.Where(channel => !present.Contains(channel.UniqueId)))
        {
            // The snapshot was requested before this channel was admitted, so it could not have
            // reported it and its silence is not evidence. Removing it here would end a call that
            // is up — the worst outcome this reconciliation can produce, and worse than the ghost
            // sessions it exists to remove.
            if (held.AdmissionMark > admittedThrough)
            {
                newerThanSnapshot++;
                continue;
            }

            if (RemoveByReload(held))
                removed++;
        }

        // Each entry is checked and admitted in one step, after every earlier entry has been
        // admitted, so an entry the snapshot repeats is admitted once and a channel held by now —
        // admitted live while the snapshot was read — is left as it is.
        var added = 0;
        var departedDuringRead = 0;
        foreach (var entry in snapshot)
        {
            // fromSnapshot: the SDK never saw this channel start. A subscriber that opens a record
            // for it must be able to tell that from a live NewChannel, because the state the
            // snapshot reports is the only history it will ever get. A channel listed but seen to
            // leave after the request was sent is not admitted: Asterisk answered before the
            // departure, and admitting it would bring back a call that ended.
            switch (Admit(entry, window))
            {
                case Admission.Admitted:
                    added++;
                    break;
                case Admission.DepartedDuringRead:
                    departedDuringRead++;
                    break;
                case Admission.AlreadyHeld:
                    break;
            }
        }

        ChannelManagerLog.Reconciled(_logger, snapshot.Count, added, removed, newerThanSnapshot, departedDuringRead);
    }

    /// <summary>
    /// Drop one channel the reload proved gone, announcing it on <see cref="ChannelRemoved"/> with
    /// <see cref="AsteriskChannel.RemovedByReload"/> set and its hangup cause left untouched.
    /// <para>
    /// The removal is a departure like a <c>Hangup</c> and is recorded the same way for every read
    /// still open, so an older snapshot read at the same time, which may still list the channel,
    /// does not admit it again.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when this call was the one that removed the channel.</returns>
    private bool RemoveByReload(AsteriskChannel channel)
    {
        AsteriskChannel? gone;
        lock (_readWindowGate)
        {
            RecordDepartureLocked(channel.UniqueId);
            if (!_channelsByUniqueId.TryRemove(channel.UniqueId, out gone))
                return false;
        }

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

    /// <summary>
    /// Empty the table without announcing anything, and forget every recorded departure with it:
    /// the table starts over, and nothing it recorded outlives the reset. A read window still open
    /// stays open with its mark.
    /// </summary>
    public void Clear()
    {
        lock (_readWindowGate)
        {
            _channelsByUniqueId.Clear();
            _channelsByName.Clear();
            _departures.Clear();
        }
    }
}

/// <summary>
/// The span of one channel snapshot's read: opened by <see cref="ChannelManager.OpenReadWindow"/>
/// before the request is sent, handed to <see cref="ChannelManager.ReconcileWithSnapshot"/>, and
/// disposed once the reconciliation has returned or the read has ended without one. A mark exists
/// only inside a window, so no caller can reconcile against a mark it did not take before its
/// request.
/// </summary>
internal sealed class SnapshotReadWindow : IDisposable
{
    private readonly ChannelManager _owner;
    private int _closed;

    internal SnapshotReadWindow(ChannelManager owner, long mark)
    {
        _owner = owner;
        Mark = mark;
    }

    /// <summary>
    /// The admission count when the window was opened. A channel stamped above it was admitted —
    /// or departed — after the snapshot was requested, so the snapshot could not have reported it
    /// as it now is.
    /// </summary>
    internal long Mark { get; }

    /// <summary>Whether the window has been closed.</summary>
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Close the window, dropping the departures recorded for it that no other open window needs.
    /// Safe to call more than once; only the first call closes it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.CloseReadWindow(this);
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
    internal long AdmissionMark { get; set; }

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
