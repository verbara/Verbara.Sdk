using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Conferences;

namespace Verbara.Sdk.Live.MeetMe;

/// <summary>
/// The former name of <see cref="ConferenceManager"/>: the same manager, whose events, rooms and participants carry
/// the former type names. <see cref="Server.VerbaraServer.MeetMe"/> and <see cref="Server.VerbaraServer.Conferences"/>
/// return the same instance.
/// </summary>
/// <remarks>
/// A handler subscribed here and one subscribed to the same event of <see cref="ConferenceManager"/> are raised in one
/// subscription order, each once per event, each in its own guard; each handler of a multicast delegate subscribed here
/// is one subscription with its own guard. Removing a delegate here does what <see cref="Delegate.Remove"/> did on the
/// field-like events of 2.10.x, over the delegates subscribed to the same event under this name: its handlers are
/// removed only as the last contiguous run of those subscriptions, and nothing is removed when no run matches.
/// </remarks>
[Obsolete(
    "Renamed to Verbara.Sdk.Live.Conferences.ConferenceManager, the same manager: VerbaraServer.Conferences returns this instance, and its events carry ConferenceRoom and ConferenceUser. Removed in Verbara.Sdk 2.12.0.",
    DiagnosticId = "VSDK0008",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0008")]
public sealed class MeetMeManager : ConferenceManager
{
    private const int Joined = 0;
    private const int Left = 1;
    private const int StateChanged = 2;

    private readonly Lock _subscriptionGate = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <inheritdoc cref="ConferenceManager(ILogger)"/>
    public MeetMeManager(ILogger logger)
        : base(logger)
    {
    }

    /// <inheritdoc cref="ConferenceManager.UserJoined"/>
    public new event Action<MeetMeRoom, MeetMeUser>? UserJoined
    {
        add => Subscribe(Joined, value);
        remove => Unsubscribe(Joined, value);
    }

    /// <inheritdoc cref="ConferenceManager.UserLeft"/>
    public new event Action<MeetMeRoom, MeetMeUser>? UserLeft
    {
        add => Subscribe(Left, value);
        remove => Unsubscribe(Left, value);
    }

    /// <inheritdoc cref="ConferenceManager.UserStateChanged"/>
    public new event Action<MeetMeRoom, MeetMeUser>? UserStateChanged
    {
        add => Subscribe(StateChanged, value);
        remove => Unsubscribe(StateChanged, value);
    }

    /// <inheritdoc cref="ConferenceManager.Rooms"/>
    public new IReadOnlyCollection<MeetMeRoom> Rooms => MeetMeRooms;

    /// <inheritdoc cref="ConferenceManager.GetRoom(string)"/>
    public new MeetMeRoom? GetRoom(string roomNumber) => GetMeetMeRoom(roomNumber);

    /// <inheritdoc cref="ConferenceManager.OnUserJoined(string, int, string)"/>
    public new void OnUserJoined(string roomNumber, int userNum, string channel) =>
        base.OnUserJoined(roomNumber, userNum, channel);

    /// <inheritdoc cref="ConferenceManager.OnUserJoined(string, int, string, string?)"/>
    public new void OnUserJoined(string roomNumber, int userNum, string channel, string? uniqueId) =>
        base.OnUserJoined(roomNumber, userNum, channel, uniqueId);

    /// <inheritdoc cref="ConferenceManager.OnUserLeft(string, string)"/>
    public new void OnUserLeft(string roomNumber, string channel) => base.OnUserLeft(roomNumber, channel);

    /// <inheritdoc cref="ConferenceManager.OnConferenceEnded(string)"/>
    public new void OnConferenceEnded(string roomNumber) => base.OnConferenceEnded(roomNumber);

    /// <inheritdoc cref="ConferenceManager.Clear"/>
    public new void Clear() => base.Clear();

    // Each handler of the delegate becomes one subscription of the conference event, so it keeps its place in the one
    // subscription order and its own fault guard. Every room and participant the manager raises is a MeetMe instance.
    private void Subscribe(int kind, Action<MeetMeRoom, MeetMeUser>? value)
    {
        if (value is null)
            return;

        lock (_subscriptionGate)
        {
            foreach (var handler in Delegate.EnumerateInvocationList(value))
            {
                Action<ConferenceRoom, ConferenceUser> adapter = (room, user) => handler((MeetMeRoom)room, (MeetMeUser)user);
                _subscriptions.Add(new Subscription(kind, handler, adapter));
                Attach(kind, adapter);
            }
        }
    }

    // Delegate.Remove over the event's own subscriptions, as the field-like event of 2.10.x removed: the delegate's
    // handlers are removed only as the last contiguous run of this event's former-name subscriptions (a subscription of
    // another event, or one made under the conference name, does not break a run), and nothing when no run matches.
    private void Unsubscribe(int kind, Action<MeetMeRoom, MeetMeUser>? value)
    {
        if (value is null)
            return;

        lock (_subscriptionGate)
        {
            if (value.HasSingleTarget)
            {
                var index = LastIndexOf(kind, value, _subscriptions.Count - 1);
                if (index >= 0)
                    Remove(kind, index);

                return;
            }

            var handlers = value.GetInvocationList();
            var last = handlers.Length - 1;
            for (var end = LastIndexOf(kind, handlers[last], _subscriptions.Count - 1); end >= 0;
                 end = LastIndexOf(kind, handlers[last], end - 1))
            {
                // Walks back from the run's last handler over this event's subscriptions only; on a match, the indexes
                // of the run are those of this event's subscriptions from the run's first to `end`.
                var first = end;
                var matched = 1;
                for (var i = end - 1; i >= 0 && matched < handlers.Length; i--)
                {
                    var subscription = _subscriptions[i];
                    if (subscription.Kind != kind)
                        continue;

                    if (!subscription.Handler.Equals(handlers[last - matched]))
                        break;

                    first = i;
                    matched++;
                }

                if (matched < handlers.Length)
                    continue;

                for (var i = end; i >= first; i--)
                {
                    if (_subscriptions[i].Kind == kind)
                        Remove(kind, i);
                }

                return;
            }
        }
    }

    // Under _subscriptionGate. The index of this event's last subscription of the handler at or before `from`, or -1.
    private int LastIndexOf(int kind, Delegate handler, int from)
    {
        for (var i = from; i >= 0; i--)
        {
            var subscription = _subscriptions[i];
            if (subscription.Kind == kind && subscription.Handler.Equals(handler))
                return i;
        }

        return -1;
    }

    // Under _subscriptionGate.
    private void Remove(int kind, int index)
    {
        Detach(kind, _subscriptions[index].Adapter);
        _subscriptions.RemoveAt(index);
    }

    private void Attach(int kind, Action<ConferenceRoom, ConferenceUser> adapter)
    {
        switch (kind)
        {
            case Joined:
                base.UserJoined += adapter;
                break;
            case Left:
                base.UserLeft += adapter;
                break;
            default:
                base.UserStateChanged += adapter;
                break;
        }
    }

    private void Detach(int kind, Action<ConferenceRoom, ConferenceUser> adapter)
    {
        switch (kind)
        {
            case Joined:
                base.UserJoined -= adapter;
                break;
            case Left:
                base.UserLeft -= adapter;
                break;
            default:
                base.UserStateChanged -= adapter;
                break;
        }
    }

    private sealed record Subscription(int Kind, Action<MeetMeRoom, MeetMeUser> Handler, Action<ConferenceRoom, ConferenceUser> Adapter);
}

/// <summary>The former name of <see cref="ConferenceRoom"/>: every room <see cref="ConferenceManager"/> holds is one.</summary>
[Obsolete(
    "Renamed to Verbara.Sdk.Live.Conferences.ConferenceRoom: every room the manager reports is the same object under both names, and ConferenceRoom.Users is a read-only view of this room's Users. Removed in Verbara.Sdk 2.12.0.",
    DiagnosticId = "VSDK0008",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0008")]
public sealed class MeetMeRoom : ConferenceRoom
{
    /// <summary>Creates a room no manager holds.</summary>
    public MeetMeRoom()
    {
    }

    /// <inheritdoc cref="ConferenceRoom.RoomNumber"/>
    public new string RoomNumber
    {
        get => base.RoomNumber;
        init => base.RoomNumber = value;
    }

    /// <summary>
    /// The participants still held in the room, keyed by the channel name Asterisk sent on each
    /// participant's join. One entry per channel. The same table <see cref="ConferenceRoom.Users"/> reads; a change made
    /// through this dictionary is outside the manager's synchronization.
    /// </summary>
    public new ConcurrentDictionary<string, MeetMeUser> Users => Table;

    /// <inheritdoc cref="ConferenceRoom.UserCount"/>
    public new int UserCount => base.UserCount;
}

/// <summary>The former name of <see cref="ConferenceUser"/>: every participant <see cref="ConferenceManager"/> holds is one.</summary>
[Obsolete(
    "Renamed to Verbara.Sdk.Live.Conferences.ConferenceUser: every participant the manager reports is the same object under both names, and ConferenceUser.State reads ConferenceUserState. Removed in Verbara.Sdk 2.12.0.",
    DiagnosticId = "VSDK0008",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0008")]
public sealed class MeetMeUser : ConferenceUser
{
    /// <summary>Creates a participant no room holds.</summary>
    public MeetMeUser()
    {
    }

    /// <inheritdoc cref="ConferenceUser.UserNum"/>
    public new int UserNum
    {
        get => base.UserNum;
        init => base.UserNum = value;
    }

    /// <inheritdoc cref="ConferenceUser.Channel"/>
    public new string Channel
    {
        get => base.Channel;
        init => base.Channel = value;
    }

    /// <inheritdoc cref="ConferenceUser.UniqueId"/>
    public new string? UniqueId
    {
        get => base.UniqueId;
        init => base.UniqueId = value;
    }

    /// <summary>
    /// <see cref="ConferenceUser.State"/> under the former enum, with the same values. A value set here is what
    /// <see cref="ConferenceUser.State"/> reads; <see cref="MeetMeUserState.Talking"/> has no
    /// <see cref="ConferenceUserState"/> member.
    /// </summary>
    public new MeetMeUserState State
    {
        get => (MeetMeUserState)base.State;
        set => base.State = (ConferenceUserState)value;
    }

    /// <inheritdoc cref="ConferenceUser.Muted"/>
    public new bool Muted
    {
        get => base.Muted;
        set => base.Muted = value;
    }

    /// <inheritdoc cref="ConferenceUser.Talking"/>
    public new bool Talking
    {
        get => base.Talking;
        set => base.Talking = value;
    }

    /// <inheritdoc cref="ConferenceUser.LeaveNotObserved"/>
    public new bool LeaveNotObserved => base.LeaveNotObserved;
}
