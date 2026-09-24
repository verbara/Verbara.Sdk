---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone using ExternalMediaActivity with an AudioSocket server, and the Pro AgentAssist deployment that pairs the two
decision_ref: Sdk/ADR-0060
---

# Proposal: externalmedia-returns-a-channel-id-that-finds-its-stream

## Why

ADR-0060 closed the AudioSocket wire format and said, in as many words, that doing so **does not**
make the ARI route work end to end: the stream table is keyed by the wire UUID while its only
consumer looks the stream up by ARI channel id, and that this was tracked separately. Nothing tracked
it. This is that tracking, and the harvest the #302 close-out owed and did not deliver.

Everything below is measured against a real Asterisk 22.9.0 with a dependency-free TCP listener.
The probe is `probe-externalmedia.py` beside this file and its output is `probe-capture.txt`; every
run records the full query string it sent, because an earlier version of that capture recorded
parameters in prose and a mislabelled run survived into the first draft of this proposal.

### Three defects, not two

**1. The default configuration creates an RTP channel and waits.** `ExternalMediaActivity` accepts an
`AudioSocketServer` in its constructor but leaves `Encapsulation` null, which Asterisk reads as
rtp/udp:

```text
RUN H  QUERY: app=probe&external_host=…&format=slin16
       HTTP 200  name=UnicastRTP/127.0.0.1:19099-0x7f6ce8002100
```

Nothing ever connects to the AudioSocket server, and after `ConnectionTimeout` the activity throws
`TimeoutException`. That is precisely what ADR-0060 predicted, for the shape callers actually get.

**2. Asking for AudioSocket fails at the create call.** Setting `Encapsulation = "audiosocket"` is not
enough, because the activity sends `transport` only when a caller sets `Transport`:

```text
RUN G  QUERY: app=probe&external_host=…&format=slin16&encapsulation=audiosocket
       HTTP 400 -> "transport must be 'tcp' for audiosocket encapsulation"
```

And once transport is supplied, `data` is mandatory too:

```text
RUN D  QUERY: …&encapsulation=audiosocket&transport=tcp
       HTTP 400 -> "data can not be empty"
```

So no configuration of today's activity reaches a working AudioSocket stream: one path times out,
the other two throw at the create call inside `EnsureAriSuccessAsync`.

**3. The two identifiers are different parameters.** The probe passed distinct, byte-order-asymmetric
UUIDs in `channelId` and `data`, so the capture says both which one travels and in which byte order:

```text
channelId = 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9
data      = f9e8d7c6-b5a4-3928-1706-f5e4d3c2b1a0

channel.id : 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9
HEX        : 01 00 10 f9 e8 d7 c6 b5 a4 39 28 17 06 f5 e4 d3 c2 b1 a0
UUID on the wire == channelId ? False    == data ? True
```

`data` becomes the identification UUID the server keys its table by. `channelId` becomes the ARI
`Channel.Id`. Supplying one value as both makes them equal — HTTP 200, and the lookup hits.
Reproduced across fresh UUIDs and both audio formats.

### Rejected alternative: mint the UUID and pass it only as `data`

RUN C shows the stream is findable **today**, with no API change: the activity mints an identifier,
passes it as `data`, and polls `GetStream(thatIdentifier)`. No `CP0002`, no suppression file, no
migration guide, nothing stops compiling.

It is rejected because it fixes the activity and leaves the capability unfixed. A consumer holding
only an ARI channel id — a `StasisStart` or `ChannelHangupRequest` handler, which is the shape Pro's
AgentAssist uses — still cannot find the stream, because `Channel.Id` remains an Asterisk-minted
uniqueid that appears in no table. Making `Channel.Id` **be** the key is the whole point, and it is
what justifies the break priced below. This is a deliberate purchase, not an oversight.

### Why no test caught it

All four constructions of `ExternalMediaActivity` in the suite use the one-argument overload, leaving
both server fields null. The polling loop does run — two tests assert on what it produces — but **the
two `GetStream` branches are never entered**. That is the same shape as the wire-format defect one
level up: a green suite that never reaches the code it appears to cover.

## What Changes

- `CreateExternalMediaAsync` gains a `channelId` parameter on `IAriChannelsResource` and
  `AriChannelsResource`. Asterisk spells it camelCase, unlike every snake_case sibling, and a typo is
  silently ignored rather than rejected — so the URL test asserts the literal.
- `ExternalMediaActivity` derives the request from the server it was handed: AudioSocket encapsulation
  implies `transport = "tcp"`, and one freshly minted UUID goes to both `channelId` and `data`, in
  canonical lowercase form because that is what the server's table is keyed by.
- A functional test originates a real `externalMedia` against a real Asterisk and asserts the stream
  is found by `Channel.Id`.
- The `IAudioServer.GetStream` contract says what its key is and in what form, instead of "channel
  ID" — the wording that invited the confusion — including that `WebSocketAudioServer` keys on
  something else entirely.

## Impact

- **Public API, BREAKING.** The nine-parameter signature is shipped in two packages
  (`src/Verbara.Sdk/PublicAPI.Shipped.txt:499`, `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt:449`).
  Optional parameters are compile-time sugar and ApiCompat matches by full signature, so the old
  member vanishes: `CP0002` in both assemblies plus `CP0006` on the interface. `src/Verbara.Sdk` has
  no `CompatibilitySuppressions.xml` today and gains one, in the ADR-0055 shape — generated
  deliberately, every entry read before it is kept, never left to a build-machine flag that writes a
  file the repository never sees. `PublicAPI.Unshipped.txt` takes a `*REMOVED*` row plus the new row
  in both packages, per ADR-0023.
- **A migration note is obligatory** (ADR-0028, for a minor carrying a break), and the CHANGELOG entry
  is `### Fixed — BREAKING`.
- **Two test doubles stop compiling, loudly rather than silently.** Both NSubstitute setups enumerate
  nine positional `Arg.Any` ending in a `CancellationToken`; inserting a parameter before it lands the
  token in a `string?` slot. They move to named arguments.
- **External implementers of `IAriChannelsResource` break** — Pro's fakes and decorators. That is the
  price of the alternative rejected above, and it is named rather than discovered.
- **Behavioural, for AudioSocket callers only.** RTP callers take an unchanged path.
- **Out of scope, and stated rather than implied:** `WebSocketAudioServer` keys its table by the last
  segment of the request URL, a second and different mismatch that no probe has yet measured; and
  `AudioStreamMetrics` declares ten instruments with no production call site. Both are carried in
  `tasks.md` with file and line, and C7 opens a change for them rather than leaving them as
  checkboxes inside this one.
