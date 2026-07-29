# ADR-0040: SDK pin cascades are their own train, never folded into a release train

- **Status:** Accepted
- **Date:** 2026-07-28
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0004 (central package management), ADR-0028 (cadence v1 preview / v2 stable),
  ADR-0037 (cross-repo ADR reference convention), ADR-0039 (Dependabot CI load),
  `Verbara.Sdk.Pro/ADR-0001` (SDK adoption cadence pattern),
  `Verbara.Platform/ADR-0001` (consumer dual-prong dependency pattern)

## Context

`Verbara.Sdk` is the root of the NuGet chain (`Sdk` → `Sdk.Pro` → `Platform`). Every version this
repo publishes becomes a **dependency floor** in the packages the two downstream repos restore
against. Three forces make advancing that floor non-routine:

1. **The floors are pinned twice.** ADR-0004 puts every NuGet version in `Directory.Packages.props`.
   Sdk, Sdk.Pro and Platform each pin `Microsoft.Extensions.*` **directly** — they do not merely
   receive it transitively. When an Sdk release raises that floor, a consumer that bumps only its
   `Verbara.Sdk.*` pins ends up with a direct pin *below* the transitive floor its own dependency
   just declared. That is `NU1605` (package downgrade).

2. **`NU1605` is fatal, not advisory.** ADR-0004 sets `TreatWarningsAsErrors=true`; neither
   Sdk.Pro nor Platform lists `NU1605` in `NoWarn` (Platform suppresses `NU1902`/`NU1603` and
   deliberately leaves `NU1605` fatal). Platform additionally sets
   `CentralPackageTransitivePinningEnabled=true`, which is precisely why the collision surfaces as
   an error rather than resolving upward in silence. A naive pin swap does not degrade — it fails
   the build.

3. **Dependency-only releases look free and are not.** `2.4.0` (2026-07-26) shipped **zero** API
   change: it moved `NATS.Client.Core` 2.8.2 → **3.0.0** (a major), the OpenTelemetry family to
   1.17.0, and the `Microsoft.Extensions.*` group 10.0.9 → 10.0.10. Nothing about that release
   reads as risky from this repo's side, yet consuming it requires a coordinated, multi-repo,
   floor-aligning commit in each consumer.

Two gaps made this repeatable trouble rather than a one-off:

- **The rule had no home.** The operative constraint — *this cascade is its own train, never folded
  into a release train* — was written down only as an italic clause in `Verbara.Platform`'s
  `docs/roadmap.md` header and as row R-013 in `verbara-meta`'s roadmap. A roadmap row is a
  schedule, not a decision; both get rewritten, and neither is where an engineer looks for "why".
- **ADR-0028 promised a compatibility matrix per release and it was never produced.** No
  `Sdk × Pro × Platform` table exists anywhere in this repo, so the current consumer pin state is
  discoverable only by reading three `Directory.Packages.props` files.

`.github/dependabot.yml` in both consumers already encodes half of this decision implicitly:
`Verbara.Sdk.*` is on the `ignore` list because "the pin is advanced deliberately as part of the
cross-repo release cadence (Sdk → Pro → Platform), not by Dependabot". This ADR records the other
half — what "deliberately" actually obliges.

## Decision

An **SDK pin cascade is a first-class cross-repo change of its own**, scoped, proposed and applied
independently of any release train. Concretely:

- **D1 — Publishing does not cascade.** A release from this repo advances only this repo. It never
  implies, schedules, or authorizes a consumer pin bump. Consumers stay on their current pin until
  a cascade is explicitly opened.
- **D2 — Floors move in the same commit as the pin.** A consumer bumping `Verbara.Sdk.*` MUST, in
  the same commit, re-align every direct pin the Sdk release raised as a transitive floor
  (`Microsoft.Extensions.*` today). Splitting the two across commits leaves `main` red under
  `TreatWarningsAsErrors`.
- **D3 — Never folded into a release train.** A cascade does not ride along with a feature release
  in Pro or Platform. `/xr:release` cuts versions of what is already on `main`; a cascade *changes*
  what is on `main` and carries its own restore/build/AOT risk. Mixing the two makes a failed
  restore indistinguishable from a failed release.
- **D4 — Scope is declared, not inferred.** Test-only packages on a different servicing track
  (e.g. `Microsoft.Extensions.TimeProvider.Testing`, which both consumers pin at `10.0.0`) are out
  of a cascade's scope unless named. A blanket `Microsoft.Extensions.*` rewrite is a defect, not a
  shortcut.
- **D5 — A major in the transitive closure is a review item, not a blocker.** When an Sdk release
  raises a *major* floor (NATS 3.x here), the cascade must state each consumer's actual source-level
  exposure to that dependency. Absent exposure, the risk reduces to restore resolution plus AOT
  publish behaviour, both of which existing CI gates already cover.
- **D6 — The cascade discharges ADR-0028's matrix obligation for the versions it touches.** Each
  cascade records the resulting `Sdk × Pro × Platform` triple in the consumer CHANGELOGs' existing
  `Dependency floors:` callout, which is the matrix in incremental form.

## Consequences

- Positive: the fatal-`NU1605` failure mode is written down where an engineer looks for "why", not
  inferred from a roadmap clause that the next roadmap edit deletes.
- Positive: releases from this repo stay cheap. Publishing a dependency bump no longer carries an
  unstated obligation on two other repos.
- Positive: D4 kills the most likely mechanical defect — a `sed` over `Microsoft.Extensions.*` that
  sweeps a test-only package onto a servicing band it does not belong on. Same for the
  `Verbara.Sdk.` prefix, which also matches `Verbara.Sdk.Pro.` in Platform's pin file.
- Negative: consumers lag the SDK baseline by design, sometimes for weeks. `2.3.2` → `2.4.0` sat
  unconsumed from 2026-07-26. That lag is now an accepted cost rather than a drift signal.
- Negative: one more ceremony. A one-line pin bump becomes a proposed change with its own branch,
  CI run and archive step.
- Neutral: this ADR governs *cadence*, not *content*. Whether to adopt a given SDK feature after
  the pin moves stays a per-consumer call (`Verbara.Sdk.Pro/ADR-0001`'s adopt-vs-defer framing).

## Alternatives considered

- **Option B: let Dependabot advance `Verbara.Sdk.*` like any other package** — rejected. Dependabot
  bumps one package family per PR; a cascade that must move `Verbara.Sdk.*` and
  `Microsoft.Extensions.*` atomically (D2) cannot be expressed that way, and each half alone is red.
  Both consumers already `ignore` these packages for exactly this reason.
- **Option C: fold the cascade into the next release train** — rejected. This was the implicit prior
  behaviour and the reason R-013 sat unscheduled: a release train is time-boxed and outcome-shaped
  ("cut v2.23.0"), while a cascade is risk-shaped ("does the graph still restore, build and publish
  AOT-clean"). Bundling them means a restore failure reads as a release failure and blocks a train
  that had nothing to do with it.
- **Option D: stop pinning `Microsoft.Extensions.*` directly in consumers and take it transitively**
  — rejected, but the closest call. It would make `NU1605` structurally impossible. Rejected because
  it inverts ADR-0004's premise (every version pinned, visible, reviewable in one file) and would
  leave consumers unable to take a security patch ahead of an SDK release.
- **Option E: record this in `verbara-meta` instead** — rejected. The constraint originates in this
  repo's publishing behaviour and in ADR-0004's pinning regime, both of which live here. The
  orchestration *mechanism* (`/xr:change`, `/xr:apply` staging) is verbara-meta's; the *rule about
  what an SDK release obliges* is this repo's.
