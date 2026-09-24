# ADR-0061: Only a `Changed — BREAKING` entry forces a minor

- **Status:** Accepted
- **Date:** 2026-09-24
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0028 (cadence commitment — **this ADR amends its post-v2.0 commitment 3**),
  ADR-0050 (the first behavioural break released as a minor, and the origin of the written rule),
  ADR-0052 F4 (restates it), ADR-0055 (the version is cut at release time, so a tier ruling binds the
  release and not the diff), ADR-0060 (the most recent `Changed — BREAKING`, and the migration guide
  its tier obliged), ADR-0048 D7 (reads as a tier rule and is not one — see *Context*), ADR-0027 (the
  stewardship pledge, whose constraint on removing public MIT types this ADR does not touch).
  Harvested from the change `ari-failed-connect-and-silent-catches`, task 9.5.

## Context

Three records in this repository say a behavioural break takes a minor, and the shipped history
contradicts them twice.

What the records say:

- **ADR-0028** (2026-04-20), post-v2.0 commitment 3: *"Patch cadence: mensual o por needs,
  **no-breaking por definición**."* In force since v2.0.0 shipped on 2026-05-06.
- **ADR-0050** (2026-08-17): *"It ships in a minor with an explicit callout, never in a patch."*
- **ADR-0052 F4** (2026-08-19): *"it is a behavioural break and gets a `BREAKING` CHANGELOG entry and
  a minor bump, matching how ADR-0050 itself shipped."*

What shipped. Every `BREAKING` heading in `CHANGELOG.md`, counted 2026-09-24:

| Release | Tier | `Fixed — BREAKING` | `Changed — BREAKING` |
|---|---|---|---|
| 2.5.0 (2026-08-24) | minor | 10 | 3 |
| 2.5.2 (2026-09-13) | **patch** | 2 | 0 |
| 2.5.3 (2026-09-13) | **patch** | 2 | 0 |
| `[Unreleased]` | — | 2 | 2 |

Both patches landed after all three records. And 2.5.2's is not a distant cousin of the class those
ADRs were written about: `Fixed — BREAKING: AmiConnection.ConnectAsync completed as Connected when
cancelled` is the AMI sibling of the ARI connect-state fix that ADR-0056 later shipped.

**The pattern nobody wrote down is narrower than any of the three records.** A
`Changed — BREAKING` has shipped three times and all three are in 2.5.0, a minor; it has never
shipped in a patch. A `Fixed — BREAKING` has shipped fourteen times, in both tiers. And 2.5.0's ten
did not cause its minor — the three `Changed — BREAKING` in the same release already forced it, so no
release in this repository's history has ever taken a minor *for* a `Fixed — BREAKING`.

Four `BREAKING` markers appear outside a heading (`CHANGELOG.md:532`, `:1415`, `:1488`, `:1848`).
Each sits under a `Fixed — BREAKING` heading and elaborates it. No break has reached a release
without a heading declaring it, so the headings are a complete index of this history.

**One record reads like a third tier rule and is not.** ADR-0048 D7 says *"a route fix that changes
the meaning of a public property is an API decision, not a patch"*. Its next sentence settles the
sense: *"never applied inline because the route is obviously wrong"* — "patch" there is an ad-hoc code
change, not a release tier. The case it names resolves the ambiguity anyway: `SpeechmaticsOptions.BaseUri`
shipped as one of 2.5.0's three `Changed — BREAKING` entries, in a minor, which is what D1 below
requires. A rule nobody had written was followed a fourth time.

**Why the split is not arbitrary.** The two labels make different promises about what the consumer
was relying on. `Fixed` says the documented behaviour was not being delivered and now is: the
consumer who depended on the old behaviour depended on something the SDK never promised. `Changed`
says the documented behaviour itself moved: the consumer depended on exactly what was promised, and
the promise was withdrawn. Those are different debts, and the history priced them differently
without ever saying so.

## Decision

**D1 — Only a `Changed — BREAKING` entry forces a minor.** A release whose breaking entries are all
`Fixed — BREAKING` MAY ship as a patch.

**D2 — This amends ADR-0028's post-v2.0 commitment 3.** *"No-breaking por definición"* now reads: a
patch carries no `Changed — BREAKING`. It may carry a `Fixed — BREAKING`, and when it does, the entry
keeps its label and its explicit callout — the tier is not what the label is for. The rest of
ADR-0028 stands untouched: the 8–12 minors/year cap, the annual major on the .NET cycle, the LTS
line, and the migration-guide obligation on a minor that carries a breaking change. ADR-0027's
constraint on removing public MIT types is untouched as well: this ADR prices behavioural breaks, and
says nothing about removing public surface, which is a separate promise under the stewardship pledge.

**D3 — The label is the decision, and it is made when the entry is written**, by the author, against
what the SDK documented before the change. An entry that cannot say which documented behaviour it
restores is a `Changed`.

**D4 — The tier binds the release, not the diff.** `Directory.Build.props` is cut at release time
(ADR-0055), so a ruling recorded against a pull request constrains the release that carries it and
changes nothing in that pull request.

**D5 — 2.5.2 and 2.5.3 are applications of this rule, not errors.** Recorded here so a reader
comparing those two releases against ADR-0028, ADR-0050 or ADR-0052 does not have to reconstruct
whether the repository broke its own commitment. It did not; the commitment was written more
broadly than the practice it described.

## Consequences

- **Positive, and the point: a defect fix can reach consumers in a patch.** Fourteen of the
  seventeen breaking entries this repository has released are `Fixed` — defects in provider
  integrations, cancellation accounting and audio handling, none of which withdrew a promise the SDK
  had kept. Pricing each at a minor would consume the 8–12 minors/year ADR-0028 caps on bug fixes,
  and would make the class of fix ADR-0050 exists to encourage the most expensive kind to ship.

- **Negative, and the headline: a label now carries the release tier, and nothing verifies it.** No
  test in this repository parses `CHANGELOG.md`; `publish.yml` reads only `## [<version>]` headings
  and never reads `[Unreleased]`. An author who wants a patch can write `Fixed` on an entry that
  moves a documented behaviour, and no gate will notice. The mitigation is D3 plus the callout text
  itself, which a reviewer can check against the diff — deliberately a review property rather than a
  gate, because a parser over prose headings would fail in the direction of blocking a correct
  release.

- **The pending release is unaffected.** `[Unreleased]` holds two `Changed — BREAKING` (#302's
  AudioSocket frame format and #291's `AriOutboundListener` accept behaviour), so 2.6.0 is a minor
  under D1 — the same answer already ruled on that change, now reached by a stated rule instead of a
  case-by-case reading.

- **ADR-0028's migration-guide obligation is now the binding constraint on that cut, and it is not
  yet met.** One guide exists, `docs/guides/audiosocket-wire-format-migration.md`, written for #302.
  Recorded here as owed rather than resolved, because this ADR settles the tier and the tier is what
  triggers the obligation.

- **This gives no procedure for choosing `Fixed` over `Changed` on a given entry.** D3 states the
  test — which documented behaviour does this restore — but the judgement stays with the author, and
  it now costs a release tier instead of nothing. That is a real increase in the stakes of a label
  that used to be presentational.

## Alternatives considered

1. **Reaffirm ADR-0028 as written, and record 2.5.2 and 2.5.3 as two violations.** Rejected.
   Fourteen of the seventeen breaking entries this repository has released are `Fixed` — the count
   excludes `[Unreleased]`. Under this reading each one costs a minor, the cap ADR-0028 sets is
   consumed by bug fixes, and the incentive runs against shipping exactly the fixes ADR-0050 was
   written to encourage. It also leaves two shipped releases standing as errors that no consumer was
   harmed by.

2. **Declare that every breaking change takes a minor, in one place, and delete the three
   scattered statements.** Same effect as 1, with tidier records. Rejected for the same reason.

3. **Decide the tier per release by measured blast radius.** Rejected: no written procedure, nothing
   to check a past release against, and the measurement would be made by the same author whose label
   it replaces.

4. **Leave it unwritten and keep ruling case by case.** Rejected: the contradiction now sits in three
   accepted records and two shipped releases, and the next reader who finds it has to redo the
   reading this ADR is the result of.
