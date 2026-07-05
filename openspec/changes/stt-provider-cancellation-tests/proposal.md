---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: CI reliability (all downstream repos gate on Sdk CI)
decision_ref: Sdk/ADR-0004
---

# Proposal: stt-provider-cancellation-tests

## Why

`stt-cancellation-test-fence` (PR#77, merged 2026-07-05) made cooperative cancellation
deterministic (`ct.ThrowIfCancellationRequested()` at iterator entry) across all **7** STT
streaming recognizers — Deepgram, Whisper, AzureWhisper, Google, Speechmatics, AssemblyAI,
Cartesia — and the `test-determinism` living spec's requirement is provider-agnostic ("STT
streaming recognizers SHALL..."). But only **2** of the 7 providers (Deepgram, Whisper) actually
had a `StreamAsync_ShouldAbort_WhenCancelled` test pre- and post-change; the other 5
(AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia) got the production fence with no test
asserting it. That gap was flagged as an explicit out-of-scope follow-up in the PEQUEÑO fence's
`tasks.md` (2.2) rather than silently dropped.

## What Changes

Add a `StreamAsync_ShouldAbort_WhenCancelled` test per remaining provider (AzureWhisper, Google,
Speechmatics, AssemblyAI, Cartesia), following the deterministic (pre-cancelled token, no
mid-flight race) pattern the fence established for Deepgram/Whisper — closing the coverage gap on
the `test-determinism` requirement so it is enforced, not just implemented, for all 7 providers.

## Capabilities

### Modified Capabilities

- `test-determinism`: no new requirement text — this closes test coverage on the existing
  "STT streaming observes cancellation deterministically" requirement for the 5 providers it
  did not yet cover.

## Impact

`Tests/Verbara.Sdk.VoiceAi.Stt.Tests` only (5 new test methods, one per provider suite). No
production code change expected — the iterator-entry fence already applies uniformly.

## Architectural Risk

**Level:** LOW. **Affected:** test suite only (`Verbara.Sdk.VoiceAi.Stt.Tests`) — no production
code path. **Mitigation:** mirrors the already-proven Deepgram/Whisper pattern; `dotnet test`
zero-warnings gate unchanged.
