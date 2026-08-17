# Recordings — Verbara.Sdk.VoiceAi.Tts.Tests

Provider wire-format fixtures, replayed by this suite so that a parser bug caused by a shared
misreading of a vendor's wire format becomes visible. Every file here is world-readable, permanent in
Git history, and redistributed with every clone.

Two replay paths and three fixture shapes coexist here, and the sidecar says which is which:

- **HTTP-transport providers** (`azure-tts`, `speechmatics-tts`, `lmnt-http`) are replayed through the
  shared WireMock fixture. `azure-tts` and `speechmatics-tts` are real provider responses, bytes
  included — `class: "recorded"`.
- **`lmnt-http` is a pair, and only one half is the vendor's.** LMNT is `not-cleared`, so
  `synthesize-short-en-us.json` is the response **envelope** — real status, real header names, the
  vendor's own declared media type, real content length, real read boundaries — with the audio counted
  and discarded rather than written (`class: "recorded"`). The body served under it,
  `body-pcm-s16le-16khz.raw`, is a locally computed tone (`class: "synthetic"`). The stub takes its
  status and media type from the envelope, so the client meets the declaration it meets in production;
  what the pair cannot prove is anything about the content of LMNT's speech.
- **WebSocket-transport providers** (`cartesia-tts`, `deepgram-tts`, `elevenlabs-tts`, `lmnt-ws`) are
  replayed by their in-process protocol fake, because WireMock.NET cannot hold a duplex session
  (ADR-0041 D2). None of them has a capture credential in this environment, and three of the four
  (`deepgram-tts`, `elevenlabs-tts`, `lmnt-ws`) are `not-cleared` on top of that, so their frames are
  authored to the vendor's *published protocol documentation* instead — `class: "synthetic"`,
  `terms.verdict: "not-applicable"`, plus a `source_schema` block naming the page they conform to.
  That route carries no vendor Output at all; see §7 of the guide.

**The `.raw` files whose sidecar says `class: "synthetic"` are not anyone's speech.** They are
signal-generator tones this repository computes for itself — a triangle wave rendered by
`SyntheticPcm.Triangle` from three parameters recorded in each sidecar's `source_audio.description`, so
the bytes are reproducible rather than magic. Each provider's
`…ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally` test regenerates its file and
compares byte-for-byte. Their lengths are deliberately **not** multiples of the buffer that will read
them — the WebSocket fakes' 320-byte frame size, or `LmntSpeechSynthesizer`'s 8192-byte HTTP read for
`lmnt-http` — so a partial final chunk reaches the consumer.

**Do not add, edit or re-capture anything here without reading
[`docs/guides/provider-recording-protocol.md`](../../../docs/guides/provider-recording-protocol.md)**
— it carries the capture procedure, the redaction rule, the source-audio rule, the size cap and the
per-provider terms findings. Every capture needs a `*.provenance.json` sidecar; a capture without one
is not reviewable and must not be merged.

`scripts/capture-provider-recording.py` produces the *captured* HTTP fixtures here — it sends the
same request the SDK sends, redacts, normalizes and writes the sidecar. Capture only with a
credential that has never seen production (guide §3.3). Documentation-derived fixtures are authored by
hand against the vendor's published schema and involve no credential and no request at all.

`scripts/check-recording-redaction.py` scans this tree in CI and fails the build on
credential-shaped content.
