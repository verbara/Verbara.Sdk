# Recordings — Verbara.Sdk.VoiceAi.Stt.Tests

Captures of real provider responses, replayed by this suite through the shared WireMock fixture so
that a parser bug caused by a shared misreading of a vendor's wire format becomes visible. Every file
here is world-readable, permanent in Git history, and redistributed with every clone.

**Do not add, edit or re-capture anything here without reading
[`docs/guides/provider-recording-protocol.md`](../../../docs/guides/provider-recording-protocol.md)**
— it carries the capture procedure, the redaction rule, the source-audio rule, the size cap and the
per-provider terms findings. Every capture needs a `*.provenance.json` sidecar; a capture without one
is not reviewable and must not be merged.

`scripts/capture-provider-recording.py` produces the captures in this tree — it sends the same
multipart request the SDK sends, redacts, normalizes and writes the sidecar. Capture with a throwaway
credential and revoke it immediately afterwards.

`scripts/check-recording-redaction.py` scans this tree in CI and fails the build on
credential-shaped content.
