# Recordings — asterisk-ami

Byte captures of the AMI stream a real Asterisk PBX sent to one manager client while twelve call
shapes ran against it, one capture per Asterisk version. This suite replays them through the SDK's
own parsing path, so a call-session assertion made here is checked against what Asterisk actually
sent rather than against what a test author believed it sends.

| File | Asterisk | AMI banner | Bytes |
|------|----------|------------|-------|
| `call-shapes-asterisk-20.20.1.raw` | 20.20.1 | `Asterisk Call Manager/9.0.0` | 183 623 |
| `call-shapes-asterisk-22.9.0.raw` | 22.9.0 | `Asterisk Call Manager/11.0.0` | 183 621 |
| `call-shapes-asterisk-23.4.1.raw` | 23.4.1 | `Asterisk Call Manager/12.0.0` | 183 623 |

Captured 2026-09-27. The `.gitattributes` rule `**/Recordings/**/*.raw binary` keeps the CRLF framing
byte-exact; never open and re-save these files in an editor.

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
