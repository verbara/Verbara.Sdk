# Design — stt-cancellation-test-fence

Backlog change: finalized at apply time.

- Ground first: confirm where each provider's `StreamAsync` first awaits (shared base vs
  per-provider) and whether `ct.ThrowIfCancellationRequested()` at iterator entry suffices for all
  four (Deepgram/Whisper/Azure/Google) or the seam lives in a shared enumerator helper.
- Follow the C1→C3 fence patterns (Platform openspec/specs/test-determinism): causal contract over
  timing; no `Task.Delay`, no racing wall-clock.
- Constraints: AOT-first (no reflection), TreatWarningsAsErrors, test naming
  `Method_ShouldExpected_WhenCondition`. Public-API note: iterator-entry throw is a behavioral
  clarification of the existing cancellation contract, not a breaking signature change — but being
  the public MIT root, call it out in the CHANGELOG.
- References: verbara-meta/ADR-0004 (adopt-on-touch convergence), Platform living spec
  `test-determinism` (fence patterns).
