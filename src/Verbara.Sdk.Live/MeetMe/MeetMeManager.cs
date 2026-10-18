using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
}

/// <summary>
/// Tracks ConfBridge conference rooms in real-time (also usable for app_meetme through direct
/// calls). Each room holds one participant per channel, keyed by the channel name Asterisk sends
/// on the participant's join.
/// </summary>
/// <remarks>
/// Calls are expected one at a time for a given room. <c>VerbaraServer</c> guarantees this: it
/// applies conference events in order from a single event pump. A join that runs concurrently
/// with the room's last leave or with <see cref="OnConferenceEnded"/> can land in a room that is
/// being removed and is then lost.
/// </remarks>
public sealed class MeetMeManager
{
    private readonly ConcurrentDictionary<string, MeetMeRoom> _rooms = new();
    private readonly ILogger _logger;

    // The change ordinal: every join and every departure takes the next value, so a snapshot window can tell what
    // changed after its request was sent.
    private long _changes;

    // The snapshot windows open now (their marks), and the participants that left after the oldest one's request was
    // sent: a listing read in such a window may still report them, and must not bring them back.
    private readonly Lock _windowGate = new();
    private readonly List<long> _openMarks = [];
    private readonly Dictionary<(string Room, string Channel), long> _leftDuringRead = [];

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

    public MeetMeManager(ILogger logger) => _logger = logger;

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

        var user = new MeetMeUser
        {
            UserNum = userNum,
            Channel = channel,
            UniqueId = uniqueId,
            State = MeetMeUserState.Joined,
            Muted = muted,
            JoinMark = Interlocked.Increment(ref _changes),
        };
        MeetMeRoom room;
        while (true)
        {
            room = _rooms.GetOrAdd(roomNumber, static rn => new MeetMeRoom { RoomNumber = rn });
            lock (room.SyncRoot)
            {
                // Taken out of the table after GetOrAdd returned it: the participant joins the table's room instead.
                if (room.RemovedFromTable)
                    continue;

                room.Users[channel] = user;
                break;
            }
        }

        MeetMeManagerLog.UserJoined(_logger, roomNumber, channel, userNum);
        UserJoined?.Invoke(room, user);
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

        RecordLeave(roomNumber, channel);
        if (_rooms.TryGetValue(roomNumber, out var room) && room.Users.TryRemove(channel, out var user))
        {
            user.State = MeetMeUserState.Left;
            MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
            UserLeft?.Invoke(room, user);
            RemoveIfEmpty(room);
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
    internal void OnUserMuted(string roomNumber, string channel, bool muted)
    {
        if (TryGetHeld("mute", roomNumber, channel, out var room, out var user))
        {
            var changed = user.Muted != muted;
            user.Muted = muted;
            MeetMeManagerLog.UserMuted(_logger, roomNumber, channel, muted, changed);
            if (changed)
            {
                UserStateChanged?.Invoke(room, user);
            }
        }
    }

    /// <summary>
    /// Records a ConfBridge talking status for the participant keyed by <paramref name="channel"/>,
    /// and raises <see cref="UserStateChanged"/> once when the value changed. A room or participant
    /// that is not held is never created and nothing is raised.
    /// </summary>
    /// <param name="roomNumber">The conference name. A missing or empty value is not applied.</param>
    /// <param name="channel">The channel name the event names. A missing or empty value is not applied.</param>
    /// <param name="talking">The participant's new talking state.</param>
    internal void OnUserTalking(string roomNumber, string channel, bool talking)
    {
        if (TryGetHeld("talking", roomNumber, channel, out var room, out var user))
        {
            var changed = user.Talking != talking;
            user.Talking = talking;
            MeetMeManagerLog.UserTalking(_logger, roomNumber, channel, talking, changed);
            if (changed)
            {
                UserStateChanged?.Invoke(room, user);
            }
        }
    }

    private bool TryGetHeld(string eventName, string roomNumber, string channel,
        [NotNullWhen(true)] out MeetMeRoom? room, [NotNullWhen(true)] out MeetMeUser? user)
    {
        room = null;
        user = null;
        if (string.IsNullOrEmpty(roomNumber) || string.IsNullOrEmpty(channel))
        {
            MeetMeManagerLog.EventNotApplied(_logger, eventName, roomNumber, channel);
            return false;
        }

        if (!_rooms.TryGetValue(roomNumber, out room) || !room.Users.TryGetValue(channel, out user))
        {
            room = null;
            MeetMeManagerLog.ParticipantNotHeld(_logger, eventName, roomNumber, channel);
            return false;
        }

        return true;
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

        if (!_rooms.TryGetValue(roomNumber, out var room))
        {
            return;
        }

        foreach (var channel in room.Users.Keys)
        {
            RecordLeave(roomNumber, channel);
            if (!room.Users.TryRemove(channel, out var user))
            {
                // A leave already removed this participant and raised its UserLeft.
                continue;
            }

            user.State = MeetMeUserState.Left;
            MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
            UserLeft?.Invoke(room, user);
        }

        lock (room.SyncRoot)
        {
            if (_rooms.TryRemove(new KeyValuePair<string, MeetMeRoom>(roomNumber, room)))
                room.RemovedFromTable = true;
        }
    }

    // Takes a room out of the table when it holds no participant. Decided under the room's lock, where every participant
    // is added: a join is either in the room before the decision, which then keeps it, or finds the room gone and joins
    // the table's room.
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
            var mark = Interlocked.Read(ref _changes);
            _openMarks.Add(mark);
            return new ConferenceSnapshotWindow(this, mark);
        }
    }

    internal void CloseSnapshotWindow(ConferenceSnapshotWindow window)
    {
        lock (_windowGate)
        {
            _openMarks.Remove(window.Mark);
            if (_openMarks.Count == 0)
            {
                _leftDuringRead.Clear();
                return;
            }

            var oldest = _openMarks.Min();
            foreach (var key in _leftDuringRead.Where(d => d.Value <= oldest).Select(static d => d.Key).ToList())
                _leftDuringRead.Remove(key);
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
    /// <item><description>A listed participant already held keeps its entry; its muted or talking value is updated
    /// only when the row's value is readable, raising <see cref="UserStateChanged"/> when it changed.</description></item>
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
                foreach (var held in room.Users.Values)
                {
                    if (keep?.Contains(held.Channel) == true)
                        continue;

                    if (held.JoinMark > window.Mark)
                    {
                        newer++;
                        continue;
                    }

                    RecordLeave(room.RoomNumber, held.Channel);
                    if (!room.Users.TryRemove(new KeyValuePair<string, MeetMeUser>(held.Channel, held)))
                        continue;

                    held.State = MeetMeUserState.Left;
                    MeetMeManagerLog.UserRemovedBySnapshot(_logger, room.RoomNumber, held.Channel);
                    removed++;
                    UserLeft?.Invoke(room, held);
                }

                RemoveIfEmpty(room);
            }
        }

        var admitted = 0;
        var leftDuringRead = 0;
        foreach (var (roomNumber, rows) in participants)
        {
            if (!rooms.Contains(roomNumber))
                continue;

            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Channel))
                    continue;

                MeetMeRoom? room;
                MeetMeUser? held;
                lock (_windowGate)
                {
                    if (_leftDuringRead.TryGetValue((roomNumber, row.Channel), out var stamp) && stamp > window.Mark)
                    {
                        leftDuringRead++;
                        continue;
                    }

                    var user = new MeetMeUser
                    {
                        UserNum = 0,
                        Channel = row.Channel,
                        UniqueId = row.UniqueId,
                        State = MeetMeUserState.Joined,
                        Muted = row.Muted ?? false,
                        Talking = row.Talking ?? false,
                        JoinMark = Interlocked.Increment(ref _changes),
                    };
                    while (true)
                    {
                        room = _rooms.GetOrAdd(roomNumber, static rn => new MeetMeRoom { RoomNumber = rn });
                        lock (room.SyncRoot)
                        {
                            // Taken out of the table after GetOrAdd returned it: admitted into the table's room instead.
                            if (room.RemovedFromTable)
                                continue;

                            held = room.Users.TryAdd(row.Channel, user) ? null : room.Users.GetValueOrDefault(row.Channel);
                            break;
                        }
                    }

                    if (held is null)
                        admitted++;
                }

                if (held is null)
                    continue;

                if (row.Muted is { } muted)
                    OnUserMuted(roomNumber, row.Channel, muted);
                if (row.Talking is { } talking)
                    OnUserTalking(roomNumber, row.Channel, talking);
            }
        }

        MeetMeManagerLog.Reconciled(_logger, rooms.Count, admitted, removed, newer, leftDuringRead, removeOmitted);
    }

    // Records a participant's departure for the open windows.
    private void RecordLeave(string roomNumber, string channel)
    {
        lock (_windowGate)
        {
            if (_openMarks.Count > 0)
                _leftDuringRead[(roomNumber, channel)] = Interlocked.Increment(ref _changes);
        }
    }

    /// <summary>
    /// Empties the conference table without raising anything. A reload no longer calls it: it reconciles the table
    /// against Asterisk's conference listing instead.
    /// </summary>
    public void Clear()
    {
        _rooms.Clear();
        lock (_windowGate)
            _leftDuringRead.Clear();
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.CloseSnapshotWindow(this);
    }
}

/// <summary>Represents a ConfBridge (or legacy MeetMe) conference room.</summary>
public sealed class MeetMeRoom
{
    internal readonly Lock SyncRoot = new();

    // Set, under SyncRoot, when the room is taken out of the manager's table; a participant that finds it set joins the
    // table's room instead.
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
