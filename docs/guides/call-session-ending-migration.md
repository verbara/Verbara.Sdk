# Migrating to the 2.7.0 call-session endings

`Verbara.Sdk.Sessions` 2.7.0 changes how some calls end. Nothing stops compiling and no public API is added or
removed, but the state, the connected time and the talk time of three kinds of call move:

- a call that was **answered with no dial or queue** (an IVR, voicemail, a Voice AI bot reached after `Answer()`,
  an originate whose dialplan neither dials nor queues) now ends `Completed`, with a connected time and a talk
  time, where it ended `Failed` without them;
- a **queued call that no member took** no longer passes through `Connected` when a member's leg answers or joins
  the bridge: it ends `Failed` with no connected time and no talk time;
- a **queued call a member took** is connected when the queue application reports the connection, no longer when
  the member's leg answers.

The reconciliation sweep's treatment of long answered calls also changes in this release; see
[the sweep](#the-reconciliation-sweep-and-long-answered-calls). In 2.8.0 the multi-server registrations run that sweep
for every server of the pool; see [the sweep on a multi-server host](#the-sweep-on-a-multi-server-host-280).

Dialed calls, calls that were never answered, and queued calls a member took end exactly as before: same state,
same cause, same domain events.

## What you have to do

1. **Dashboards and alerts on `sessions.failed` and `sessions.completed`.** The answered-with-no-dial calls move
   from the first to the second, and they now record a `sessions.talk_time` sample. An alert tuned to the old
   failure rate of an IVR-heavy deployment fires less; an answer rate computed as
   `completed / (completed + failed)` rises. See [a call answered with no dial](#a-call-answered-with-no-dial).
2. **Code that reads `CallSession.State == Failed` (or `CallEndedEvent` with no `TalkTime`) as "nobody talked".**
   Such calls now end `Completed`. If you need to tell them from a dialed call, read the audit trail: see
   [telling the two apart](#telling-an-answered-call-from-a-dialed-one).
3. **Reports of queued calls that read `ConnectedAt`, `WaitTime` or `TalkTime`.** A queued call that no member took
   loses the connected time and talk time it reported; a queued call a member took is connected at the queue's
   report. See [queued calls](#queued-calls).
4. **The AMI user the SDK logs in with.** It must read the `agent` class for a queued call to connect at all. See
   [the `agent` read class](#the-agent-read-class).

## A call answered with no dial

A call is in this case when the SDK saw one of its channels answer (`Newstate` to `Up`) while the session was still
in its initial state, `Created`, and nothing then dialed it onward or queued it. Asterisk sends this shape for:

- an inbound call the dialplan answers and plays, records or hangs up (IVR, voicemail, announcements);
- a Voice AI call the dialplan answers and then hands to the `AudioSocket()` application or to Stasis
  (`Dial(AudioSocket/…)` is a dial, and already ended `Completed`);
- an AMI `Originate` whose far end answers and whose dialplan or application neither dials nor queues, whether the
  originate targets an endpoint or a `Local` channel (a voice broadcast, for example).

When such a call hangs up with `NormalClearing`:

| | Before | Now |
|---|---|---|
| `CallSession.State` | `Failed` | `Completed` |
| `CallSession.HangupCause` | `NormalClearing` | `NormalClearing` |
| `CallSession.ConnectedAt` | `null` | the time the SDK observed the answer |
| `CallSession.WaitTime` | `null` | from the call's creation to the observed answer |
| `CallSession.TalkTime`, `CallEndedEvent.TalkTime` | `null` | from the observed answer to the hangup, less any hold |
| metrics | `sessions.failed` +1 | `sessions.completed` +1, one `sessions.talk_time` sample |
| `CallConnectedEvent` | none | none |
| audit trail (`CallSession.Events`) | no `Connected` entry | no `Connected` entry |

With any other hangup cause the call still ends `Failed`, the rule a connected call follows. A call whose answer the
SDK never observed ends `Failed`, as before, with no connected time and no talk time; that includes a call the SDK
first sees with its leg already up, since that is not an observed answer.

**While the call is live nothing changes.** The answer is recorded inside the session and read only when the call
ends: a live call answered with no dial reads `State == Created` with `ConnectedAt == null`, no `CallConnectedEvent`
is published, no metric is recorded and the session store is not handed the session at the answer. If the call is
then queued or dialed, it follows the queued or dialed flow exactly as before, and its connected time comes from the
connection it reaches there, not from the dialplan's answer.

**Ended by a state reload.** When an AMI reconnect's state reload proves such a call gone (its channels are no longer
listed), it now ends `Completed`, the way a reload ends a connected call: `Metadata["cause"] == "reload"`, no
`HangupCause`, and its connected time is the answer the SDK observed. Before, it ended `Failed`. A call whose answer
the SDK never observed still ends `Failed` on a reload.

**Measured** on 2026-10-02 against Asterisk 20.20.1, 22.9.0 and 23.4.1, with a host wired as a consumer wires it
(`AddVerbara` and `AddVerbaraSessions`), one call per shape and version: an IVR the PBX hangs up 2.5 s after
answering, one the caller hangs up after 3 s, an originate to an endpoint that answers after 1 s of ringing and is
hung up 2 s later, and an originate to a `Local` channel running that IVR. Each ended `Failed` with no talk time
before, and `Completed` with a talk time of 2.0–3.0 s now, the time from its answer to its hangup. The dialed,
queued and unanswered calls run in the same harness ended with the same state, cause and domain events as before.

### Telling an answered call from a dialed one

Both end `Completed`. A dialed call's audit trail (`CallSession.Events`) holds a `Dialing` entry and a `Connected`
entry; a call answered with no dial holds neither: its trail goes from `Created` to the participants leaving.

```csharp
bool answeredWithNoDial = session.State == CallSessionState.Completed
    && !session.Events.Any(e => e.Type is CallSessionEventType.Connected or CallSessionEventType.Dialing);
```

## Queued calls

A call waiting in a queue (state `Queued`) is now connected **only** when Asterisk's queue application reports the
connection: the AMI `AgentConnect` event, the same frame `CallConnectedEvent` is published from. Before, the first of
four signals connected it: a member leg answering, a member leg entering the bridge, the caller's `DialEnd` with
`ANSWER`, or `AgentConnect`. A member's leg can answer, or enter the bridge, without the queue application ever
connecting the call: a pooled agent that must acknowledge each call and never does, or a member whose connect-time
gosub rejects the call.

### A queued call no member took

| | Before | Now |
|---|---|---|
| `CallSession.State` | `Failed` in every captured shape, but `Completed` when the last of its channels hung up with `NormalClearing` | `Failed`, whatever the cause |
| `CallSession.QueuedAt` | set | set |
| `CallSession.ConnectedAt`, `WaitTime` | set, at the member leg's answer or bridge entry | `null` |
| `CallSession.TalkTime`, `CallEndedEvent.TalkTime` | set | `null` |
| `sessions.talk_time` | one sample | none |
| `CallConnectedEvent` | none | none |
| audit trail | a `Connected` entry from the member leg | no `Connected` entry other than the dial outcomes (`Detail` = `dial:<status>`), which it recorded before too |

### A queued call a member took

It still passes through `Queued` to `Connected` and ends `Completed`, with one `CallConnectedEvent`. Its
`ConnectedAt` is now the queue application's `AgentConnect` — the same instant `CallConnectedEvent` carries — no
longer the member leg's answer. `WaitTime` and `TalkTime` follow from it: when the queue application connects the
member as soon as the member answers, the two instants are a frame apart: measured on 2026-10-02 against Asterisk
20.20.1, 22.9.0 and 23.4.1 with a member that answers at once, a taken call's wait and talk times differed from the
earlier code's by no more than the run-to-run variation (at most 25 ms). When something runs between the member's
answer and the connection, such as an acknowledgement by a pooled agent or a gosub run on the member's channel,
`WaitTime` now includes that interval and `TalkTime` no longer does.

The audit trail of a taken call loses the bare `Connected` entry the member leg's answer used to write; its
`AgentConnected` entry marks the connection, and the `Connected` entry with `Detail` = `dial:ANSWER` is still
recorded.

### The `agent` read class

Asterisk sends its queue events, `AgentConnect` among them, in the AMI `agent` class. An AMI user that does not read
that class never receives the report this change waits for, so the SDK cannot connect a queued call; the same class
was already needed for the SDK to see a call join a queue and to publish `CallConnectedEvent` for it. Grant the
class (`read = all`, or a list that includes `agent`); see
[troubleshooting § Missing events](troubleshooting.md#missing-events).

## The reconciliation sweep and long answered calls

`AddVerbaraSessions` registers a sweep that runs every `SessionOptions.ReconciliationInterval` (30 s by default).
Before 2.7.0 it ended calls by their age alone: a call still dialing after `DialingTimeout` (60 s) or ringing after
`RingingTimeout` (120 s) became `TimedOut`, and a call still in `Created` after `DialingTimeout` became `Failed` with
`Metadata["cause"] == "orphaned"`, whether or not Asterisk still had it. A call answered with no dial (an IVR, a
voicemail, a Voice AI bot) is in `Created` for its whole life, so every such call that lasted longer than 60 to 90 s
was marked ended while it was still up. The sweep's ending also reported nothing at the time: no `CallEndedEvent` until the
call's legs left, and none ever if they never did.

Since 2.7.0 the clock only decides **when to ask**. When a held call is older than `DialingTimeout` and the channel
table holds one of its channels, the sweep asks Asterisk once for its channel snapshot (`Status`) and reconciles the
channels against it (`VerbaraServer.ReconcileChannelsAsync`, new in `Verbara.Sdk.Live`). A call whose channels Asterisk
still reports is left exactly as it is, whatever its age or state. A call whose channels it no longer reports — a call
whose hangup the SDK never received — ends the way a reconnect's reload ends it.

### What changes

| | Before | Now |
|---|---|---|
| A call Asterisk still has, older than the timeouts (a long IVR, a long ring, a long dial) | ended by the sweep: `TimedOut`, or `Failed` with `cause=orphaned`; its `Duration` cut at the sweep | untouched; it ends at its hangup, with that hangup's state, cause, talk time and duration |
| A call that rang past the timeouts and was then answered | `TimedOut`, no talk time, no `CallConnectedEvent` | `Completed` with its talk time, as any answered call |
| A call whose hangup was lost (Asterisk dropped it, the SDK never saw it go) | held for the life of the process, still active in a durable store; a connected call was never ended | ended at the first sweep after Asterisk dropped it: `Completed` if it was answered, `Failed` otherwise, no `HangupCause`, `Metadata["cause"] == "reload"`, one `CallEndedEvent`, saved to the store and released after `CompletedRetention` |
| A queued call whose hangup was lost | held | ended as above (`Failed`), and its queue visit counted abandoned once |
| `sessions.timed_out`, `sessions.orphaned` | moved by the sweep (`sessions.timed_out` twice per call) | never move; a call the sweep ends is counted once, in `sessions.completed` or `sessions.failed` |

**How late.** A call whose hangup was lost ends at the first sweep after Asterisk dropped it once it is older than
`DialingTimeout`: at worst `DialingTimeout` plus one interval after the call was opened, 90 s with the defaults.

**Kept, no longer produced.** `CallSessionState.TimedOut`, `CallSessionEventType.TimedOut`,
`SessionMetrics.SessionsTimedOut` and `SessionMetrics.SessionsOrphaned` stay, so that code, dashboards and sessions
stored by earlier versions keep binding; the SDK no longer produces them, and the stores still read `TimedOut` as an
ended state. `SessionOptions.RingingTimeout` is not read any more and is kept so that configuration keeps binding. A
session read back from a store may still carry `cause=orphaned`; the SDK no longer writes it.

### What you have to do

1. **The AMI user needs `Status`.** Put `system`, `call` or `reporting` in its `write` line in `manager.conf`
   (`write = all` includes them). Without it Asterisk refuses the snapshot, the sweep ends nothing, and the server logs
   `[LIVE] Status refused: …` at Warning once per AMI session (Debug after that).
2. **Dashboards and alerts on `sessions.timed_out` or `sessions.orphaned`.** They stay at zero. A call whose hangup
   was lost now shows up in `sessions.completed` or `sessions.failed`, with `Metadata["cause"] == "reload"` and no
   `HangupCause`; a consumer cannot tell it from a call a reconnect's reload ended, by design.
3. **Code that reads `TimedOut` or `cause=orphaned` as "the call went too long".** Long calls are no longer cut; read
   `Duration`, `RingingAt` and `ConnectedAt` instead.

### When the sweep does not verify

- **The connection is not established**, or it **does not report how an action ended**
  (`IAmiConnection.ReportsEventActionOutcome` is false): without that the sweep could not tell a refused snapshot from an
  empty one, which would end every call. `AmiConnection` reports it; a wrapper of your own must forward that member and
  the outcome overload, or the sweep skips.
- **A load of the same server is running** (the start's, a reconnect's, or `RequestInitialStateAsync`): the sweep skips
  that tick without waiting.
- **A held call none of whose channels the channel table holds** cannot be proved gone by a snapshot: the sweep leaves
  it alone, does not ask Asterisk for it alone, and counts it in the `sessions.unverifiable` tag of its span.

Each sweep is a `session reconciliation` span of the `Verbara.Sdk.Sessions` source, tagged `sessions.candidates`,
`sessions.unverifiable`, `sessions.ended` and `verification` (`run`, or `skipped:<reason>`), and one Debug line.

### Switching it off, or running your own

`ReconciliationInterval = Timeout.InfiniteTimeSpan` switches the sweep off: no timer, no snapshot. Calls whose hangup
was lost then stay held until a reconnect's reload. Any other interval of zero or less fails the host's start with
`ArgumentOutOfRangeException`, as before.

A host that wants another bound can call the same reconciliation itself, on a schedule of its own:

```csharp
// One server, on a schedule of your own.
await server.ReconcileChannelsAsync(cancellationToken);
```

Up to 2.7.x `AddVerbaraSessionsMultiServer` registered no sweep, and this guide showed a loop of your own over
`pool.Servers`. Since 2.8.0 that registration runs the sweep for every server of the pool: a host that kept the loop
sends two `Status` per server per tick. Remove the loop, or switch the pool sweep off; see
[the sweep on a multi-server host](#the-sweep-on-a-multi-server-host-280).

It requests only the channel snapshot, reconciles nothing when the snapshot did not complete (it throws
`OperationCanceledException` or `AmiNotConnectedException`), and reconciles nothing, without throwing, when Asterisk
refuses `Status`.

**Measured** on 2026-10-03 against Asterisk 20.20.1, 22.10.1 and 23.4.1, with a host wired as a consumer wires it and
`DialingTimeout` 3 s, a 1 s interval: ten calls per shape and version — a call that rings 8 s and talks 5 s, one that
only rings, an IVR that answers with no dial, a dial that is never answered. With every event delivered, the sweep
ended none of the 120 calls; each ended at its hangup with its own state and cause. With an AMI user that filters out
`Hangup`, it ended all 120, the talked and the IVR calls `Completed` with a talk time and the others `Failed`, all with
`cause=reload`, and none was held past the retention. With the default timeouts, on 22.10.1, an IVR that lasts 100 s
and a call that rings 160 s and talks 5 s ran to their hangups, 5 of 5 each.

## The sweep on a multi-server host (2.8.0)

`AddVerbaraSessionsMultiServer` and `AddVerbaraSessionsMultiServerBuilder` register the reconciliation sweep since
2.8.0. Before, a multi-server host ran none: a call whose `Hangup` never reached the host (an AMI user whose event
filter drops it, or an event pump that lost it, `ami.events.dropped`) stayed open for the life of the process, and a
durable store listed it as active. Only a reconnect of that server cleared it. This is the release's breaking change:
no public API is added or removed, but those calls now end.

### What changes on a multi-server host

- On each `ReconciliationInterval` tick (30 s by default) the sweep takes the servers `VerbaraServerPool` holds and runs,
  for each one, the check a single-server host runs: the candidates are the held calls attached under that server's
  id, older than `DialingTimeout` (60 s by default), holding a channel the server's table holds; when there is one, the
  server receives one `Status`; a call whose channels the completed snapshot omits ends `Completed` if it was answered
  and `Failed` otherwise, with no `HangupCause` and `Metadata["cause"] == "reload"`, once.
- The servers are verified side by side. One whose verification throws or times out, or that was removed while the
  tick held it, is logged and skipped for that tick — at Error with its id while it is still in the pool, at Debug once
  it has left — and the others are verified. A server that is not connected is skipped (`skipped:not-connected`); the
  reload after its reconnect ends its own lost calls.
- A server added to the pool is walked from the next tick on; a server removed is not walked again.
- Each server's pass is a `session reconciliation` span tagged `server.id`, beside the tags of
  [the single-server sweep](#when-the-sweep-does-not-verify); each tick logs one Debug line,
  `Pool reconciliation sweep: servers=<n> serverless=<m>`.
- `GetServices<IHostedService>()` returns one more service. Calling the registration twice registers one sweep.

### What you have to do on a multi-server host

1. **Remove a loop of your own** over `pool.Servers` that calls `ReconcileChannelsAsync`, or switch the pool sweep off.
   Both together are correct (each reconciliation keeps its own read window) but send two `Status` per server per tick.
2. **Attach each server under its id in the pool**: `AttachToServer(server, id)` with the id you gave
   `AddExistingServer` or `AddServerAsync`. The sweep picks a server's candidates by that id; a session attached under
   any other id is never verified and is counted as `serverless`.
3. **Give every Asterisk of the pool its own `systemname`** (`asterisk.conf`, `[options]`). The session engine finds a
   call by the channel ids Asterisk issues, and two servers without a system name issue the same ids
   (`<epoch>.<sequence>`) in the same second: a hangup on one server can then end a call of the other, and calls of
   both servers can share one session. That happens on every `Hangup`, with or without the sweep; the sweep neither
   causes nor repairs it, and what it promises holds only where channel ids are unique across the pool. The session
   engine logs a Warning the first time two of its servers report the same id (see
   [troubleshooting](troubleshooting.md#multi-server-pools)).
4. **The AMI user of every server needs `Status`**: `system`, `call` or `reporting` in its `write` line (`write = all`
   includes them). A server whose user may not run it logs `[LIVE] Status refused: …` at Warning once per AMI session,
   and its lost hangups stay open, as on a single server.

### Switching the pool sweep off

The registration's `configure` delegate is the switch:

```csharp
builder.Services.AddVerbaraSessionsMultiServer(o => o.ReconciliationInterval = Timeout.InfiniteTimeSpan);
```

No timer is started and nothing is sent. No SDK registration binds a `Sessions` configuration section (`AddVerbara`
binds only `Asterisk:Ami` and `Asterisk:Ari`). To drive the options from configuration, bind the section inside the
delegate yourself:

```csharp
var sessions = builder.Configuration.GetSection("Sessions");
builder.Services.AddVerbaraSessionsMultiServer(o => sessions.Bind(o));
```

For Native AOT, set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` in the host's
project, so the `Bind` call compiles to generated code instead of reflection. `Timeout.InfiniteTimeSpan` is minus one
millisecond, which configuration writes as:

```json
{ "Sessions": { "ReconciliationInterval": "-00:00:00.001" } }
```

### What it costs

One `Status` per server per tick on which that server holds a candidate. Its size grows with the channels that server
holds; see [high-load-tuning.md](high-load-tuning.md#session-reconciliation) for one server's cost.

### What you see

- A call whose hangup was lost ends at the first tick after its server dropped it once it is older than
  `DialingTimeout`, as on a single server: one `CallEndedEvent`, counted once in `sessions.completed` or
  `sessions.failed`, saved to the store and released after `CompletedRetention`.
- A held session that holds no channel the server's table holds (one restored from a store without participants, for
  instance) is counted in `sessions.unverifiable` on every tick; it was invisible before.
- On a host that registers both `AddVerbaraSessions` and a multi-server registration, the single DI server is verified
  by the single-server sweep only, also when the pool holds it. The sessions that registration attaches (under the id
  `default`) are counted as `serverless` on each tick unless the pool holds the DI server under `default`.

### Measured on two servers

On 2026-10-08, two Asterisk servers per version (20.20.1, 22.10.1, 23.4.1, each with its own `systemname`), a host
built with `AddVerbaraMultiServer` + `AddVerbaraSessionsMultiServer`, servers joined by connect, start, add to the
pool and attach, `DialingTimeout` 3 s, a 1 s interval; server A's AMI user filters out `Hangup`, server B's does not.
Ten calls per shape (a call that rings 8 s and talks 5 s, one that only rings, an IVR answered with no dial for 8 s or
100 s, a dial never answered, a call that rings 160 s and talks 5 s): before 2.8.0, A left 60 of 60 open on each
version; with the pool sweep, 0 of 60, all ended `cause=reload` once, while B's 60 ended by their own hangups as
before. With 100 simultaneous calls per server, A's 100 of 100 open became 0 (22.10.1, 23.4.1). Calls that crossed
the trunk, a server added to the running host, and a server cut off the network for about 16 s while the other kept
its lost hangups: every lost hangup ended once, no call Asterisk still had was ended, and no call ended twice.

### A server that leaves the pool

The sessions of a server that was detached and removed, or that came back as a new instance under the same id, stay
held: the sweep neither verifies nor ends them, and counts them as `serverless` on every tick. The SDK cannot tell
whether such a call is over. `DetachFromServer` is the same call when a cluster hands a node over to another instance,
which restores those calls from a shared store and goes on following them, and when an operator removes a node; ending
the sessions on detach would raise false `CallEndedEvent`s and write a false ending into a store another instance is
reading, and ending them by their age would let the clock decide an ending, which the sweep never does. No public member
ends a session by hand.
