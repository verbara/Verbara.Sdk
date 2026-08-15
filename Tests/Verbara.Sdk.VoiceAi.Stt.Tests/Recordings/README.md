# Recordings — Verbara.Sdk.VoiceAi.Stt.Tests

Provider wire-format fixtures, replayed by this suite so that a parser bug caused by a shared
misreading of a vendor's wire format becomes visible. Every file here is world-readable, permanent in
Git history, and redistributed with every clone.

Two replay paths and two fixture classes coexist here, and the sidecar says which is which:

- **HTTP-transport providers** (`openai-whisper`, `azure-openai-whisper`, …) are replayed through the
  shared WireMock fixture. Their captures are real provider responses — `class: "recorded"`.
- **WebSocket-transport providers** (`deepgram-stt`, …) are replayed by their in-process protocol
  fake, because WireMock.NET cannot hold a duplex session (ADR-0041 D2). Where the vendor's terms do
  not clear a capture, the frames are authored to the vendor's *published protocol documentation*
  instead — `class: "synthetic"`, `terms.verdict: "not-applicable"`, plus a `source_schema` block
  naming the page they conform to. That route carries no vendor Output at all; see §7 of the guide.

**Do not add, edit or re-capture anything here without reading
[`docs/guides/provider-recording-protocol.md`](../../../docs/guides/provider-recording-protocol.md)**
— it carries the capture procedure, the redaction rule, the source-audio rule, the size cap and the
per-provider terms findings. Every capture needs a `*.provenance.json` sidecar; a capture without one
is not reviewable and must not be merged.

`scripts/capture-provider-recording.py` produces the *captured* HTTP fixtures here — it sends the
same multipart request the SDK sends, redacts, normalizes and writes the sidecar. Capture only with a
credential that has never seen production (guide §3.3). Documentation-derived fixtures are authored by
hand against the vendor's published schema and involve no credential and no request at all.

`scripts/check-recording-redaction.py` scans this tree in CI and fails the build on
credential-shaped content.
