---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone using ExternalMediaActivity with an AudioSocket server, and the Pro AgentAssist deployment that pairs the two
decision_ref: Sdk/ADR-0060
---

# Proposal: externalmedia-returns-a-channel-id-that-finds-its-stream

## Why

ADR-0060 closed the AudioSocket wire format and said, in as many words, that doing so **does not** make
the ARI AudioSocket route work end to end: the stream table is keyed by the wire UUID while its only
consumer looks the stream up by ARI channel id, and that this was tracked separately. This is that
tracking. It is also the harvest ADR-0060's own close-out owed and did not deliver.

### The route is broken in two places, and the first one is worse than the note said

`ExternalMediaActivity` creates the channel and then polls for the stream:

```csharp
Channel = await AriClient.Channels.CreateExternalMediaAsync(
    App, ExternalHost, Format, encapsulation: Encapsulation, transport: Transport, ...);
...
_audioStream = _audioSocketServer.GetStream(Channel.Id);
```

The note predicted that poll times out. **It never gets that far.** A probe against a real Asterisk
22.9.0 sent exactly what that call sends and got:

```text
HTTP 400 -> {"message": "data can not be empty"}
```

`data` is **mandatory** for `encapsulation=audiosocket`. The request fails inside
`EnsureAriSuccessAsync`, before any polling. `CreateExternalMediaAsync` exposes `data` — the activity
simply never passes it — so a caller who supplies it gets past this, and then meets the second defect.

### The two identifiers are different parameters, and this is measured

The same probe ran `POST /channels/externalMedia` with **distinct, byte-order-asymmetric** UUIDs in
`channelId` and in `data`, so the capture says both which parameter travels and in which byte order:

```text
channelId = 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9
data      = f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0

channel.id : 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9
HEX        : 01 00 10 f9 e8 d7 c6 b5 a4 39 28 17 06 f5 e4 d3 c2 b1 a0
UUID on the wire == channelId ? False    == data ? True
```

`data` becomes the identification UUID the server keys its stream table by. `channelId` becomes the
ARI `Channel.Id`. Passing neither — today's behaviour — leaves Asterisk minting its own uniqueid,
which no stream table can ever contain.

### Passing the same UUID in both closes the gap, and that is measured too

```text
E1: HTTP 200  id=c165018a-d35b-4783-a057-4d43c1f7ba59
    HEX: 01 00 10 c1 65 01 8a d3 5b 47 83 a0 57 4d 43 c1 f7 ba 59
    UUID on the wire == channelId ? True   == data ? True
```

Reproduced twice with fresh UUIDs. `Channel.Id` **is** the wire UUID, and `GetStream(Channel.Id)`
hits. The full capture is `probe-capture.txt` beside this file.

### `CreateExternalMediaAsync` cannot express it

```csharp
public async ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
    string? encapsulation = null, string? transport = null, string? connectionType = null,
    string? direction = null, string? data = null, CancellationToken cancellationToken = default)
```

`data` is there. **`channelId` is not.** Asterisk documents it as "The unique id to assign the channel
on creation" and it is the only way to choose the ARI channel id. With this signature a caller cannot
align the two even knowing exactly how.

### Why no test caught it

All four constructions of `ExternalMediaActivity` in the test suite use the one-argument overload, so
both server fields stay null and the polling block is skipped entirely. The failing branch is never
executed. That is the same shape as the wire-format defect one level up: a green suite that never
reaches the code it appears to cover.

## What Changes

- `CreateExternalMediaAsync` gains a `channelId` parameter. Additive and optional; no existing call
  site changes meaning.
- `ExternalMediaActivity` mints one UUID and passes it as **both** `channelId` and `data` when the
  encapsulation is AudioSocket, so its own poll can succeed.
- A functional test originates a real `externalMedia` against a real Asterisk and asserts the stream is
  found by `Channel.Id` — the assertion that would have failed before.
- The `IAudioServer.GetStream` contract says what its key is, instead of "channel ID", which is the
  wording that invited the confusion.

## Impact

- **Public API, additive.** A new optional parameter on a shipped method; `PublicAPI.Unshipped.txt`
  records it. No `CP0002`/`CP0011`, so no new suppression entry.
- **Behavioural, for AudioSocket callers only.** `ExternalMediaActivity` with
  `Encapsulation = "audiosocket"` goes from an HTTP 400 to a working stream. RTP callers are untouched.
- **Downstream:** Pro's AgentAssist pairs this activity with an AudioSocket server and is the reason
  the route matters.
- **Out of scope, and stated rather than implied:** `WebSocketAudioServer` keys its table by the last
  segment of the request URL, which is a second and different mismatch; and `AudioStreamMetrics`
  declares ten instruments with no production call site. Both are named in `tasks.md` as follow-ups
  with no fix here.
