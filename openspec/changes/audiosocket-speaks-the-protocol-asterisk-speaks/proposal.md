---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone who has ever pointed an Asterisk `AudioSocket()` dialplan line at this SDK, and the Pro AgentAssist deployment that ships one
decision_ref: Sdk/ADR-0017
---

# Proposal: audiosocket-speaks-the-protocol-asterisk-speaks

## Why

### Neither AudioSocket server has ever completed a handshake, and this is measured

A probe run against a real Asterisk 22 with the real `res_audiosocket.so`, originating a real call into
`AudioSocket(11111111-2222-3333-4444-555555555555,127.0.0.1:9092)`, captured the first bytes Asterisk
puts on the wire:

```text
BYTES RECEIVED: 19
HEX: 01 00 10 | 11 11 11 11 22 22 33 33 44 44 55 55 55 55 55 55
```

**Three bytes of header, then sixteen of UUID.** The header is one byte of type and two of length,
big-endian: `01` is the type, `00 10` is 16, and the sixteen bytes that follow are the dialplan's UUID
in RFC 4122 order.

Both of this repository's implementations read a **four**-byte header — `HeaderSize = 4` at
`src/Verbara.Sdk.Ari/Audio/AudioSocketProtocol.cs:12` and at
`src/Verbara.Sdk.VoiceAi.AudioSocket/Internal/AudioSocketFrameCodec.cs:9`, the latter commented
"1 type + 3 length". And both believe `0x01` means an audio frame while the UUID is `0x00`. So the very
first packet Asterisk sends is read as an audio frame of length `0x111111`, the UUID never arrives, and
the session never starts. Asterisk then logs what it logged in the probe:

```text
app_audiosocket.c:221 audiosocket_run: Reached timeout after 2000 ms of no activity on
AudioSocket connection between 'Local/400@default-00000000;2' and '127.0.0.1:9092'
```

### The enum contradicts itself, so this needed no external source to catch

`src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketFrameType.cs` declares `AudioSlin12 = 0x11` through
`AudioSlin192 = 0x18` — the extended per-rate audio codes, taken from Asterisk — while declaring the
base `Audio = 0x01`. If `0x11` is slin12, the 8 kHz audio code immediately below it is `0x10`, not
`0x01`. The file has the extended codes right and the base code wrong, and has said so to every reader
since the rebrand.

### Why six months of green tests never noticed

- **Nothing in this repository cites the protocol's source.** `grep -rl res_audiosocket` returns
  nothing. The wire format was written into
  `docs/specs/2026-03-19-sprint23-voiceai-audiosocket-design.md:236-253` with no citation, and both
  implementations were written from that.
- **The functional dialplan has no `AudioSocket()` extension.** `docker/functional/asterisk-config/extensions.conf`
  covers FastAGI, Stasis, queues and echo. AudioSocket is absent, so no functional test could reach it.
- **The unit tests are a closed loop.** Each package builds its test frames with its own enum and its own
  codec, then asserts its own parser reads them back. A test written that way passes whatever the bytes
  on the wire actually are. It is the exact shape the archived functional-test design already flagged as
  a gap: *"No Voice AI E2E with real audio (AudioSocket → STT → Handler → TTS)"*.

### ADR-0017 documents a protocol that exists nowhere

`docs/decisions/0017-audiosocket-codec-negotiation.md` is Accepted and describes an 18-byte header,
frame types that are not members of either enum, and per-connection codec negotiation from the first
inbound audio frame — while the code assigns the format from options and never reads it off the wire.
It is not a stale ADR about a real decision; it documents a design that was never built.

## What Changes

1. **The header becomes three bytes in both codecs**: one type, two of big-endian length. The ARI
   `AudioSocketProtocol` and the VoiceAi `AudioSocketFrameCodec` each change their header constant and
   their length read and write.
2. **Both frame-type enums take the values Asterisk sends**: `Hangup = 0x00`, `Uuid = 0x01`,
   `Dtmf = 0x03` (new, currently unhandled by both), `Audio = 0x10`, `Error = 0xFF`. `Silence = 0x02`
   is removed from the VoiceAi enum — Asterisk has no such frame. The `AudioSlin12..192` series is
   already correct and does not move.
3. **The ARI UUID parse takes `bigEndian: true`** (`Audio/AudioSocketSession.cs:184`), which the probe
   confirms and which this repo's own spec already required. VoiceAi already does this.
4. **A fixture of real captured bytes, shared by both parsers.** The UUID frame above is its first
   entry, taken from the wire and not from either codec. Both packages' tests decode it. A test that
   builds its own input with the code under test cannot catch a wire error, so this fixture is what the
   change is actually worth.
5. **An `AudioSocket()` extension in the functional dialplan**, and a functional test that originates a
   call and asserts the session starts with the dialplan's UUID. That is the gap that allowed this.
6. **ADR-0017 is superseded** by an ADR that records the protocol's source — Asterisk's
   `res_audiosocket.h` — and the rule that the wire format is pinned by captured bytes.

## Impact

- `src/Verbara.Sdk.Ari/Audio/AudioSocketProtocol.cs`, `Audio/AudioSocketSession.cs`, and the frame-type
  enum on `IAriClient.cs`.
- `src/Verbara.Sdk.VoiceAi.AudioSocket/Internal/AudioSocketFrameCodec.cs`, `AudioSocketFrameType.cs`,
  and `AudioSocketClient.cs`, whose frame-building must move with the codec.
- Test frame builders in both packages, which currently encode the wrong format.
- `docker/functional/asterisk-config/extensions.conf` and a new functional test.
- **Breaking by construction, and harmless in practice.** Both enums are in
  `PublicAPI.Shipped.txt` and enum values are inlined by the compiler, so a consumer that pinned the old
  values must recompile. But no consumer could have depended on them for anything that worked: the old
  values describe a protocol no Asterisk speaks. `Verbara.Sdk.Pro.AgentAssist` resolves frame types
  through the `AudioSlin12..192` series, which does not change, and a symbolic `Audio` fallback; it
  recompiles without an edit. Pro and Platform both pin SDK 2.4.0 and see nothing until they move.
- **`### Changed — BREAKING`**, and a `minor` under the repository's own rule that a `Changed — BREAKING`
  never ships in a patch.

## Architectural Risk

- **Level:** MEDIUM. The change itself is small and the evidence is exact, but it alters a wire format
  on two shipped public types.
- **Affected:** both AudioSocket packages and anything that has implemented the SDK's invented dialect
  against them. That last population is believed empty — the dialect cannot talk to Asterisk — but it is
  believed, not known, so the CHANGELOG says so plainly.
- **Mitigation:** the captured-bytes fixture is the test that would have failed in March, and it is
  written before the fix. The functional test closes the loop end to end against a real Asterisk. Both
  packages change together so the two parsers cannot drift again while one is fixed.
- **Residual, recorded and not closed here:** the ARI AudioSocket path has a second, independent defect —
  `_streams` is keyed by the wire UUID while its only consumer looks the stream up by ARI channel id
  (`Activities/ExternalMediaActivity.cs:67`) — so that route still cannot work end to end after this
  change. And `Diagnostics/AudioStreamMetrics.cs` declares ten instruments with no production call site.
  Both are tracked separately; fixing the wire format is the prerequisite for either being worth doing.
