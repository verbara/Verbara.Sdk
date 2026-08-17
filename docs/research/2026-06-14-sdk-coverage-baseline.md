# Sdk coverage baseline (coverage-ratchet replication)

**Date:** 2026-06-14
**Context:** P2 SP3 — coverage ratchet, replicated from the Platform pilot.
**Pilot spec:** `Verbara.Platform/docs/specs/2026-06-14-coverage-ratchet-pilot.md`.

## Result

| Metric | Value | Note |
|--------|-------|------|
| **Line coverage** | **80.45%** | the gated metric |
| Branch coverage | 66.05% | advisory only |
| Assemblies | 25 | production `src/` assemblies the unit job exercises |
| **Floor set** | **78** | `⌊80.45⌋ − 2` (2-point slack) |

All unit projects green. Subset = CI's unit filter
`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`.

## Excludes (`coverlet.runsettings`)

Beyond test assemblies / generated code / migrations, three `src/` assemblies are
excluded because their **only** tests are container-backed (Testcontainers), so they are
not in the unit subset — counting them at 0% would dilute and mask regressions:

- `Verbara.Sdk.Cluster.Postgres`
- `Verbara.Sdk.Sessions.Postgres`
- `Verbara.Sdk.Sessions.Redis`

Checked and **kept** (they do have unit coverage, not container-only): `Verbara.Sdk.Data.Npgsql`
(98.4%) and `Verbara.Sdk.Push.Nats` (53.1%). Also excluded: `*.TestInfrastructure`, `*.Benchmarks`.

## CI integration note

Sdk's `ci.yml` already triggers on `pull_request` + `merge_group` (no `push:[main]`) and is
gated by the `main-merge-queue` ruleset — so the coverage job inherits the merge-queue model
and does **not** double-run (unlike Platform/Pro/Web, which used `push:[main]`).

## Reproduce / raise floor

Same recipe as the Platform baseline doc. Raise `coverage-floor.json` manually in a normal
PR when coverage improves, keeping ~2 points of slack.

## Measured 2026-08-17 — two assemblies are silently not measured

`Verbara.Sdk.VoiceAi.Stt.Tests` and `Verbara.Sdk.VoiceAi.Tts.Tests` do not measure the
`Verbara.Sdk.VoiceAi` assembly at all. Coverlet reports an instrumentation **failure**, not an
exclusion (an excluded module logs `Excluded module:` instead):

```
[coverlet]Unable to instrument module: …/Tests/Verbara.Sdk.VoiceAi.Stt.Tests/bin/Release/net10.0/Verbara.Sdk.VoiceAi.dll
[coverlet]Unable to instrument module: …/Tests/Verbara.Sdk.VoiceAi.Stt.Tests/bin/Release/net10.0/Verbara.Sdk.VoiceAi.AudioSocket.dll
```

Both are ordinary `ProjectReference` dependencies with their PDBs sitting next to them, and the same
`Verbara.Sdk.VoiceAi.dll` instruments cleanly under `Verbara.Sdk.VoiceAi.Tests` and
`Verbara.Sdk.VoiceAi.TurnDetection.Tests` — so this is per-consumer, not per-module. Reproducible on
a single-project run:

```sh
dotnet test Tests/Verbara.Sdk.VoiceAi.Stt.Tests/ \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

The resulting Cobertura lists only `Verbara.Sdk.Audio` and `Verbara.Sdk.VoiceAi.Stt`. Read the
warning with `--diag`; the console output says nothing.

**Consequence for the gates.** Any type in `Verbara.Sdk.VoiceAi` whose only exercisers live in those
two suites reads as 0% covered however thoroughly it is tested. Not theoretical: it failed the patch
gate once, on a change whose new exception type is thrown by all eight WebSocket provider clients and
asserted on by more than twenty of their tests. The work-around is to assert such a type's behaviour
from a suite that does instrument the assembly (`Verbara.Sdk.VoiceAi.Tests`). The fix — finding out
why the instrumenter refuses those two modules under those two consumers — is not yet diagnosed.
