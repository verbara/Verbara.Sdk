using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.MeetMe;

internal static partial class MeetMeManagerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] User joined: room={RoomNumber} channel={Channel} user_num={UserNum}")]
    public static partial void UserJoined(ILogger logger, string roomNumber, string channel, int userNum);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] User left: room={RoomNumber} channel={Channel} user_num={UserNum}")]
    public static partial void UserLeft(ILogger logger, string roomNumber, string channel, int userNum);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] Event not applied, missing conference or channel: event={EventName} room={RoomNumber} channel={Channel}")]
    public static partial void EventNotApplied(ILogger logger, string eventName, string? roomNumber, string? channel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] User muted: room={RoomNumber} channel={Channel} muted={Muted} changed={Changed}")]
    public static partial void UserMuted(ILogger logger, string roomNumber, string channel, bool muted, bool changed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] User talking: room={RoomNumber} channel={Channel} talking={Talking} changed={Changed}")]
    public static partial void UserTalking(ILogger logger, string roomNumber, string channel, bool talking, bool changed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] Event not applied, participant not held: event={EventName} room={RoomNumber} channel={Channel}")]
    public static partial void ParticipantNotHeld(ILogger logger, string eventName, string roomNumber, string channel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] Event not applied, no readable talking status: room={RoomNumber} channel={Channel}")]
    public static partial void TalkingStatusUnreadable(ILogger logger, string? roomNumber, string? channel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CONFERENCE] User removed, the completed conference listing no longer reports it: room={RoomNumber} channel={Channel}")]
    public static partial void UserRemovedBySnapshot(ILogger logger, string roomNumber, string channel);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[CONFERENCE] Rooms reconciled: rooms_listed={RoomsListed} admitted={Admitted} removed={Removed} newer_than_snapshot={NewerThanSnapshot} left_during_read={LeftDuringRead} removes_omitted={RemovesOmitted}")]
    public static partial void Reconciled(ILogger logger, int roomsListed, int admitted, int removed, int newerThanSnapshot,
        int leftDuringRead, bool removesOmitted);

    [LoggerMessage(Level = LogLevel.Error, Message = "[CONFERENCE] Event handler failed for a change whose call did not raise it")]
    public static partial void UnownedHandlerFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Tracks ConfBridge conference rooms in real-time (also usable for app_meetme through direct
/// calls). Each room holds one participant per channel, keyed by the channel name Asterisk sends
/// on the participant's join.
/// </summary>
/// <remarks>
/// Manager operations serialize conference lifetime and state changes with listing application. Direct changes to
/// a room's public participant collection or a participant's public properties are outside that synchronization.
/// </remarks>
public sealed class MeetMeManager
{
    private readonly ConcurrentDictionary<string, MeetMeRoom> _rooms = new();
    private readonly ILogger _logger;

    // The admission ordinal, advanced under _windowGate. A listing cannot change a participant admitted after it opened.
    private long _changes;

    // Always acquire this before a room's lock, including live operations and closing a window. Each window owns
    // reports received during its read, including reports for keys that the table does not yet hold.
    private readonly Lock _windowGate = new();
    private readonly HashSet<ConferenceSnapshotWindow> _openWindows = [];

    // Every event this manager raises, posted under the room lock of its transition and raised once _windowGate is
    // released, so a handler may wait for another thread that calls into this manager.
    private readonly LiveEventOutbox _events;

    private void RaisePending()
    {
        if (!_windowGate.IsHeldByCurrentThread)
            _events.Drain();
    }

    internal int ActiveSnapshotWindowCount
    {
        get
        {
            lock (_windowGate)
                return _openWindows.Count;
        }
    }

    /// <summary>
    /// Raised when a participant joins. Not raised for a participant a reload only read from Asterisk's conference
    /// listing.
    /// </summary>
    public event Action<MeetMeRoom, MeetMeUser>? UserJoined;

    /// <summary>
    /// Raised once for each participant that leaves: on its leave, for each participant still held when its room ends,
    /// and for each held participant a reload's completed conference listing no longer reports, whether its room is
    /// still listed or not.
    /// </summary>
    public event Action<MeetMeRoom, MeetMeUser>? UserLeft;

    /// <summary>
    /// Raised once, with the room and the participant, after a ConfBridge mute, unmute or talking
    /// event changes the participant's <see cref="MeetMeUser.Muted"/> or
    /// <see cref="MeetMeUser.Talking"/>. Not raised when the event leaves the value as it was, for
    /// an event naming a participant that is not held, or for a join: <see cref="UserJoined"/>
    /// already carries the state the join set.
    /// </summary>
    public event Action<MeetMeRoom, MeetMeUser>? UserStateChanged;

    public MeetMeManager(ILogger logger)
    {
        _logger = logger;
        _events = new LiveEventOutbox(ex => MeetMeManagerLog.UnownedHandlerFailed(logger, ex), LiveEventOutbox.DefaultTurnWait);
    }

    public IReadOnlyCollection<MeetMeRoom> Rooms => _rooms.Values.ToList().AsReadOnly();

    public MeetMeRoom? GetRoom(string roomNumber) => _rooms.GetValueOrDefault(roomNumber);

    /// <summary>
    /// Records a participant's join, keyed by <paramref name="channel"/>. The room is created if it
    /// is not held. A join for a channel already held in the room replaces that participant and
    /// raises <see cref="UserJoined"/> again. The participant's <see cref="MeetMeUser.UniqueId"/>
    /// stays <see langword="null"/>; use the overload that takes the join's unique id to set it.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="userNum">The user number Asterisk sent, stored only; <c>0</c> for ConfBridge, which sends none.</param>
    /// <param name="channel">The participant's channel name, which is its key. A missing or empty value is not applied.</param>
    public void OnUserJoined(string roomNumber, int userNum, string channel) =>
        OnUserJoined(roomNumber, userNum, channel, null);

    /// <summary>
    /// Records a participant's join, keyed by <paramref name="channel"/>, and stores the join's
    /// unique id on <see cref="MeetMeUser.UniqueId"/> as data. The room is created if it is not
    /// held. A join for a channel already held in the room replaces that participant and raises
    /// <see cref="UserJoined"/> again.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="userNum">The user number Asterisk sent, stored only; <c>0</c> for ConfBridge, which sends none.</param>
    /// <param name="channel">The participant's channel name, which is its key. A missing or empty value is not applied.</param>
    /// <param name="uniqueId">The join's <c>Uniqueid</c>, stored as data only; it never identifies, replaces or removes a participant.</param>
    public void OnUserJoined(string roomNumber, int userNum, string channel, string? uniqueId) =>
        OnUserJoined(roomNumber, userNum, channel, uniqueId, muted: false);

    /// <summary>
    /// Records a participant's join as the public overload does, and sets
    /// <see cref="MeetMeUser.Muted"/> to <paramref name="muted"/> before the participant is stored
    /// and before <see cref="UserJoined"/> is raised. <see cref="MeetMeUser.Talking"/> starts
    /// <see langword="false"/>, also for a join that replaces a held participant.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="userNum">The user number Asterisk sent, stored only; <c>0</c> for ConfBridge, which sends none.</param>
    /// <param name="channel">The participant's channel name, which is its key. A missing or empty value is not applied.</param>
    /// <param name="uniqueId">The join's <c>Uniqueid</c>, stored as data only.</param>
    /// <param name="muted">The muted value the join carried.</param>
    internal void OnUserJoined(string roomNumber, int userNum, string channel, string? uniqueId, bool muted)
    {
        if (string.IsNullOrEmpty(roomNumber) || string.IsNullOrEmpty(channel))
        {
            MeetMeManagerLog.EventNotApplied(_logger, "join", roomNumber, channel);
            return;
        }

        lock (_windowGate)
        {
            var user = new MeetMeUser
            {
                UserNum = userNum,
                Channel = channel,
                UniqueId = uniqueId,
                State = MeetMeUserState.Joined,
                Muted = muted,
                JoinMark = ++_changes,
            };
            // The join starts a new participant lifetime. Older absent-key reports cannot seed it; its JoinMark
            // protects both fields from every window that was already open. A room-end fence stays in those windows.
            foreach (var window in _openWindows)
                window.Participants.Remove((roomNumber, channel));

            var room = _rooms.GetOrAdd(roomNumber, static rn => new MeetMeRoom { RoomNumber = rn });
            lock (room.SyncRoot)
            {
                room.Users[channel] = user;
                MeetMeManagerLog.UserJoined(_logger, roomNumber, channel, userNum);
                _events.Post(() => UserJoined?.Invoke(room, user));
            }
        }
        RaisePending();
    }

    /// <summary>
    /// Records a participant's leave: removes the participant keyed by <paramref name="channel"/>,
    /// sets its state to <see cref="MeetMeUserState.Left"/> and raises <see cref="UserLeft"/> once.
    /// The room is removed when its last participant leaves. A leave for a room or channel that is
    /// not held changes nothing and raises nothing.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="channel">The channel name the leave names, matched against the join's. A missing or empty value is not applied.</param>
    public void OnUserLeft(string roomNumber, string channel)
    {
        if (string.IsNullOrEmpty(roomNumber) || string.IsNullOrEmpty(channel))
        {
            MeetMeManagerLog.EventNotApplied(_logger, "leave", roomNumber, channel);
            return;
        }

        try
        {
            lock (_windowGate)
            {
                RecordLeave(roomNumber, channel);
                if (!_rooms.TryGetValue(roomNumber, out var room))
                    return;

                lock (room.SyncRoot)
                {
                    if (!room.Users.TryRemove(channel, out var user))
                        return;

                    user.State = MeetMeUserState.Left;
                    RemoveIfEmpty(room);
                    MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
                    _events.Post(() => UserLeft?.Invoke(room, user));
                }
            }
        }
        finally
        {
            RaisePending();
        }
    }

    /// <summary>
    /// Records a ConfBridge mute (<paramref name="muted"/> <see langword="true"/>) or unmute
    /// (<see langword="false"/>) for the participant keyed by <paramref name="channel"/>, and raises
    /// <see cref="UserStateChanged"/> once when the value changed. A room or participant that is not
    /// held is never created and nothing is raised.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="channel">The channel name the event names. A missing or empty value is not applied.</param>
    /// <param name="muted">The participant's new mute state.</param>
    internal void OnUserMuted(string roomNumber, string channel, bool muted) =>
        ApplyLiveField("mute", roomNumber, channel, muted, isMuted: true);

    /// <summary>
    /// Records a ConfBridge talking status for the participant keyed by <paramref name="channel"/>,
    /// and raises <see cref="UserStateChanged"/> once when the value changed. A room or participant
    /// that is not held is never created and nothing is raised.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="channel">The channel name the event names. A missing or empty value is not applied.</param>
    /// <param name="talking">The participant's new talking state.</param>
    internal void OnUserTalking(string roomNumber, string channel, bool talking) =>
        ApplyLiveField("talking", roomNumber, channel, talking, isMuted: false);

    private void ApplyLiveField(string eventName, string roomNumber, string channel, bool value, bool isMuted)
    {
        if (string.IsNullOrEmpty(roomNumber) || string.IsNullOrEmpty(channel))
        {
            MeetMeManagerLog.EventNotApplied(_logger, eventName, roomNumber, channel);
            return;
        }

        try
        {
            lock (_windowGate)
            {
                // Presence in a window is the mark: even an idempotent or absent-key report is newer than that window.
                foreach (var window in _openWindows)
                    window.RecordField(roomNumber, channel, value, isMuted);

                if (!_rooms.TryGetValue(roomNumber, out var room))
                {
                    MeetMeManagerLog.ParticipantNotHeld(_logger, eventName, roomNumber, channel);
                    return;
                }

                lock (room.SyncRoot)
                {
                    if (!room.Users.TryGetValue(channel, out var user))
                    {
                        MeetMeManagerLog.ParticipantNotHeld(_logger, eventName, roomNumber, channel);
                        return;
                    }

                    ApplyField(room, user, value, isMuted);
                }
            }
        }
        finally
        {
            RaisePending();
        }
    }

    // Caller holds _windowGate and room.SyncRoot. Snapshot writes use this origin-neutral path: they are not live
    // reports against other windows. Posts one notification per field that actually changed, in the order of the
    // changes; the caller raises them once it has released the gate.
    private void ApplyField(MeetMeRoom room, MeetMeUser user, bool value, bool isMuted)
    {
        var changed = (isMuted ? user.Muted : user.Talking) != value;
        if (isMuted)
        {
            user.Muted = value;
            MeetMeManagerLog.UserMuted(_logger, room.RoomNumber, user.Channel, value, changed);
        }
        else
        {
            user.Talking = value;
            MeetMeManagerLog.UserTalking(_logger, room.RoomNumber, user.Channel, value, changed);
        }

        if (changed)
            _events.Post(() => UserStateChanged?.Invoke(room, user));
    }

    /// <summary>
    /// Records the end of a conference: removes every participant still held in the room, sets
    /// each one's state to <see cref="MeetMeUserState.Left"/>, raises <see cref="UserLeft"/> once
    /// for each, then removes the room. An end for a room that is not held changes nothing. A later
    /// join for the same conference creates the room anew.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    public void OnConferenceEnded(string roomNumber)
    {
        if (string.IsNullOrEmpty(roomNumber))
        {
            MeetMeManagerLog.EventNotApplied(_logger, "end", roomNumber, null);
            return;
        }

        try
        {
            EndConference(roomNumber);
        }
        finally
        {
            RaisePending();
        }
    }

    private void EndConference(string roomNumber)
    {
        lock (_windowGate)
        {
            foreach (var window in _openWindows)
            {
                // Permanent for this window, including keys never held. A later live join cannot reopen old rows.
                window.EndedRooms.Add(roomNumber);
                foreach (var key in window.Participants.Keys.Where(key => key.Room == roomNumber).ToArray())
                    window.Participants.Remove(key);
            }

            if (!_rooms.TryGetValue(roomNumber, out var room))
                return;

            lock (room.SyncRoot)
            {
                var departed = room.Users.Values.ToArray();
                room.Users.Clear();
                foreach (var user in departed)
                    user.State = MeetMeUserState.Left;
                if (_rooms.TryRemove(new KeyValuePair<string, MeetMeRoom>(roomNumber, room)))
                    room.RemovedFromTable = true;

                // The old lifetime is finished before any handler runs, so a handler's join creates a replacement room.
                foreach (var user in departed)
                {
                    MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
                    _events.Post(() => UserLeft?.Invoke(room, user));
                }
            }
        }
    }

    // Caller holds _windowGate. Remove the empty lifetime before callbacks can recreate the room.
    private void RemoveIfEmpty(MeetMeRoom room)
    {
        lock (room.SyncRoot)
        {
            if (room.Users.IsEmpty && _rooms.TryRemove(new KeyValuePair<string, MeetMeRoom>(room.RoomNumber, room)))
                room.RemovedFromTable = true;
        }
    }

    /// <summary>
    /// Opens the window a load reads Asterisk's conference listing in. The caller opens it <c>before</c> it sends the
    /// room listing, keeps it open until <see cref="ReconcileWithSnapshot"/> has returned, and disposes it on every way
    /// out. A participant that joined after it opened is newer than the listing and is not removed by it; one that left
    /// after it opened is not brought back by it.
    /// </summary>
    internal ConferenceSnapshotWindow OpenSnapshotWindow()
    {
        lock (_windowGate)
        {
            var window = new ConferenceSnapshotWindow(this, _changes);
            _openWindows.Add(window);
            return window;
        }
    }

    internal void CloseSnapshotWindow(ConferenceSnapshotWindow window)
    {
        lock (_windowGate)
        {
            if (window.IsClosed)
                return;

            window.MarkClosed();
            _openWindows.Remove(window);
            window.Participants.Clear();
            window.EndedRooms.Clear();
        }
    }

    /// <summary>
    /// Reconciles the conference table against a completed room listing read in <paramref name="window"/>, and the
    /// participant listings that completed for the rooms it reported.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>A listed participant the room does not hold is admitted silently (no
    /// <see cref="UserJoined"/>), keyed by its channel, with its unique id and the muted and talking values its row
    /// carries (an unreadable value reads <see langword="false"/>); its room is created if it is not held.</description></item>
    /// <item><description>A listed participant already held keeps its entry; a readable muted or talking value refreshes
    /// only if no live report of that field was handled during the window, raising <see cref="UserStateChanged"/> when
    /// it changed. A new participant silently merges newer field reports received while it was absent.</description></item>
    /// <item><description>When <paramref name="removeOmitted"/> is set, a held participant is removed, and
    /// <see cref="UserLeft"/> raised for it once, when its room is not listed, or when its room's participant listing
    /// completed without it. A room left with no participant is removed. A listed room whose participant listing did
    /// not complete keeps every participant.</description></item>
    /// <item><description>A participant that joined after the window's request is not removed, and one that left
    /// after it is not brought back.</description></item>
    /// </list>
    /// The participants are only ever made from the participant listing's rows, never from a bridge's channels.
    /// </remarks>
    /// <param name="window">The window opened before the room listing was sent, still open.</param>
    /// <param name="listedRooms">The rooms the completed room listing reported; empty for <c>No active conferences.</c>.</param>
    /// <param name="participants">
    /// The rows of each room whose participant listing completed, by room. A listed room absent from it is left alone.
    /// </param>
    /// <param name="removeOmitted">
    /// Whether the listing is evidence of absence: <see langword="false"/> applies only the admissions, for an answer
    /// whose completion cannot be told.
    /// </param>
    /// <exception cref="ObjectDisposedException"><paramref name="window"/> is already closed.</exception>
    internal void ReconcileWithSnapshot(ConferenceSnapshotWindow window, IReadOnlyCollection<string> listedRooms,
        IReadOnlyDictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> participants, bool removeOmitted)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            ReconcileUnderGate(window, listedRooms, participants, removeOmitted);
        }
        finally
        {
            RaisePending();
        }
    }

    // The whole reconciliation is one critical section under _windowGate; its UserLeft and UserStateChanged
    // notifications are raised after it, in the order of its changes.
    private void ReconcileUnderGate(ConferenceSnapshotWindow window, IReadOnlyCollection<string> listedRooms,
        IReadOnlyDictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> participants, bool removeOmitted)
    {
        lock (_windowGate)
        {
            ObjectDisposedException.ThrowIf(window.IsClosed, window);
            var rooms = new HashSet<string>(listedRooms, StringComparer.Ordinal);
            var removed = 0;
            var newer = 0;
            if (removeOmitted)
            {
                foreach (var room in _rooms.Values)
                {
                    IReadOnlyList<ConferenceSnapshotParticipant>? rows = null;
                    if (rooms.Contains(room.RoomNumber) && !participants.TryGetValue(room.RoomNumber, out rows))
                        continue;

                    var keep = rows is null
                        ? null
                        : new HashSet<string>(rows.Select(static r => r.Channel), StringComparer.Ordinal);
                    lock (room.SyncRoot)
                    {
                        foreach (var held in room.Users.Values.Where(user => keep?.Contains(user.Channel) != true).ToArray())
                        {
                            if (window.IsClosed)
                                return;
                            if (!IsCurrentParticipant(room, held))
                                continue;
                            if (held.JoinMark > window.Mark)
                            {
                                newer++;
                                continue;
                            }

                            RecordLeave(room.RoomNumber, held.Channel);
                            room.Users.TryRemove(new KeyValuePair<string, MeetMeUser>(held.Channel, held));
                            held.State = MeetMeUserState.Left;
                            RemoveIfEmpty(room);
                            MeetMeManagerLog.UserRemovedBySnapshot(_logger, room.RoomNumber, held.Channel);
                            removed++;
                            _events.Post(() => UserLeft?.Invoke(room, held));
                        }
                    }
                }
            }

            var admitted = 0;
            var leftDuringRead = 0;
            foreach (var (roomNumber, rows) in participants)
            {
                if (!rooms.Contains(roomNumber))
                    continue;

                foreach (var row in rows.Where(static row => !string.IsNullOrEmpty(row.Channel)))
                {
                    if (window.IsClosed)
                        return;
                    if (window.EndedRooms.Contains(roomNumber)
                        || window.Participants.TryGetValue((roomNumber, row.Channel), out var state) && state.Left)
                    {
                        leftDuringRead++;
                        continue;
                    }

                    window.Participants.TryGetValue((roomNumber, row.Channel), out state);
                    var room = _rooms.GetOrAdd(roomNumber, static rn => new MeetMeRoom { RoomNumber = rn });
                    lock (room.SyncRoot)
                    {
                        if (!room.Users.TryGetValue(row.Channel, out var held))
                        {
                            var user = new MeetMeUser
                            {
                                UserNum = 0,
                                Channel = row.Channel,
                                UniqueId = row.UniqueId,
                                State = MeetMeUserState.Joined,
                                Muted = state?.Muted ?? row.Muted ?? false,
                                Talking = state?.Talking ?? row.Talking ?? false,
                                JoinMark = ++_changes,
                            };
                            room.Users[row.Channel] = user;
                            admitted++;
                            continue;
                        }

                        if (row.Muted is { } muted && CanRefreshField(window, room, held, isMuted: true))
                            ApplyField(room, held, muted, isMuted: true);
                        // No handler runs inside the reconciliation; the recheck keeps each field's write conditional on
                        // identity, lifetime and its own live mark under the same locks.
                        if (row.Talking is { } talking && CanRefreshField(window, room, held, isMuted: false))
                            ApplyField(room, held, talking, isMuted: false);
                    }
                }
            }

            MeetMeManagerLog.Reconciled(_logger, rooms.Count, admitted, removed, newer, leftDuringRead, removeOmitted);
        }
    }

    private bool CanRefreshField(ConferenceSnapshotWindow window, MeetMeRoom room, MeetMeUser user, bool isMuted) =>
        !window.IsClosed && !window.EndedRooms.Contains(room.RoomNumber)
        && IsCurrentParticipant(room, user) && user.JoinMark <= window.Mark
        && (!window.Participants.TryGetValue((room.RoomNumber, user.Channel), out var state)
            || !state.Left && (isMuted ? state.Muted : state.Talking) is null);

    private bool IsCurrentParticipant(MeetMeRoom room, MeetMeUser user) =>
        !room.RemovedFromTable && ReferenceEquals(_rooms.GetValueOrDefault(room.RoomNumber), room)
        && ReferenceEquals(room.Users.GetValueOrDefault(user.Channel), user);

    // Caller holds _windowGate. A leave also invalidates an absent key's rows and resets its old-life field reports.
    private void RecordLeave(string roomNumber, string channel)
    {
        foreach (var window in _openWindows)
            window.Participants[(roomNumber, channel)] = new ConferenceWindowParticipant { Left = true };
    }

    /// <summary>
    /// Empties the conference table without raising anything. A reload no longer calls it: it reconciles the table
    /// against Asterisk's conference listing instead.
    /// </summary>
    public void Clear()
    {
        lock (_windowGate)
        {
            foreach (var room in _rooms.Values)
            {
                lock (room.SyncRoot)
                    room.RemovedFromTable = true;
            }
            _rooms.Clear();
            foreach (var window in _openWindows)
                window.Participants.Clear();
        }
    }
}

/// <summary>A participant as Asterisk's participant listing (<c>ConfbridgeList</c>) reports it.</summary>
/// <param name="Channel">The participant's channel name, its key.</param>
/// <param name="UniqueId">The row's <c>Uniqueid</c>.</param>
/// <param name="Muted">The row's <c>Muted</c>, or <see langword="null"/> when unreadable.</param>
/// <param name="Talking">The row's <c>Talking</c>, or <see langword="null"/> when unreadable.</param>
internal sealed record ConferenceSnapshotParticipant(string Channel, string? UniqueId, bool? Muted, bool? Talking);

/// <summary>
/// The span of one load's conference listing: opened by <see cref="MeetMeManager.OpenSnapshotWindow"/> before the room
/// listing is sent, handed to <see cref="MeetMeManager.ReconcileWithSnapshot"/>, and disposed once that has returned or
/// the read ended without it.
/// </summary>
internal sealed class ConferenceSnapshotWindow : IDisposable
{
    private readonly MeetMeManager _owner;
    private int _closed;

    internal ConferenceSnapshotWindow(MeetMeManager owner, long mark)
    {
        _owner = owner;
        Mark = mark;
    }

    /// <summary>The manager's change ordinal when the window opened, before the room listing was sent.</summary>
    internal long Mark { get; }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    // Accessed only under the owner's window gate; closed windows release both collections.
    internal Dictionary<(string Room, string Channel), ConferenceWindowParticipant> Participants { get; } = [];
    internal HashSet<string> EndedRooms { get; } = new(StringComparer.Ordinal);

    // Caller holds the owner's window gate. Reuse the retained lifetime, including any leave tombstone.
    internal void RecordField(string roomNumber, string channel, bool value, bool isMuted)
    {
        var key = (roomNumber, channel);
        if (!Participants.TryGetValue(key, out var state))
        {
            state = new ConferenceWindowParticipant();
            Participants.Add(key, state);
        }

        if (isMuted)
            state.Muted = value;
        else
            state.Talking = value;
    }

    internal void MarkClosed() => Volatile.Write(ref _closed, 1);

    public void Dispose() => _owner.CloseSnapshotWindow(this);
}

internal sealed class ConferenceWindowParticipant
{
    internal bool Left { get; init; }
    internal bool? Muted { get; set; }
    internal bool? Talking { get; set; }
}

/// <summary>Represents a ConfBridge (or legacy MeetMe) conference room.</summary>
public sealed class MeetMeRoom
{
    internal readonly Lock SyncRoot = new();

    // Set, under the manager gate and SyncRoot, when the room leaves the table. Reentrant snapshot continuations must
    // not change that detached lifetime after an event subscriber creates its replacement.
    internal bool RemovedFromTable { get; set; }

    public string RoomNumber { get; init; } = string.Empty;

    /// <summary>
    /// The participants still held in the room, keyed by the channel name Asterisk sent on each
    /// participant's join. One entry per channel.
    /// </summary>
    public ConcurrentDictionary<string, MeetMeUser> Users { get; } = new();

    public int UserCount => Users.Count;
}

/// <summary>Represents a participant in a conference room.</summary>
public sealed class MeetMeUser
{
    /// <summary>
    /// The user number exactly as Asterisk sent it. It is <c>0</c> for every ConfBridge participant,
    /// because ConfBridge sends none; it never identifies a participant (the key is <see cref="Channel"/>).
    /// </summary>
    public int UserNum { get; init; }

    public string Channel { get; init; } = string.Empty;

    /// <summary>
    /// The <c>Uniqueid</c> of the participant's join as Asterisk sent it, or <see langword="null"/>
    /// when the join carried none or was recorded without it. Data only: it never identifies,
    /// replaces or removes a participant.
    /// </summary>
    public string? UniqueId { get; init; }

    public MeetMeUserState State { get; set; }

    /// <summary>
    /// Whether the participant is muted, as Asterisk last reported it: set from the join's
    /// <c>Muted</c> header (an absent or unreadable value reads <see langword="false"/>), then by
    /// each <c>ConfbridgeMute</c> (<see langword="true"/>) and <c>ConfbridgeUnmute</c>
    /// (<see langword="false"/>) that names this participant.
    /// </summary>
    /// <remarks>
    /// This is the participant's mute request as Asterisk reports it, not whether the participant is
    /// audible: a participant on hold music, or a wait-marked participant with no marked user present,
    /// is silent while reading <see langword="false"/>. A whole-room mute from the conference keypad
    /// sends no event, so the participants keep reading <see langword="false"/> until a later
    /// per-participant event or a reload of the conference table reads the real state.
    /// </remarks>
    public bool Muted { get; set; }

    /// <summary>
    /// Whether the participant is talking, as the last <c>ConfbridgeTalking</c> naming it reported
    /// (<c>TalkingStatus</c>; an absent or unreadable status leaves the value unchanged).
    /// <see langword="false"/> from the join.
    /// </summary>
    /// <remarks>
    /// Asterisk sends <c>ConfbridgeTalking</c> only for a ConfBridge user profile with
    /// <c>talk_detection_events=yes</c> (the default is <c>no</c>); without it this stays
    /// <see langword="false"/>.
    /// </remarks>
    public bool Talking { get; set; }

    // When the manager took this participant in, on its change ordinal: a listing requested before it cannot report it.
    internal long JoinMark { get; init; }
}
