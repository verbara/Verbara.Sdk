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
[the sweep](#the-reconciliation-sweep-and-long-answered-calls).

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

The sweep's treatment of long answered calls changes in this release, under its own entry in the changelog. This
section is completed by that change.
