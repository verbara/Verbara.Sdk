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

    public event Action<MeetMeRoom, MeetMeUser>? UserJoined;
    public event Action<MeetMeRoom, MeetMeUser>? UserLeft;

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
    public void OnUserJoined(string roomNumber, int userNum, string channel, string? uniqueId)
    {
        if (string.IsNullOrEmpty(roomNumber) || string.IsNullOrEmpty(channel))
        {
            MeetMeManagerLog.EventNotApplied(_logger, "join", roomNumber, channel);
            return;
        }

        var room = _rooms.GetOrAdd(roomNumber, static rn => new MeetMeRoom { RoomNumber = rn });
        var user = new MeetMeUser
        {
            UserNum = userNum,
            Channel = channel,
            UniqueId = uniqueId,
            State = MeetMeUserState.Joined,
        };
        room.Users[channel] = user;
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

        if (_rooms.TryGetValue(roomNumber, out var room) && room.Users.TryRemove(channel, out var user))
        {
            user.State = MeetMeUserState.Left;
            MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
            UserLeft?.Invoke(room, user);
            if (room.Users.IsEmpty)
            {
                _rooms.TryRemove(new KeyValuePair<string, MeetMeRoom>(roomNumber, room));
            }
        }
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
            if (!room.Users.TryRemove(channel, out var user))
            {
                // A leave already removed this participant and raised its UserLeft.
                continue;
            }

            user.State = MeetMeUserState.Left;
            MeetMeManagerLog.UserLeft(_logger, roomNumber, user.Channel, user.UserNum);
            UserLeft?.Invoke(room, user);
        }

        _rooms.TryRemove(new KeyValuePair<string, MeetMeRoom>(roomNumber, room));
    }

    public void Clear() => _rooms.Clear();
}

/// <summary>Represents a ConfBridge (or legacy MeetMe) conference room.</summary>
public sealed class MeetMeRoom
{
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
    public bool Muted { get; set; }
    public bool Talking { get; set; }
}
