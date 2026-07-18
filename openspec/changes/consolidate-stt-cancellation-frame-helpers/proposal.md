---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: CI reliability / test-suite maintainability (all downstream repos gate on Sdk CI)
decision_ref: verbara-meta/ADR-0004
---

# Proposal: consolidate-stt-cancellation-frame-helpers

## Why

`stt-provider-cancellation-tests` (PR#111, merged 2026-07-15) added a
`StreamAsync_ShouldAbort_WhenCancelled` test to the five remaining STT WebSocket suites, each of
which needs an `EndlessFrames()` frame generator to keep the stream open until the pre-cancelled
token is observed at the iteration boundary. That generator is now duplicated **verbatim** across
four WebSocket suites — Deepgram, AssemblyAI, Cartesia, Speechmatics
(`Tests/Verbara.Sdk.VoiceAi.Stt.Tests/{Deepgram,AssemblyAi,Cartesia,Speechmatics}/*RecognizerTests.cs`).

Adversarial review of PR#111 judged the duplication **acceptable in-change**: it matches the
existing per-class helper idiom (`SingleFrame`, `ThreeFrames` are duplicated the same way), so
promoting one helper to shared while leaving the others per-class would have been an inconsistent,
out-of-scope refactor for a PEQUEÑO test-coverage change. It was recorded as a **consolidation
candidate** to be harvested into the backlog rather than left only in review prose. This change is
that harvest.

## What Changes

Promote the duplicated cancellation-test frame generator to a single shared helper under the test
project's `Helpers/` directory (which already hosts `MockHttpMessageHandler.cs`), and have the four
WebSocket suites reference it instead of each carrying a private copy. The `Task.Delay(10, ct)`
pacer inside the generator keeps its `// fence-allow: LOOP-DRIVER` annotation (SyncFenceRegressionGuard
net-new ratchet) at the shared site — one annotated pacer instead of three copies.

Scope is deliberately limited to the `EndlessFrames` cancellation-test generator (the one this
change harvested). Whether to also consolidate the older per-class `SingleFrame`/`ThreeFrames`
idiom is left open — the review verdict only flagged `EndlessFrames`.

## Capabilities

### Modified Capabilities

- `test-determinism`: no change to the cancellation *contract* — this adds a maintainability clause
  requiring the shared cancellation-test frame generator to live in one place, so the seven-provider
  coverage the contract mandates is asserted through a single, non-duplicated helper.

## Impact

`Tests/Verbara.Sdk.VoiceAi.Stt.Tests` only (move one generator to `Helpers/`, update four suites'
references). No production code change. `dotnet test` zero-warnings gate unchanged.

## Architectural Risk

**Level:** LOW. **Affected:** test suite only (`Verbara.Sdk.VoiceAi.Stt.Tests`) — no production
code path, no public API. **Mitigation:** pure test-helper relocation; the deterministic
cancellation assertions and the `fence-allow: LOOP-DRIVER` annotation are preserved verbatim at the
shared site; `dotnet test` + zero-warnings gate proves behavioral equivalence.
