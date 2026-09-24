# ADR-0060: The AudioSocket wire format is Asterisk's, and no parser is held to its own encoder

- **Status:** Accepted
- **Date:** 2026-09-23
- **Deciders:** Harol A. Reina H.
- **Supersedes:** [ADR-0017](0017-audiosocket-codec-negotiation.md) (AudioSocket codec negotiation) —
  which is left verbatim, because what it records is a design that was never built and that history
  is the finding
- **Related:** ADR-0048 (wire conformance established against the vendor by live probe, never against
  our own fixtures — written for provider APIs, and this is the same rule turned on a protocol this
  repository implements twice), ADR-0041 D7 (a test dependency's transitive graph is confined to the
  suites that need it — why the shared capture is its own dependency-free project), ADR-0051 and
  ADR-0043 (the functional lane runs off the PR path), ADR-0023 (`PublicAPI.*.txt`, which is what
  made the enum change state itself instead of landing quietly), ADR-0005 (Testcontainers is the
  functional substrate, so "a real Asterisk" is a container this repository builds)

## Context

AudioSocket is Asterisk's TCP bridge between the dialplan and an external audio application. This
repository implements it **twice** — `Verbara.Sdk.Ari.Audio.AudioSocketProtocol` and
`Verbara.Sdk.VoiceAi.AudioSocket.Internal.AudioSocketFrameCodec` — and until this change **neither
implementation could complete a handshake with any Asterisk**.

### What Asterisk sends, measured

A probe against Asterisk 22.9.0 with the stock `res_audiosocket.so` / `app_audiosocket.so`,
originating a real call into `AudioSocket(<uuid>,127.0.0.1:9092)` against a bare listener that
recorded every byte:

```text
BYTES RECEIVED: 19
HEX: 01 00 10 | 11 11 11 11 22 22 33 33 44 44 55 55 55 55 55 55
```

**Three bytes of header: one byte of type, two of big-endian length.** `01` is identification,
`00 10` is 16, and the sixteen bytes that follow are the dialplan's UUID in RFC 4122 order. Further
runs captured the rest:

| kind | header, verbatim | what it is |
|---|---|---|
| identification | `01 00 10` + 16 bytes | the dialplan's UUID, most significant byte first |
| audio | `10 01 40` + 320 bytes | 160 samples of 8 kHz signed linear, one 20 ms frame |
| DTMF | `03 00 01` + `31` | one ASCII digit; `SendDTMF(1234,…)` gave `31 32 33 34` |
| hangup | — | **never arrived**; see R5 |

**1,411 frames across the runs parsed with that header and left zero trailing bytes** — 304 frames
over 97,888 bytes in the audio run, 5 over 35 bytes in the DTMF run, and a longer run of the same
shape that produced 701 audio frames. A header that is wrong by one byte does not leave a stream
accounted for exactly; this one does.

### What the two implementations believed

Both read a **four**-byte header — `HeaderSize = 4` in each, the second commented "1 type +
3 length" — and both took `0x01` for audio and `0x00` for the UUID. So the first packet of every
session was read as an audio frame of length `0x111111`, the UUID never arrived, and Asterisk logged
the only thing it could:

```text
app_audiosocket.c:221 audiosocket_run: Reached timeout after 2000 ms of no activity on
AudioSocket connection between 'Local/400@default-00000000;2' and '127.0.0.1:9092'
```

The ARI enum was worse than wrong in one specific way: it carried `Error = 0x10`, the byte Asterisk
uses for 8 kHz audio, and its read pump *returns* on `Error`. Even with a correct header it would
have torn the session down on the first audio frame.

### Why it survived six months of green tests

Three independent absences, and none of them is about care:

- **Nothing in the repository cited the protocol's source.** `grep -rl res_audiosocket` returned
  nothing. The format was written into a sprint design document with no citation and both
  implementations were written from that document. A reader could inherit the claim; no reader could
  check it.
- **The functional dialplan had no `AudioSocket()` extension.** It covered FastAGI, Stasis, queues
  and echo. Nothing in this repository had ever put a real `res_audiosocket.so` on the other end of
  the socket, so the end-to-end lane could not see the defect either.
- **The unit tests were a closed loop.** Every test in both packages built its input with the codec
  under test and then asserted that same codec read it back. **A test written that way passes
  whatever the bytes on the wire actually are.** Both suites were green, continuously, against a
  header no Asterisk has ever sent.

That third absence is the one worth a decision. The first two are gaps; the third is a test design
that *cannot* fail for the defect it appears to cover, and it will be rebuilt by anyone who writes
the next parser without being told not to.

### The contradiction that needed no external source

`AudioSocketFrameType` declared `AudioSlin12 = 0x11` through `AudioSlin192 = 0x18` — the extended
per-rate codes, taken from Asterisk — beside a base `Audio = 0x01`. If `0x11` is slin12 then the
8 kHz code immediately below it is `0x10`, not `0x01`. The file had been stating its own error to
every reader since it was written. Nobody had a reason to read it that way, because nothing in the
file said where any of the numbers came from.

### What ADR-0017 recorded

ADR-0017 is **Accepted** and describes an 18-byte header, frame types (`.Slin16`, `.Ulaw`, `.Alaw`,
`.Gsm`) that are members of no enum in this repository, and per-connection codec negotiation from
the first inbound audio frame. The code assigns the session format from `AudioSocketOptions` and has
never read it off the wire. It is not a stale ADR about a decision that later changed: **it is a
record of a design that was never built**, carrying the authority of `Accepted` for six months, in
the one place a reader would go to check the format. Editing it would destroy the only evidence of
how the wrong format acquired that authority.

## Decision

**R1 — the protocol's source is Asterisk's `res_audiosocket.h`, and it is cited where the format is
defined.** `enum ast_audiosocket_msg_kind` is the definition; this repository's enums are a copy of
it, and they say so in their own remarks, next to a pointer to the capture. A number a reader can
check is worth more than a number a reader must inherit, and the whole six months rests on the
difference.

**R2 — the frame is one byte of type, then two bytes of big-endian length, then the payload.** The
type values are Asterisk's, not ours:

| member | value | source |
|---|---|---|
| `Hangup` | `0x00` | the type table; **outbound only**, see R5 |
| `Uuid` | `0x01` | measured, twice |
| `Dtmf` | `0x03` | measured |
| `Audio` | `0x10` | measured |
| `Error` | `0xFF` | the type table |
| `AudioSlin12` … `AudioSlin192` | `0x11` … `0x18` | already correct, unchanged |

There is no `Silence` member, in either package. `0x02` is not a frame kind Asterisk defines; the
value this repository carried for it was invented here.

**R3 — no parser may be held to its own encoder.** A test that builds its input with the code under
test is not evidence about the wire, whatever it asserts, because it cannot fail when the parser and
the encoder share one wrong format. Where a format is external, the fixture is **bytes captured from
a real instance of the thing that speaks it**. This is ADR-0048's rule — conformance is established
against the vendor, never against our own fixtures — read one layer in: there the vendor is a remote
API, here it is a `.so` on the other end of a socket, and the failure mode is identical.

**R4 — one captured fixture serves every parser in the repository.** `AudioSocketWireCapture` lives
in `Tests/Verbara.Sdk.TestInfrastructure.Wire`, a project with zero `PackageReference`, and both
suites reference it. A per-package copy is not a smaller version of this rule; it is the defect
returning, because two copies of a capture drift apart exactly as two parsers did, each toward what
its own package already accepts.

**R5 — the fixture only ever grows from a capture.** A hand-written entry is a guess wearing the
costume of evidence, which is the single thing the fixture exists to exclude. That rule is why
**there is no hangup frame in it**: two runs ended the call two different ways — `channel request
hangup all` from the CLI, and the far leg reaching `Hangup()` on its own — and in both the listener
saw the peer close the TCP connection with zero trailing bytes. Asterisk 22.9.0 closes the socket;
it does not send an inbound hangup frame. `Hangup = 0x00` stays in the table for the **outbound**
direction on the strength of the type table alone, and the fixture says so in as many words rather
than quietly implying a measurement.

**R6 — a real Asterisk dials both servers in the functional lane.** The dialplan carries an
`AudioSocket()` extension per implementation, each naming a UUID the matching test asserts verbatim.
This is the only place in the repository where the bytes are produced by Asterisk at the moment of
the assertion rather than replayed from a recording, and the only thing that would notice if a
future Asterisk changed the format. It runs in the merge queue and on the scheduled matrix, not on
every push (ADR-0051, ADR-0043).

## Consequences

- **Both enums change value, and that is a break by construction.** Enum values are inlined by the
  compiler, so a consumer that pinned the old ones must recompile. `PublicAPI.Unshipped.txt` in both
  packages carries the move — **11 removals and 10 additions**, both `PublicAPI.Shipped.txt` files
  untouched — which is ADR-0023 working: the change had to state itself.
- **The break is believed harmless and that belief is written down as a belief.** No consumer could
  have depended on the old values for anything that worked, because they describe a protocol no
  Asterisk speaks. That population is believed empty rather than known to be, and the CHANGELOG says
  so plainly instead of claiming otherwise.
- **One public member leaves the surface with no replacement.**
  `AudioSocketSession.WriteSilenceAsync` existed only to write a `Silence` frame. Keeping it would
  mean keeping a member Asterisk does not define, or casting a magic `0x02` onto the wire; both are
  the defect, preserved for the sake of a signature.
- **A two-byte length caps a frame at 65,535 bytes where the old three-byte length reached 16 MiB.**
  Left alone the corrected write path would have silently truncated a longer payload and put a frame
  on the wire whose declared length is not its own — this change's own defect class, newly
  introduced by it. Both `WriteFrame` paths now throw `ArgumentOutOfRangeException`, with a test
  each.
- **The old test suites are evidence of what the closed loop hid, and were counted rather than
  waved at.** 25 encoding sites across the two packages (21 frame builders, 2 UUID payload builders
  that wrote the `Guid` little-endian, 1 type-byte table, and 1 in the benchmark project), feeding
  **39 tests** that changed the bytes they assert on. Every one of them had been green for six
  months.
- **The fixture is the only artifact here that cannot be edited into agreement with a wrong
  parser.** Every mutation tried against the corrected code is also caught by tests outside the
  fixture — but only because those tests' literals were hand-edited to the correct header, and the
  correct header came from the capture. They are downstream of it, not independent of it: a future
  wrong "fix" would arrive with the same hand-edit to the same literals and the suite would go green
  again, which is precisely the six months this ADR is about.
- **R4 is not a preference, and the measurement that shows it was an accident of the mutation
  round.** The same one-line edit — `HeaderSize` back to `4`, to a constant of the same name in both
  packages — turned **4 of 4** VoiceAi fixture entries red and **0 of 5** ARI ones, because ARI's
  reader consumes three bytes with `TryRead` whatever the constant says while VoiceAi's uses it to
  size the header span. Two parsers answer the same edit differently; only a single shared fixture
  puts both verdicts on the same nineteen bytes, and a per-package fixture would have been
  maintained by whoever owns the package that cannot see the difference.
- **The functional lane now has a number.** Two tests, run locally on the Docker-gated lane:
  **Failed: 0, Passed: 2**, 523 ms and 556 ms, 17 s wall including container startup. As a negative
  control the two enums were swapped back to `Uuid = 0x10` / `Audio = 0x01` — the shape of the
  original defect — and **both tests failed**, each after waiting out its full 45 s handshake budget
  with no session ever arriving. That is the pre-change state reproduced on demand, and it is what
  the two tests are worth.
- **ADR-0017's codec negotiation is not replaced by anything, and nothing is lost.** The session
  format comes from options, as it always did in code. The per-connection negotiation ADR-0017
  described was never implemented, so this ADR removes a claim rather than a behaviour. The extended
  `AudioSlin12..192` series is the mechanism Asterisk actually offers for per-rate audio, and it is
  untouched.
- **Correcting the wire format does not make the ARI AudioSocket route work end to end.** Its stream
  table is keyed by the wire UUID while its only consumer looks the stream up by ARI channel id, and
  ten declared audio instruments still have no production call site. Both are tracked separately.
  This is their prerequisite, not their fix, and saying so here keeps a green functional test from
  being read as a claim it does not make.

## Alternatives considered

**Edit ADR-0017 to describe the real format.** Rejected, and this is the alternative the catalog's
own rule already forbids ("Once `Accepted`, never edit the body"). The reason is stronger than the
convention here: ADR-0017 is the *only* record of how a design nobody built came to carry the
authority of an accepted decision, in the exact place a reader checking the wire format would look.
An edit would leave the repository with a correct document and no evidence of the failure, which is
the half worth keeping.

**Fix the codecs and leave the tests as they were.** Rejected. The suites would have gone green
again — they were green before — and the next wrong format would be adopted the same way. The fix is
worth roughly nothing without R3; the capture is the change, and the two-line header correction is
its consequence.

**A copy of the captured bytes in each package's test project.** Rejected, and then measured:
see the `HeaderSize` asymmetry above. The two copies would have been maintained by the two packages
and would have drifted toward what each package's parser accepts — the closed loop reassembled out
of honest parts.

**Hand-write the hangup frame from the type table, so the fixture is complete.** Rejected. A
complete-looking fixture whose last entry is a guess is worse than an incomplete one that says which
entry is missing and why, because the guess is indistinguishable from the measurements sitting
beside it. The absence is itself a finding about Asterisk 22.9.0 and is recorded as one.

**Keep `Silence` and `WriteSilenceAsync` as deprecated members for source compatibility.** Rejected.
`0x02` is not a frame kind Asterisk defines. A deprecated member that still puts an invented byte on
a wire is not compatibility; it is the same defect with a warning attached, and the warning would
outlive the memory of why it is there.

**Derive the format from the archived sprint design document instead of from a capture.** Rejected —
that document is where the wrong format came from. An uncited secondary source is the mechanism
under examination, not an alternative to it.
