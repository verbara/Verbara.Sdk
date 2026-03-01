using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.MeetMe;

/// <summary>
/// Tracks MeetMe/ConfBridge conference rooms in real-time.
/// </summary>
public sealed class MeetMeManager
{
    private readonly ConcurrentDictionary<string, MeetMeRoom> _rooms = new();
    private readonly ILogger _logger;

    public event Action<MeetMeRoom, MeetMeUser>? UserJoined;
    public event Action<MeetMeRoom, MeetMeUser>? UserLeft;

    public MeetMeManager(ILogger logger) => _logger = logger;

    public IReadOnlyCollection<MeetMeRoom> Rooms => _rooms.Values.ToList().AsReadOnly();

    public MeetMeRoom? GetRoom(string roomNumber) => _rooms.GetValueOrDefault(roomNumber);

    public void OnUserJoined(string roomNumber, int userNum, string channel)
    {
        var room = _rooms.GetOrAdd(roomNumber, _ => new MeetMeRoom { RoomNumber = roomNumber });
        var user = new MeetMeUser { UserNum = userNum, Channel = channel, State = MeetMeUserState.Joined };
        room.Users[userNum] = user;
        UserJoined?.Invoke(room, user);
    }

    public void OnUserLeft(string roomNumber, int userNum)
    {
        if (_rooms.TryGetValue(roomNumber, out var room) && room.Users.TryRemove(userNum, out var user))
        {
            user.State = MeetMeUserState.Left;
            UserLeft?.Invoke(room, user);
            if (room.Users.IsEmpty)
            {
                _rooms.TryRemove(roomNumber, out _);
            }
        }
    }

    public void Clear() => _rooms.Clear();
}

/// <summary>Represents a MeetMe/ConfBridge conference room.</summary>
public sealed class MeetMeRoom
{
    public string RoomNumber { get; init; } = string.Empty;
    public ConcurrentDictionary<int, MeetMeUser> Users { get; } = new();
    public int UserCount => Users.Count;
}

/// <summary>Represents a user in a conference room.</summary>
public sealed class MeetMeUser
{
    public int UserNum { get; init; }
    public string Channel { get; init; } = string.Empty;
    public MeetMeUserState State { get; set; }
    public bool Muted { get; set; }
    public bool Talking { get; set; }
}
