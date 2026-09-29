# Recordings — asterisk-ami

Byte captures of the AMI stream a real Asterisk PBX sent to one manager client, one capture per
Asterisk version for each of three runs: twelve call shapes (`call-shapes-*`), seventeen queue
shapes (`queue-shapes-*`), and four queue calls with the PBX's own state snapshots taken while they
waited (`queue-reload-*`). This suite replays them through the SDK's own parsing path, so a
call-session assertion made here is checked against what Asterisk actually sent rather than against
what a test author believed it sends.

| File | Asterisk | AMI banner | Bytes |
|------|----------|------------|-------|
| `call-shapes-asterisk-20.20.1.raw` | 20.20.1 | `Asterisk Call Manager/9.0.0` | 183 623 |
| `call-shapes-asterisk-22.9.0.raw` | 22.9.0 | `Asterisk Call Manager/11.0.0` | 183 621 |
| `call-shapes-asterisk-23.4.1.raw` | 23.4.1 | `Asterisk Call Manager/12.0.0` | 183 623 |
| `queue-shapes-asterisk-20.20.1.raw` | 20.20.1 | `Asterisk Call Manager/9.0.0` | 256 349 |
| `queue-shapes-asterisk-22.9.0.raw` | 22.9.0 | `Asterisk Call Manager/11.0.0` | 256 350 |
| `queue-shapes-asterisk-23.4.1.raw` | 23.4.1 | `Asterisk Call Manager/12.0.0` | 256 350 |
| `queue-reload-asterisk-20.20.1.raw` | 20.20.1 | `Asterisk Call Manager/9.0.0` | 122 799 |
| `queue-reload-asterisk-22.9.0.raw` | 22.9.0 | `Asterisk Call Manager/11.0.0` | 122 800 |
| `queue-reload-asterisk-23.4.1.raw` | 23.4.1 | `Asterisk Call Manager/12.0.0` | 122 800 |

The call and queue shapes were captured 2026-09-27, the reload captures 2026-09-28. The
`.gitattributes` rule `**/Recordings/**/*.raw binary` keeps the CRLF framing byte-exact; never open
and re-save these files in an editor.

The sections from *Topology* to *What was changed from the raw capture* describe the call-shape
captures. The queue-shape captures were made on a different topology and reduced differently; they
are described in [The queue-shape captures](#the-queue-shape-captures). The reload captures reuse the
queue-shape topology with a few additions; they are described in
[The reload captures](#the-reload-captures).

## Topology

Two containers per version on a private network, with no host ports published:

- **`pbx`** — the Asterisk the capture observes. Its PJSIP endpoint `anonymous` takes anything that
  does not identify (context `from-external`); its endpoint `tocaller` reaches the other container.
- **`caller`** — a second Asterisk acting as the far end. It places inbound calls to the PBX over its
  endpoint `topbx`, and it answers, or leaves ringing, the calls the PBX originates towards it
  (context `from-pbx`).

## How the capture was driven

A raw TCP AMI client, not the SDK, logged in to both containers and recorded every byte the PBX
sent on its own manager connection. Before each scenario it sent the PBX an AMI `UserEvent` action
with `UserEvent: N5Marker` and `Scenario: <id>`, which the PBX echoes back as an `Event: UserEvent`
frame, then ran the scenario and let it settle for a fixed number of seconds. The last marker is
`Scenario: N5Done`, followed by `Logoff`. `N5` is only the name of the measurement that produced the
captures. A replay uses the marker frames to attribute each call to the scenario that was current
when the call started. They carry no other meaning, and they are kept verbatim because rewriting them
would break the byte-exact property the replay relies on.

An SDK host was connected to the PBX as a second manager client during the run. Its own action
responses went to its own connection and do not appear here.

Inbound scenarios are placed by the driver as an `Originate` on the **caller** container
(`PJSIP/<exten>@topbx`), so the PBX sees an ordinary inbound PJSIP call. Originate scenarios are an
`Originate` on the **PBX** itself, so the PBX sees an AMI originate: its `DialBegin` and `DialEnd`
name only the dialed side (`DestChannel`, `DestUniqueid`) and carry no calling channel.

## The twelve scenarios

| Id | Kind | What happens |
|----|------|--------------|
| `S1-ivr-pbx-hangs-up` | inbound to `300` | The PBX answers, waits 2 s and hangs up. No dial, no queue. |
| `S2-ivr-caller-hangs-up` | inbound to `301` | The PBX answers and waits; the caller hangs up 3 s after the answer. |
| `S3-originate-answered-no-dial` | originate `PJSIP/400@tocaller` → `n5-orig,500` | The far end rings 1 s and answers; the PBX waits 2 s and hangs up. No dial onward. |
| `S4-originate-local-answered` | originate `Local/300@from-external/n`, application `Wait(10)` | The local channel runs S1's IVR: answered, 2 s, hung up by the PBX. |
| `S5-originate-never-answered` | originate `PJSIP/401@tocaller`, timeout 4 s | The far end rings and never answers; the originate gives up (`NoAnswer`). |
| `S6-dialer-queued-agent-answers` | originate `PJSIP/400@tocaller` → `n5-orig,510` | Answered, then queued in `n5q`; the member answers, talks 3 s and hangs up. |
| `S7-dialer-queued-abandoned` | originate `PJSIP/400@tocaller` → `n5-orig,520` | Answered, then queued in `n5qempty`, which has no member; the queue gives up after 3 s. |
| `S8-long-ivr` | inbound to `302` | The PBX answers, waits 10 s and hangs up. |
| `S9-control-inbound-dialed` | inbound to `303` | The PBX dials the agent `Local/600@n5-agents/n`, which answers and talks 3 s. |
| `S10-ivr-then-queue-agent-answers` | inbound to `304` | The PBX answers, waits 1 s, then queues in `n5q`; the member answers and talks 3 s. |
| `S11-ivr-then-queue-abandoned` | inbound to `305` | The PBX answers, waits 1 s, then queues in `n5qempty`; the queue gives up after 3 s. |
| `S12-ivr-then-dial-agent` | inbound to `306` | The PBX answers, waits 1 s, then dials the agent, which answers and talks 3 s. |

## Dialplans

PBX, `extensions.conf`:

```ini
[general]
static = yes
writeprotect = no

; Inbound calls from the "caller" container land here (PJSIP anonymous endpoint).
[from-external]
; S1 IVR: the PBX answers, plays for 2 s, and hangs up itself. No Dial, no Queue.
exten => 300,1,Answer()
 same => n,Wait(2)
 same => n,Hangup()
; S2 IVR: the PBX answers and waits; the caller hangs up first.
exten => 301,1,Answer()
 same => n,Wait(60)
 same => n,Hangup()
; S8 long IVR / Voice-AI-shaped: answered, 10 s, PBX hangs up. Longer than the probe's DialingTimeout.
exten => 302,1,Answer()
 same => n,Wait(10)
 same => n,Hangup()
; S9 control: a normal inbound call that is dialed onward to an agent (Local) and answered.
exten => 303,1,Dial(Local/600@n5-agents/n,20)
 same => n,Hangup()
; S10 the usual contact-center inbound: the dialplan answers (welcome prompt), then queues; an agent answers.
exten => 304,1,Answer()
 same => n,Wait(1)
 same => n,Queue(n5q,,,,20)
 same => n,Hangup()
; S11 same, no agent; the queue gives up after 3 s.
exten => 305,1,Answer()
 same => n,Wait(1)
 same => n,Queue(n5qempty,,,,3)
 same => n,Hangup()
; S12 IVR then Dial: the dialplan answers, then dials an agent who answers.
exten => 306,1,Answer()
 same => n,Wait(1)
 same => n,Dial(Local/600@n5-agents/n,20)
 same => n,Hangup()

; Where an AMI Originate lands once the far end answers.
[n5-orig]
; S3 outbound "voice broadcast / outbound Voice AI": answered, no Dial, no Queue, PBX hangs up.
exten => 500,1,Wait(2)
 same => n,Hangup()
; S6 dialer: answered, then queued; an agent answers.
exten => 510,1,Queue(n5q,,,,20)
 same => n,Hangup()
; S7 dialer: answered, then queued; no agent; the queue gives up after 3 s.
exten => 520,1,Queue(n5qempty,,,,3)
 same => n,Hangup()

[n5-agents]
; The queue member / dialed agent: answers and talks for 3 s, then hangs up.
exten => 600,1,Answer()
 same => n,Wait(3)
 same => n,Hangup()
```

PBX, `queues.conf`:

```ini
[general]
persistentmembers = no

[n5q]
strategy = ringall
timeout = 15
joinempty = yes
leavewhenempty = no
member => Local/600@n5-agents/n

[n5qempty]
strategy = ringall
joinempty = yes
leavewhenempty = no
```

Caller, `extensions.conf`:

```ini
[general]
static = yes
writeprotect = no

; Calls the pbx originates to this container (the "customer").
[from-pbx]
; answers after 1 s of ringing and stays up; the pbx side decides when the call ends.
exten => 400,1,Ringing()
 same => n,Wait(1)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
; rings and never answers.
exten => 401,1,Ringing()
 same => n,Wait(60)
 same => n,Hangup()

; The far end of calls this container places to the pbx (the originated local leg runs this).
[caller-side]
exten => 900,1,Wait(60)
 same => n,Hangup()
; hangs up 3 s after the pbx answers (S2: the caller ends the IVR call).
exten => 903,1,Wait(3)
 same => n,Hangup()
```

The S2 call is placed with the caller's local leg in `caller-side,903`; every other inbound scenario
uses `caller-side,900`.

## What was changed from the raw capture

Everything not listed here is the byte stream exactly as the PBX sent it.

- **Removed:** the login `Response` (`ActionID: n5-1`, "Authentication accepted") and the
  `Event: SuccessfulAuth` frame. The latter carries a container address, the manager account name
  and a manager session id. Each removal takes the whole frame, including its terminating blank line,
  so the framing of what remains is unchanged.
- **Replaced:** each distinct UUID Asterisk generated during the run (the `BridgeUniqueid` of every
  bridge, and the `Value` of each `BRIDGEPVTCALLID` variable Asterisk set on a bridged PJSIP leg) is
  replaced by a single-digit fill of the same length, in order of first appearance:
  `11111111-1111-1111-1111-111111111111`, then `22222222-…`, up to `88888888-…` (8 per capture). The replacement is consistent within a file,
  so every frame that named the same bridge still names the same one. It exists because the
  repository's recording redaction check rejects any UUID-shaped value that is not a placeholder.
  The replay was compared with and without the fill and reports the same calls.

## The queue-shape captures

Fifteen queue shapes and two calls that never join a queue, run one after another against Asterisk
20.20.1, 22.9.0 and 23.4.1. Each file holds 711 frames after the banner line. They are replayed by
`AmiCaptureReplay.ReplayQueueShapesAsync`, which scores every shape against Asterisk's own verdict in
the same file: app_queue's `AgentConnect` on the caller's channel is an answered visit, and its
`QueueCallerAbandon` an abandoned one.

### Topology

Two containers per version on a private Docker network, with no host ports published:

- **`dut`** — the Asterisk the capture observes. Its PJSIP endpoint `pstn` is the trunk to the other
  container and takes every inbound call (context `from-pstn`). Its endpoints `agent1`, `agent2`,
  `agent3`, `agentphone` and `agentphone2` (context `from-agents`) each have their contact on the
  other container, where a dialplan extension plays the phone.
- **`far`** — a second Asterisk acting as the public network: it places the inbound calls over its
  endpoint `dut`, answers the dialer's customer leg, and plays the phones behind the agent endpoints
  (context `from-dut`).

### How the capture was driven

A raw TCP tap, not the SDK, logged in to the PBX's manager interface (a `read = all` account) one
second before the run started, and wrote every byte the PBX sent on that connection to a file until
45 s passed without a `Newchannel` or a `Hangup`. `timestampevents` was off, so no frame carries an
event time. The run itself was driven by an SDK host connected as a second manager client; its own
action responses went to its own connection and do not appear here.

1. **Agents.** Two AMI `Originate`s on the PBX log in the `app_agent_pool` agents from their phones:
   `PJSIP/agentphone` → `agent-login,1001` and `PJSIP/agentphone2` → `agent-login,1002`.
2. **Shapes.** Each inbound shape is an AMI `Originate` on the far end, `PJSIP/<exten>@dut` with the
   application `Wait` and the caller id `<id> <number>`, so the PBX sees an ordinary inbound call on
   `pstn`. The dialer shape `PD` is instead an `Originate` on the PBX itself: `PJSIP/1001@pstn` into
   `dialer,qp,1`, caller id `Dialer <7000>`.
3. **The transfer.** For `T`, once the receptionist's `PJSIP/agent1` channel is up the driver waits
   1.5 s and sends AMI `BlindTransfer` for that channel to `4015@from-pstn`.
4. **Pacing.** The next shape starts once every call of the current one has ended and 1.5 s passed
   with no AMI traffic, so no two shapes' calls overlap.

### The seventeen shapes

A replay keys each shape by the caller number its caller's channel reports in `Newchannel`. The
dialer's customer leg is created by the PBX before any caller id is set on it, so its `Newchannel`
reports `CallerIDNum: <unknown>` on a `PJSIP/pstn-` channel; a replay reads that pair as `7000`.

| Id | Caller | Extension | What happens | Asterisk: connect / abandon |
|----|--------|-----------|--------------|-----------------------------|
| `L` | 5550001 | 4001 | `Queue(q-local)`; the `Local/agent@agents/n` member rings 2 s and answers. | 1 / 0 |
| `P` | 5550002 | 4002 | `Queue(q-pjsip)`; the `PJSIP/agent1` phone rings 2 s and answers. | 1 / 0 |
| `P2` | 5550003 | 4003 | `Queue(q-ringall)` over `agent1`, which answers, and `agent2`, which never does. | 1 / 0 |
| `PI` | 5550004 | 4004 | The PBX answers, plays 3 s, then `Queue(q-pjsip)`. | 1 / 0 |
| `PD` | 7000 | `dialer,qp` | Dialer: the customer rings 1 s and answers, then `Queue(q-pjsip)`. | 1 / 0 |
| `A` | 5550005 | 4005 | `Queue(q-agent)`; the member `Local/1001@agent-request/n` runs `AgentRequest(1001)`. | 1 / 0 |
| `AO` | 5550006 | 4006 | The same agent through a `Local` member without `/n`, which optimizes away. | 1 / 0 |
| `F` | 5550007 | 4007 | `Queue(q-freepbx)`; a FreePBX-style member `Local/agent1@from-queue/n` that dials `PJSIP/agent1`. | 1 / 0 |
| `XC` | 5550013 | 4013 | `Queue(q-confirm)`; the member's phone answers but a `U()` gosub rejects the call, so the caller is never connected and gives up at 8 s. | 0 / 1 |
| `AX` | 5550014 | 4014 | `Queue(q-agent-ack)`; agent 1002 must acknowledge (`ackcall = yes`) and never does; the caller gives up at 8 s. | 0 / 1 |
| `X1` | 5550008 | 4008 | `Queue(q-noans)`; nobody answers and the caller gives up at 5 s. | 0 / 1 |
| `X2` | 5550009 | 4009 | The PBX answers, then `Queue(q-noans)`; the caller hangs up 5 s after the answer. | 0 / 1 |
| `X3` | 5550010 | 4010 | `Queue(q-noans,,,,4)` times out after 4 s; the dialplan waits 1 s and hangs up. | 0 / 1 |
| `T` | 5550015 | 4011 → 4015 | A direct dial to `agent1`, the receptionist, who blind-transfers the caller to `Queue(q-pjsip3)`; `agent3` answers. | 1 / 0 |
| `O` | 5550016 | 4016 | `Queue(q-noans,,,,4)` times out after 4 s, then `Queue(q-pjsip3)`; `agent3` answers. | 1 / 1 |
| `D` | 5550011 | 4011 | Control, no queue: a direct `Dial(PJSIP/agent1)`, answered. | 0 / 0 |
| `I` | 5550012 | 4012 | Control, no queue: the PBX answers, plays 4 s and hangs up. | 0 / 0 |

### Dialplans, queues and agents

`dut`, `extensions.conf`:

```ini
[general]
static = yes
writeprotect = no

[from-pstn]
exten => 4001,1,Queue(q-local)
 same => n,Hangup()
exten => 4002,1,Queue(q-pjsip)
 same => n,Hangup()
exten => 4003,1,Queue(q-ringall)
 same => n,Hangup()
exten => 4004,1,Answer()
 same => n,Wait(3)
 same => n,Queue(q-pjsip)
 same => n,Hangup()
exten => 4005,1,Queue(q-agent)
 same => n,Hangup()
exten => 4006,1,Queue(q-agent-opt)
 same => n,Hangup()
exten => 4007,1,Queue(q-freepbx)
 same => n,Hangup()
exten => 4013,1,Queue(q-confirm)
 same => n,Hangup()
exten => 4014,1,Queue(q-agent-ack)
 same => n,Hangup()
exten => 4008,1,Queue(q-noans)
 same => n,Hangup()
exten => 4009,1,Answer()
 same => n,Queue(q-noans)
 same => n,Hangup()
exten => 4010,1,Queue(q-noans,,,,4)
 same => n,Wait(1)
 same => n,Hangup()
exten => 4011,1,Dial(PJSIP/agent1,30)
 same => n,Hangup()
exten => 4012,1,Answer()
 same => n,Wait(4)
 same => n,Hangup()
exten => 4015,1,Queue(q-pjsip3)
 same => n,Hangup()
exten => 4016,1,Queue(q-noans,,,,4)
 same => n,Queue(q-pjsip3)
 same => n,Hangup()

[dialer]
exten => qp,1,Queue(q-pjsip)
 same => n,Hangup()

[agents]
exten => agent,1,Ringing()
 same => n,Wait(2)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()

[from-queue]
exten => agent1,1,Dial(PJSIP/agent1,30)
 same => n,Hangup()

[from-queue-confirm]
exten => agent1,1,Dial(PJSIP/agent1,30,U(sub-reject))
 same => n,Hangup()

[sub-reject]
exten => s,1,Wait(1)
 same => n,Set(GOSUB_RESULT=ABORT)
 same => n,Return()

[agent-request]
exten => _X.,1,AgentRequest(${EXTEN})
 same => n,Hangup()

[agent-login]
exten => _X.,1,AgentLogin(${EXTEN},s)
 same => n,Hangup()

[from-agents]
exten => _X.,1,Hangup()
```

`dut`, `queues.conf`:

```ini
[general]
persistentmembers = no

[qdefaults](!)
strategy = ringall
timeout = 30
retry = 1
wrapuptime = 0
joinempty = yes
leavewhenempty = no
ringinuse = yes
announce-frequency = 0
periodic-announce-frequency = 0

[q-local](qdefaults)
member => Local/agent@agents/n

[q-pjsip](qdefaults)
member => PJSIP/agent1

[q-ringall](qdefaults)
member => PJSIP/agent1
member => PJSIP/agent2

[q-agent](qdefaults)
member => Local/1001@agent-request/n,0,Agent One,Agent:1001

[q-agent-opt](qdefaults)
member => Local/1001@agent-request,0,Agent One,Agent:1001

[q-freepbx](qdefaults)
member => Local/agent1@from-queue/n,0,Agent1 FreePBX,PJSIP/agent1

[q-confirm](qdefaults)
member => Local/agent1@from-queue-confirm/n,0,Agent1 Confirm,PJSIP/agent1

[q-noans](qdefaults)
member => PJSIP/agent2

[q-agent-ack](qdefaults)
member => Local/1002@agent-request/n,0,Agent Two,Agent:1002

[q-pjsip3](qdefaults)
member => PJSIP/agent3
```

`dut`, `agents.conf`:

```ini
[general]

[agent-defaults](!)
ackcall = no
autologoff = 0
wrapuptime = 0

[1001](agent-defaults)
fullname = Agent One

[1002](agent-defaults)
fullname = Agent Two
ackcall = yes
```

`far`, `extensions.conf`:

```ini
[general]
static = yes
writeprotect = no

[from-dut]
; the dialer's customer: rings 1 s, answers, stays 8 s
exten => 1001,1,Ringing()
 same => n,Wait(1)
 same => n,Answer()
 same => n,Wait(8)
 same => n,Hangup()
; the phone behind PJSIP/agent1: rings 2 s, answers, stays until the caller hangs up
exten => agent1,1,Ringing()
 same => n,Wait(2)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
; the phone behind PJSIP/agent2: rings and never answers
exten => agent2,1,Ringing()
 same => n,Wait(120)
 same => n,Hangup()
; agent 1001's phone: answers the login call at once and stays
exten => agentphone,1,Answer()
 same => n,Wait(3600)
 same => n,Hangup()
; agent 1002's phone: answers the login call and never sends the acknowledging DTMF
exten => agentphone2,1,Answer()
 same => n,Wait(3600)
 same => n,Hangup()
; the phone behind PJSIP/agent3: rings 2 s, answers
exten => agent3,1,Ringing()
 same => n,Wait(2)
 same => n,Answer()
 same => n,Wait(60)
 same => n,Hangup()
```

`manager.conf` and `pjsip.conf` are not reproduced: they hold the containers' credentials. The
endpoint layout they define is the one under *Topology*.

### What was changed from the raw capture

Each raw capture held 1,863 frames after the banner, about 764 KB, over the 256 KiB per-file cap. It
was reduced by removing **whole frames** of the types the SDK does not read, so every frame that is
left is byte-exact, CRLF framing included, apart from the bridge ids below.

- **Kept** (the same counts in each version): the banner line; every frame of a type
  `VerbaraServer`'s event observer dispatches — `Newchannel` 49, `Newstate` 58, `Hangup` 49,
  `DialBegin` 26, `DialEnd` 32, `BridgeCreate` 17, `BridgeEnter` 35, `BridgeLeave` 35,
  `BridgeDestroy` 16, `QueueCallerJoin` 16, `QueueCallerLeave` 16, `QueueMemberStatus` 151,
  `DeviceStateChange` 157, `AgentLogin` 2, `AgentLogoff` 2, `AgentConnect` 10, `AgentComplete` 10,
  `BlindTransfer` 1, `Unhold` 1; and app_queue's verdict and member frames, which the observer does
  not dispatch — `QueueCallerAbandon` 6, `AgentCalled` 18, `AgentRingNoAnswer` 4 (no `AgentDump` was
  sent). The dispatched set was read from the observer's `switch` when the files were reduced; a type
  it starts dispatching later is not in these files.
- **Removed** (the same counts in each version, 1,152 frames): `VarSet` 737, `Newexten` 93,
  `RTCPSent` 72, `RTCPReceived` 72, `NewConnectedLine` 52, `NewCallerid` 38, `SoftHangupRequest` 28,
  `DialState` 24, `HangupRequest` 22, `LocalBridge` 7, `OriginateResponse` 3, `SuccessfulAuth` 2,
  `FullyBooted` 1, and the login `Response` 1. Every frame that carried a container address
  (`RTCPSent`, `RTCPReceived`, `SuccessfulAuth`) is among them, as are the manager account name and
  session ids `SuccessfulAuth` carries.
- **Replaced:** each of the 17 distinct `BridgeUniqueid` values is replaced by a single-character
  fill of the same length, in order of first appearance: `11111111-1111-1111-1111-111111111111`
  through `99999999-…`, then `aaaaaaaa-…` through `hhhhhhhh-…`. There are more bridges than
  single-character hexadecimal fills, so the last two are not hexadecimal; the SDK treats a bridge
  id as an opaque string. The fill exists for the same reason as in the call-shape captures, the
  recording redaction check.

The reduction was checked by replaying the full capture, the reduced capture before the fill and
the reduced capture after it: every shape scored the same in each (its `CallConnectedEvent`s,
answered, abandoned, left waiting, and the exceptions the observer threw).

## The reload captures

Four queue calls, run one after another against Asterisk 20.20.1, 22.9.0 and 23.4.1, each with its
own caller number. While a caller waited, the tap asked the PBX for the two snapshots
`VerbaraServer.RequestInitialStateAsync` reads on a reload, `Status` and `QueueStatus`, so each file
holds Asterisk's own answers beside the live events around them. A replay can then run the SDK's
reload at the point where Asterisk returned a snapshot, answered by that snapshot, with the live
frames of an outage withheld. Each file holds 360 frames after the banner line. They are replayed one
call at a time by `AmiCaptureReplay.ReplayQueueReloadAsync`, which sets the session manager's clock
from each frame's `Timestamp` and delivers only live frames: a frame that carries an `ActionID` is the
tap's own marker or an answer to the tap's own action.

### Topology and additions

The topology, the dialplans, the queues and the agents are those of
[The queue-shape captures](#the-queue-shape-captures), with these additions and nothing else:

- `manager.conf`, `[general]`: `timestampevents = yes`. Every live event therefore carries a
  `Timestamp` header, Asterisk's own time of the event in seconds with microseconds. The snapshot
  answers (`Status`, `StatusComplete`, `QueueParams`, `QueueMember`, `QueueEntry`,
  `QueueStatusComplete`) carry none.
- `queues.conf`:

  ```ini
  [q-late](qdefaults)
  member => Local/late@agents-late/n
  ```

- `extensions.conf`, in `[from-pstn]`:

  ```ini
  exten => 4021,1,Queue(q-late)
   same => n,Hangup()
  exten => 4022,1,Queue(q-late,,,,4)
   same => n,Wait(3)
   same => n,Queue(q-late)
   same => n,Hangup()
  exten => 4023,1,Queue(q-noans,,,,4)
   same => n,Queue(q-late)
   same => n,Hangup()
  exten => 4024,1,Queue(q-late,,,,1)
   same => n,Queue(q-late)
   same => n,Hangup()
  ```

  and a new context for the member of `q-late`, which rings 10 s and answers:

  ```ini
  [agents-late]
  exten => late,1,Ringing()
   same => n,Wait(10)
   same => n,Answer()
   same => n,Wait(60)
   same => n,Hangup()
  ```

### How the capture was driven

A raw TCP tap, not the SDK, logged in to the PBX's manager interface (a `read = all` account) with
`Events: on` and wrote every byte the PBX sent on that connection to a file. No SDK host was
connected. Each call is an AMI `Originate` on the far end, `PJSIP/<exten>@dut` with the application
`Wait(4)` and the caller id `<id> <number>`: the PBX answers the caller only when the queue connects
it, so the far end hangs up 4 s after the connect. The next call starts once every channel of the
current one has gone and 2 s passed with no `Newchannel` or `Hangup`.

A snapshot is three actions the tap sends on its own connection, in this order, each with its own
`ActionID`:

1. `UserEvent` with `UserEvent: W2qrSnapshot` and `Snapshot: <id>`, `ActionID: w2qr-<id>-mark`.
   Asterisk echoes it as an `Event: UserEvent` frame with its own `Timestamp`, and copies the
   `ActionID` and `Snapshot` headers into it. That `Timestamp` is the instant of the snapshot: the last
   live event before it can be seconds older, because a caller waiting in a queue emits nothing.
2. `Status`, `ActionID: w2qr-<id>-st`.
3. `QueueStatus` with no `Queue` header, so every queue is listed, as the SDK asks on a reload;
   `ActionID: w2qr-<id>-qs`.

Each snapshot is triggered by an event of the call as the tap received it, never by the time since
the originate. In every file each snapshot's answer frames are contiguous: no live event falls
between its first and its last frame. `w2qr` is only the name of the measurement.

### The four calls

| Id | Caller | Extension | Asterisk, in wire order | Snapshots |
|----|--------|-----------|-------------------------|-----------|
| `a` | 5552101 | 4021 | `QueueCallerJoin(q-late)` → `QueueCallerLeave(q-late)` → `AgentConnect(q-late)`, `HoldTime: 10` | `a1`, 3 s after the join: the caller's `QueueEntry` in `q-late`, `Wait: 3` |
| `b` | 5552102 | 4022 | `QueueCallerJoin(q-late)` → `QueueCallerAbandon(q-late)`, `HoldTime: 4` → `QueueCallerLeave(q-late)` → `QueueCallerJoin(q-late)` → `QueueCallerLeave(q-late)` → `AgentConnect(q-late)`, `HoldTime: 10` | `b1`, 1 s after the first `QueueCallerLeave`: no `QueueEntry` for the caller (its `Status` shows `Application: Wait`); `b2`, 2 s after the second join: `q-late`, `Wait: 2` |
| `c` | 5552103 | 4023 | `QueueCallerJoin(q-noans)` → `QueueCallerAbandon(q-noans)`, `HoldTime: 4` → `QueueCallerLeave(q-noans)` → `QueueCallerJoin(q-late)` → `QueueCallerLeave(q-late)` → `AgentConnect(q-late)`, `HoldTime: 10` | `c1`, 2 s after the join to `q-late`: `q-late`, `Wait: 2` |
| `d` | 5552104 | 4024 | `QueueCallerJoin(q-late)` → `QueueCallerAbandon(q-late)`, `HoldTime: 1` → `QueueCallerLeave(q-late)` → `QueueCallerJoin(q-late)` → `QueueCallerLeave(q-late)` → `AgentConnect(q-late)`, `HoldTime: 10` | `d1`, 1 s after the second join: `q-late`, `Wait: 1` |

The order and every value in the table are the same in the three files. Asterisk's verdicts, one
`AgentConnect` or `QueueCallerAbandon` per visit: `a` 1 connect and 0 abandons, `b` 1 and 1, `c` 1
and 1 (the abandon in `q-noans`), `d` 1 and 1. Every `QueueEntry` in every snapshot carries `Wait`.

The snapshot's reported start of the caller's current visit, the marker's `Timestamp` minus `Wait`,
falls between 0.0004 s and 0.0014 s after that visit's `QueueCallerJoin` in every snapshot of every
file. For `d`, the shortest loop (a 1 s timeout straight back into the same queue), that start is
1.0018 s (20.20.1), 1.0022 s (22.9.0) and 1.0018 s (23.4.1) after the call's first
`QueueCallerJoin`; for `b` it is 7.007 s, 7.006 s and 7.008 s.

### What was changed from the raw capture

Each raw capture held 645 frames after the banner, about 251 KB. It was reduced by removing whole
frames, so every frame that is left is byte-exact, CRLF framing included, apart from the bridge ids
below.

- **Kept** (the same counts in each version): the banner line; every frame of a type
  `VerbaraServer`'s event observer dispatches — `Newchannel` 17, `Newstate` 19, `Hangup` 17,
  `DialBegin` 7, `DialEnd` 10, `BridgeCreate` 4, `BridgeEnter` 8, `BridgeLeave` 8, `BridgeDestroy` 4,
  `QueueCallerJoin` 7, `QueueCallerLeave` 7, `QueueMemberStatus` 16, `DeviceStateChange` 53,
  `AgentConnect` 4, `AgentComplete` 4; app_queue's verdict and member frames, which the observer does
  not dispatch — `QueueCallerAbandon` 3, `AgentCalled` 7, `AgentRingNoAnswer` 3; the five snapshot
  markers (`UserEvent` 5); and every frame of the five snapshot answers — the three `Response`
  frames of each snapshot (15), `Status` 13, `StatusComplete` 5, `QueueParams` 55, `QueueMember` 60,
  `QueueEntry` 4, `QueueStatusComplete` 5. The dispatched set was read from the observer's `switch`
  when the files were reduced.
- **Removed** (the same counts in each version, 285 frames): `VarSet` 181, `Newexten` 42,
  `NewCallerid` 13, `NewConnectedLine` 13, `HangupRequest` 10, `SoftHangupRequest` 10, `DialState` 7,
  `LocalBridge` 6, `FullyBooted` 1, `SuccessfulAuth` 1, and the login `Response` 1. `SuccessfulAuth`
  carries a container address, the manager account name and a manager session id.
- **Replaced:** each of the 4 distinct `BridgeUniqueid` values is replaced by a single-character fill
  of the same length, in order of first appearance: `11111111-1111-1111-1111-111111111111` through
  `44444444-…`, for the recording redaction check. 24 frames carry one.

The reduction was checked by reading the raw and the reduced capture with the same script: for every
call, the lifecycle frames in order with their `Timestamp`, `HoldTime` and verdicts, and for every
snapshot its marker, the caller's `Status` and `QueueEntry`, and the reported start, are identical.
