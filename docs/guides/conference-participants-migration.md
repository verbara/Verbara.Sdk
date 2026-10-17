# Migrating to conference participants keyed by channel (2.9.0)

Required by the release cadence: a minor that carries a breaking change ships a migration guide.

`VerbaraServer.MeetMe` tracks ConfBridge conferences from `ConfbridgeJoin`, `ConfbridgeLeave` and,
new in 2.9.0, `ConfbridgeEnd`. Up to 2.8.0 it keyed each room's participants by user number.
ConfBridge sends no user number, so every participant was recorded as user 0: a room with two
participants reported one, and the first participant to leave removed the room while the other was
still talking. 2.9.0 keys participants by channel name.

## What changed

Two public members change shape, in `Verbara.Sdk.Live` (`Verbara.Sdk.Live.MeetMe`):

| Member | 2.8.0 | 2.9.0 |
|---|---|---|
| `MeetMeRoom.Users` | `ConcurrentDictionary<int, MeetMeUser>`, keyed by user number | `ConcurrentDictionary<string, MeetMeUser>`, keyed by the channel name of each participant's join |
| `MeetMeManager.OnUserLeft` | `(string roomNumber, int userNum)` | `(string roomNumber, string channel)` |

Three members are added:

- `MeetMeUser.UniqueId` (`string?`): the `Uniqueid` of the participant's join.
- `MeetMeManager.OnUserJoined(string roomNumber, int userNum, string channel, string? uniqueId)`:
  the overload `VerbaraServer` now calls. The three-argument `OnUserJoined` keeps its signature and
  leaves `UniqueId` null.
- `MeetMeManager.OnConferenceEnded(string roomNumber)`: what `VerbaraServer` calls on
  `ConfbridgeEnd`.

What the table now does:

- **Every caller counts.** Two participants in one conference are two entries, and `UserCount` is 2.
- **The first leave no longer removes the room.** A leave removes only the participant whose channel
  it names. The room is removed when its last participant leaves.
- **`UserJoined` and `UserLeft` are raised once per participant.** In 2.8.0 a second participant's
  join replaced the first under key 0 and raised `UserJoined` again for the same entry, and only one
  `UserLeft` was ever raised for the room.
- **The end of a conference raises `UserLeft` for whoever is still held.** On `ConfbridgeEnd`, each
  participant still in the room is set to `Left` and `UserLeft` is raised once for it, then the room
  is removed. On the wire the last participant's `ConfbridgeLeave` arrives before `ConfbridgeEnd`, so
  usually nobody is left by then and the end only removes what remains. An end for a room that is not
  held changes nothing, and a later join for the same conference creates the room again.
- **A leave or join with no conference or no channel is not applied.** It is logged at `Debug` under
  the `[CONFERENCE]` tag.

## Who is affected

- Code that indexes `Users` by `int` (`room.Users[0]`, `Users.ContainsKey(n)`, `Users.Keys` read as
  numbers). It no longer compiles (`CS1503` for an `int` argument).
- Code that calls `OnUserLeft(string, int)`. It no longer compiles either.
- Code that relied on a room vanishing at the first leave, or on one `UserJoined`/`UserLeft` per
  room. It compiles and now sees every participant.
- A binary built against 2.8.0 that uses either changed member fails at run time with
  `MissingMethodException` when it reaches that call. Recompile against 2.9.0.

Code that only enumerates `Users.Values`, reads `UserCount`, `Rooms` or `GetRoom`, or subscribes to
`UserJoined`/`UserLeft` compiles unchanged and gets the right numbers.

## Before and after

Reading the participants. 2.8.0, keyed by user number:

<!-- skip-doc-snippet -->
```csharp
// 2.8.0: every ConfBridge participant sat under key 0
var room = server.MeetMe.GetRoom("100");
if (room is not null && room.Users.TryGetValue(0, out var user))
{
    Console.WriteLine($"{user.Channel} is in room 100");
}

server.MeetMe.OnUserLeft("100", 0);
```

2.9.0, keyed by channel:

```csharp
using Verbara.Sdk.Live.MeetMe;

static void ShowRoom(VerbaraServer server)
{
    var room = server.MeetMe.GetRoom("100");
    if (room is null)
    {
        return;
    }

    Console.WriteLine($"room 100 holds {room.UserCount} participants");
    foreach (var (channel, user) in room.Users)
    {
        Console.WriteLine($"{channel} joined with uniqueid {user.UniqueId}, state {user.State}");
    }

    if (room.Users.TryGetValue("PJSIP/1001-00000001", out var one))
    {
        Console.WriteLine($"{one.Channel} is in room 100");
    }
}
```

Recording a leave yourself (only if you feed a `MeetMeManager` outside `VerbaraServer`), with the
`Channel` of the `ConfbridgeLeave`:

```csharp
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.MeetMe;

static void Apply(MeetMeManager meetMe, ConfbridgeLeaveEvent leave)
{
    meetMe.OnUserLeft(leave.Conference ?? "", leave.Channel ?? "");
}
```

Counting with the events instead of the table:

```csharp
using Verbara.Sdk.Live.MeetMe;

static void Watch(VerbaraServer server)
{
    server.MeetMe.UserJoined += (room, user) =>
        Console.WriteLine($"{user.Channel} joined {room.RoomNumber}; {room.UserCount} now");
    server.MeetMe.UserLeft += (room, user) =>
        Console.WriteLine($"{user.Channel} left {room.RoomNumber}; {room.UserCount} remain");
}
```

## `UserNum` and `UniqueId`

- **`UserNum` is `0` for every ConfBridge participant.** `ConfbridgeJoin` carries no user number
  (no `Usernum` header on Asterisk 20, 22 or 23), so there is nothing else to store. It is kept as
  what Asterisk sent and never identifies a participant. The key is the channel.
- **`UniqueId` is the join's `Uniqueid`, as data only.** It never identifies, replaces or removes a
  participant: a leave is matched by channel. It is null when the join carried none, or when the
  participant was recorded through the three-argument `OnUserJoined`.
- A second join for a channel already held in the room replaces that participant and raises
  `UserJoined` again; it does not add a second entry.

## A participant whose leave names another channel

A leave removes only the participant whose join named the same channel. If a participant's leave
named a different channel, that participant would stay held, counted, and keeping its room alive.

What was measured on Asterisk 20.20.1, 22.10.1 and 23.4.1, with Local channels: every
`ConfbridgeLeave` (`ConfbridgeKick` of one participant, `ConfbridgeKick` of all, and a `Redirect`
out of the conference) named the channel and `Uniqueid` of the participant's own join. A participant
reached through a Local channel's `Dial` kept its name from join to leave. Asterisk refuses to move a
ConfBridge participant with `Action: Bridge` (`Unable to add Channel1 to bridge`), and no `Rename` or
masquerade frame reached a participant on any exit those versions allowed.

What was not measured: a masquerade that does take a participant out of a conference, and SIP
participants (attended transfer, call pickup). If one of those produced a leave under another channel
name, the participant would stay held until `ConfbridgeEnd` for its conference removes it, raising
`UserLeft` then. If that end is missed too, for example while the AMI connection is down, the
reconnect clears it (next section).

## Reconnect

A reconnect still clears the whole conference table and raises no `UserLeft` for what it drops. The
conference table is not reloaded from Asterisk afterwards: a participant who was already in a
conference is held again only when it joins again, and a leave for a participant that is not held
changes nothing. If you keep state per participant, reset it when the connection comes back, as you
did in 2.8.0.

## Package validation against 2.8.0

If you run package validation with 2.8.0 as the baseline, `Verbara.Sdk.Live` reports two `CP0002`:

- `M:Verbara.Sdk.Live.MeetMe.MeetMeRoom.get_Users`
- `M:Verbara.Sdk.Live.MeetMe.MeetMeManager.OnUserLeft(System.String,System.Int32)`

Both are the intended breaks above. Package validation reports nothing for the added members,
`UserStateChanged` among them.

## Muted and Talking follow Asterisk

From this release `MeetMeUser.Muted` and `MeetMeUser.Talking` are set; before, both always read
`false`.

- `Muted` is set from the join's `Muted` header (an absent or unreadable value reads `false`), before
  `UserJoined` is raised, then by each `ConfbridgeMute` and `ConfbridgeUnmute` naming the
  participant. It is the participant's mute request as Asterisk reports it, not whether the
  participant is audible. A whole-room mute from the conference keypad sends no event; a reload reads
  the real state.
- `Talking` follows `ConfbridgeTalking`. Asterisk sends it only for a user profile with
  `talk_detection_events=yes`; without it `Talking` stays `false`.
- A mute, unmute or talking event for a participant the table does not hold changes nothing.
- `MeetMeManager.UserStateChanged` is raised once when a participant's `Muted` or `Talking` changes;
  not for an event that leaves the value as it was, and not on the join.

If you read `ConfbridgeMute`, `ConfbridgeUnmute` or `ConfbridgeTalking` yourself to fill that state,
you can read the participant instead.

## What did not change

- `Rooms`, `GetRoom`, `UserCount`, `Clear`, `UserJoined` and `UserLeft` keep their signatures.
  `Clear` still raises nothing.
- The type names stay `MeetMe*`: they track ConfBridge conferences.
