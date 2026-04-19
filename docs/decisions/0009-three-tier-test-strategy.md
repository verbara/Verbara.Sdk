# ADR-0009: Three-tier test pyramid — Unit, Integration, Functional (Layer2/Layer5)

- **Status:** Accepted
- **Date:** 2026-04-19 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0005 (Testcontainers)

## Context

The SDK has three distinct testing concerns:

1. **Protocol correctness** — does `AmiProtocolReader.ParseSingleEvent()` produce the right `Dictionary<string, string>` for a given byte buffer? Pure, isolatable.
2. **Infrastructure correctness** — does `PostgresSessionStore.SaveBatchAsync()` actually round-trip JSONB and honor cancellation against a live Postgres? Requires a real daemon.
3. **Behavior correctness** — does `CallSessionManager` track `StasisStart` → `ConfBridgeJoin` → `Hangup` across a real Asterisk bridge? Requires a live PBX + channels.

Each concern has different CI cost, different failure modes, and different audiences (a protocol bug breaks unit tests fast; a behavior bug may only surface under specific Asterisk configuration).

## Decision

Tests live in **three tiers** classified by `[Trait("Category", ...)]`:

| Tier | Category | Runtime | Contains |
|------|----------|---------|----------|
| **Unit** | default (no category) | in-process, no Docker, < 60 s | `Tests/Asterisk.Sdk.*.Tests/` — per-package unit tests with mocks |
| **Integration** | `Category=Integration` | Docker required, ~30 s | `Tests/Asterisk.Sdk.IntegrationTests/`, `Tests/Asterisk.Sdk.Sessions.{Redis,Postgres}.Tests/` — spin up Redis/Postgres/Toxiproxy |
| **Functional** | `Category=Functional` | Docker + live Asterisk, ~15 min | `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/` + `Layer5_Integration/` — spin up `Dockerfile.asterisk` + SIPp + PSTN emulator |

Within Functional, **two sub-layers**:

- **Layer2 — Unit Protocol.** Protocol parsing/writing exercised against a live Asterisk response (wire-level fidelity, no domain state).
- **Layer5 — Integration.** End-to-end scenarios: bridge creation, queue join, agent state transitions, MOH, recordings. Uses `FunctionalFixture` / `RealtimeFixture`.

Default `dotnet test` run uses `--filter "Category!=Functional&Category!=Integration"` — the fast loop for developers.

## Consequences

- **Positive:** A contributor iterating on an AMI event parser gets < 60 s feedback (unit tests only). A contributor touching `CallSessionManager` runs the full 15-min matrix before pushing. CI workflows map 1:1 to tiers: `unit-tests` job + `aot-check` job + `functional-tests` job (separate timeouts). Flaky Docker issues can't block unit-test feedback.
- **Negative:** Three test projects per SDK package (the Sessions packages have unit + integration split — a minor duplication of fixtures). Layer2 vs Layer5 boundary is occasionally blurry; judgment call per test.
- **Trade-off:** We accept a slightly larger test matrix for the confidence that "fast tests protect quick iteration, slow tests protect production."

## Alternatives considered

- **Two-layer (Unit + Integration)** — rejected because behavior tests (live PBX) have fundamentally different flake profiles and runtime budgets than fixture-based integration tests. Merging them forced every contributor to run Docker for every push.
- **Single integration suite** — rejected because a ~15-min default run would kill the feedback loop for trivial changes.
- **Four layers** (adding "contract" or "smoke") — rejected as diminishing returns; Layer2/Layer5 inside Functional already captures the "protocol vs behavior" split.

## Notes

- Category filter lives in each `*.csproj` test file via `[Trait("Category", "...")]` attributes; no global config.
- `Tests/Asterisk.Sdk.TestInfrastructure` ships the 5 container wrappers + 3 fixtures shared across Layer5 and Integration.
- CI enforcement: `.github/workflows/ci.yml` runs the three tiers as named jobs with independent timeouts (Unit 15 min, Functional 30 min).
- Baseline counts as of 2026-04-18: 2,643 unit / 65 integration / 148 functional.
