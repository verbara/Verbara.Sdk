# Upgrading from 2.11.x to 2.12.0

This is the one migration guide for 2.12.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Live` | [The MeetMe names of the conference table are removed](#the-meetme-names-of-the-conference-table-are-removed) | removed types and member, BREAKING |
| `Verbara.Sdk.Live` | [`ConferenceManager` is sealed](#conferencemanager-is-sealed) | sealed class, BREAKING |
| `Verbara.Sdk.Live` | [The `live.server` span tag is removed](#the-liveserver-span-tag-is-removed) | span tag removed (no compiler error) |

## Before you start: move every package at once

Move every `Verbara.Sdk.*` package to 2.12.0 together, then recompile. The packages are built and tested as one
release; a 2.11.x package loaded next to a 2.12.0 one is not a combination anything verifies.

## Deprecated surface removed

2.11.0 printed "Removed in Verbara.Sdk 2.12.0." in the warning of each symbol below, and linked the warning to its
id's heading in the [2.11.0 guide](migration-2.11.0.md#vsdk0008). Those symbols are gone. `VSDK0008` is retired:
nothing in the SDK carries it any more, so a `NoWarn` or `#pragma warning disable VSDK0008` you kept for it suppresses
nothing and can be deleted.

A binary built against 2.11.x or earlier that names a removed symbol fails when it reaches it, with
`MissingMethodException` for the removed property and `TypeLoadException` for a removed type, until it is recompiled
against 2.12.0 and the use is moved as below.

### The MeetMe names of the conference table are removed

**What changed.** The five former names of the Live conference table are removed (marked with `VSDK0008` since
2.11.0). The table tracks ConfBridge, and since 2.11.0 it has had names that say so; the former names reached the same
objects. What each removed name is now:

| Removed name | Use instead |
|---|---|
| `VerbaraServer.MeetMe` | `VerbaraServer.Conferences`, the same instance it returned |
| `Verbara.Sdk.Live.MeetMe.MeetMeManager` | `Verbara.Sdk.Live.Conferences.ConferenceManager` |
| `Verbara.Sdk.Live.MeetMe.MeetMeRoom` | `Verbara.Sdk.Live.Conferences.ConferenceRoom` |
| `Verbara.Sdk.Live.MeetMe.MeetMeUser` | `Verbara.Sdk.Live.Conferences.ConferenceUser` |
| `Verbara.Sdk.Live.MeetMeUserState` | `Verbara.Sdk.Live.ConferenceUserState` (`Joined`, `Left`, same values) |

The namespace `Verbara.Sdk.Live.MeetMe` is gone with them. Nothing changes at run time for code that already uses the
conference names: the events, their order, the `[CONFERENCE]` log lines and the `live.meetme.subscriber_faults`
counter, which keeps its published name, are what 2.11.0 produced.

The differences 2.11.0 listed between the two sets of names still hold, and are now the only behaviour there is:
`ConferenceUserState` has no `Talking` (read `ConferenceUser.Talking`), `ConferenceRoom.Users` is a read-only view,
the `ConferenceUser` properties have no public setter, and neither type has a public constructor.

**Who is affected.** Code that names one of the five symbols. A read of `server.MeetMe` reports `CS1061`
(`'VerbaraServer' does not contain a definition for 'MeetMe'`); a fully qualified former type reports `CS0234`, and a
`using Verbara.Sdk.Live.MeetMe;` with an unqualified type reports `CS0234` on the directive and `CS0246` on the type.
Code that already moved to the conference names in 2.11.0 needs no change.

**What to do.** Move to the conference names. Before (2.11.x: warned `VSDK0008`; does not compile against 2.12.0):

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Live.MeetMe;

server.MeetMe.UserJoined += (MeetMeRoom room, MeetMeUser user) => Console.WriteLine($"{user.Channel} joined {room.RoomNumber}");
var joined = server.MeetMe.GetRoom("100")?.Users.Values.Count(u => u.State == MeetMeUserState.Joined) ?? 0;
```

After:

```csharp
using Verbara.Sdk.Live;
using Verbara.Sdk.Live.Conferences;
using Verbara.Sdk.Live.Server;

static int Watch(VerbaraServer server)
{
    server.Conferences.UserJoined += (ConferenceRoom room, ConferenceUser user) =>
        Console.WriteLine($"{user.Channel} joined {room.RoomNumber}");
    return server.Conferences.GetRoom("100")?.Users.Values.Count(u => u.State == ConferenceUserState.Joined) ?? 0;
}
```

Code that wrote `MeetMeRoom.Users` or a `MeetMeUser` setter has no replacement: it changed the table without Live
knowing. To feed a table yourself, construct a `ConferenceManager` and call its `OnUserJoined`, `OnUserLeft` and
`OnConferenceEnded`.

### `ConferenceManager` is sealed

**What changed.** `ConferenceManager` is declared `sealed`. It has no virtual or protected member a derived class could
use, so a subclass could only add members of its own; its rooms and participants are created by the manager alone.

**Who is affected.** Code that declares a class deriving from `ConferenceManager`. It reports `CS0509`
(`'CustomConferences': cannot derive from sealed type 'ConferenceManager'`). A binary built against 2.11.x that holds
such a class fails with `TypeLoadException` when the class is loaded. Code that only constructs, subscribes to or calls
a `ConferenceManager` is not affected.

**What to do.** Compose instead of deriving: hold the manager, and put the members your subclass added on the type
that holds it, or on extension methods. Before (2.11.x; does not compile against 2.12.0):

<!-- skip-doc-snippet -->
```csharp
public sealed class CountingConferences(ILogger logger) : ConferenceManager(logger)
{
    public int JoinedIn(string room) => GetRoom(room)?.UserCount ?? 0;
}
```

After:

```csharp
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Live.Conferences;

static int Joined(ConferenceManager conferences, string room) => conferences.JoinedIn(room);

public static class ConferenceManagerExtensions
{
    public static int JoinedIn(this ConferenceManager conferences, string room) =>
        conferences.GetRoom(room)?.UserCount ?? 0;
}

public sealed class CountingConferences(ILogger logger)
{
    public ConferenceManager Conferences { get; } = new(logger);

    public int JoinedIn(string room) => Conferences.GetRoom(room)?.UserCount ?? 0;
}
```

For the server's own table, use the extension on `server.Conferences`: the server creates that manager, and a
subclass of it was never what the server held.

## Span tags

### The `live.server` span tag is removed

**What changed.** The `live channel-reconcile` and `live state-load` spans no longer carry `live.server`. The tag held
the Asterisk version, not a server identity, and 2.11.0 marked it obsolete. The spans keep `server.id`, the pool id,
which 2.11.0 added on `live channel-reconcile`, `live state-load` and `live originate` when the server is in a
`VerbaraServerPool`; a standalone server's spans carry neither tag. The `live.server.subscriber_faults` counter is a
meter instrument, not this tag, and is unchanged.

**Who is affected.** A dashboard, query or alert that reads `live.server`. Nothing reports a compiler error and no
public symbol changes: against 2.12.0 the tag is simply absent, so a filter on it matches no span, and a group-by on it
puts every span in one empty bucket.

**What to do.** Filter and group on `server.id` to tell pooled servers apart. If you read `live.server` for the
Asterisk version, read `VerbaraServer.AsteriskVersion` instead, or record it on your own resource attributes.
