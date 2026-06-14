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
