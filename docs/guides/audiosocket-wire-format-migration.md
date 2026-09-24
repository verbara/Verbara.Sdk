# Migrating to the corrected AudioSocket wire format

Required by ADR-0028: a minor that carries a breaking change ships a migration guide.

## What happened

Both AudioSocket implementations in this SDK read a frame header Asterisk does not send, and had done
since 2.0.0. **Neither server has ever completed a handshake with a real Asterisk.**

Asterisk sends a **three-byte header** — one byte of type, two of big-endian length. Both packages read
four. Both also believed `0x01` meant an audio frame and `0x00` the identification frame; Asterisk
sends the opposite. So the very first packet was read as an audio frame whose length came out of the
UUID's own bytes, the identification never arrived, and Asterisk closed the connection two seconds
later.

This is measured, not inferred. A probe against Asterisk 22.9.0 with the real `res_audiosocket.so`
captured 1,411 frames: identification as `01 00 10` plus sixteen bytes of UUID, audio as `10 01 40`
plus 320 bytes, DTMF as `03 00 01 31`.

## What you have to do

**Recompile.** That is the whole of it for anyone who used the SDK's own client and server types.

Enum values are inlined by the C# compiler, so a build pinned to 2.5.3 or earlier holds the old numbers
in its own IL even after you update the package. Rebuilding against the new package is what picks up the
corrected values.

## What changed, precisely

| member | was | is | why |
|---|---|---|---|
| `Uuid` | `0x00` | `0x01` | the value `res_audiosocket.h` defines |
| `Audio` | `0x01` | `0x10` | as above; the `AudioSlin12..192` series at `0x11..0x18` already implied it |
| `Error` | `0x04` (VoiceAi) / `0x10` (Ari) | `0xFF` | as above. The ARI value was the real audio code |
| `Hangup` | `0xFF` | `0x00` | as above |
| `Dtmf` | — | `0x03` | new: Asterisk sends it and neither package handled it |
| `Silence` | `0x02` | **removed** | Asterisk has no such frame |
| `AudioSocketSession.WriteSilenceAsync` | — | **removed** | wrote a frame kind that does not exist |
| `AudioSlin12` … `AudioSlin192` | `0x11`…`0x18` | unchanged | already correct |

Affected types: `Verbara.Sdk.Ari.Audio.AudioFrameType` and
`Verbara.Sdk.VoiceAi.AudioSocket.AudioSocketFrameType`.

## If you wrote your own client or parser against this SDK

You are the one case that needs a real edit rather than a rebuild, and only if you implemented the
SDK's previous format rather than Asterisk's. Change your header to one type byte plus two big-endian
length bytes, take the values from the table above, and parse the identification payload's sixteen
bytes **big-endian** — Asterisk sends RFC 4122 order.

We believe this population is empty, because the previous format cannot complete a handshake with
Asterisk. That is a reasoned claim and not a measured one, which is why this section exists.

## If you called `WriteSilenceAsync`

Remove the call. It wrote a frame Asterisk does not define and therefore never did anything useful. If
you needed to keep a stream alive through a quiet period, send audio frames of silence — an `Audio`
frame whose payload is zeroed samples in the negotiated format.

## How to check you are on the corrected format

Point an Asterisk dialplan at your server and look at the first bytes:

```
exten => 400,1,Answer()
 same => n,AudioSocket(<a-uuid>,<your-host>:<port>)
```

The first three bytes on the wire are `01 00 10`. If your code reads a four-byte header, it will see a
frame type of `0x01` with a length taken from the UUID's first bytes, and nothing after that will line
up.

The repository pins this with the captured bytes themselves, in a fixture shared by both packages'
tests, rather than with frames built by the code under test — which is the loop that let the wrong
format live for six months with every test green.
