# provider-contract-fidelity — Delta

## ADDED Requirements

### Requirement: A vendor's published contract is a first-class evidence class, vendored and pinned
A provider's own machine-readable wire contract, obtained under an open license, SHALL be treated as
evidence of equal standing to a recorded response, and MUST be vendored into this repository at an
explicit commit pin rather than fetched at test time. A terms verdict on a vendor's **Output** does
not govern its **published specification**; the two are separate artifacts under separate licenses and
MUST be assessed separately. Every vendored contract MUST carry a provenance sidecar recording the
source repository, the pinned commit, the SPDX license identifier and the path of the license file it
was read from — the sidebar label of a hosting service is not evidence of a license.

#### Scenario: A surface blocked for recording is still covered by its contract
- **GIVEN** a provider whose Output is not cleared for capture
- **WHEN** that provider publishes its wire contract under an open license
- **THEN** the contract is vendored and used as the field-set authority, because the terms verdict never applied to it

#### Scenario: A contract without a sidecar cannot merge
- **GIVEN** a vendored contract file
- **WHEN** it carries no provenance sidecar, or the sidecar omits the pin or the license
- **THEN** the suite fails, because a third-party file in a public repository with unrecorded provenance is a compliance defect regardless of whether anything reads it

#### Scenario: The rule is enforced by a checker that exists
- **GIVEN** that the only automated rule over recording provenance today scans for credential-shaped strings
- **WHEN** this requirement is implemented
- **THEN** a sidecar validator is written and wired into the same lane, covering captures and vendored contracts alike — because a requirement satisfied by "extend the existing checker" would be satisfied by nothing

#### Scenario: Attribution obligations are discharged where they attach
- **GIVEN** a contract under a license that requires attribution, a license copy, or an indication of changes
- **WHEN** the contract or anything derived from it is distributed with this repository
- **THEN** each of those obligations travels with it, in the sidecar and in the derived artifact, identified per license rather than treated as one generic notice

### Requirement: A fixture derived from a contract declares that provenance and its limits
A test fixture built from a vendor's published contract rather than from its traffic SHALL declare
`class: "spec-derived"` in its provenance sidecar, together with the contract source, its pinned
commit and its license. The sidecar MUST state plainly what the fixture is and is not: the field set,
the optionality and the shape are the vendor's, the values are locally authored. A `spec-derived`
fixture MUST NOT be described, in the sidecar or in the test that consumes it, as if it were recorded
traffic.

#### Scenario: A reviewer can tell the three classes apart
- **GIVEN** three fixtures for three providers
- **WHEN** a reviewer reads their sidecars
- **THEN** each says whether its bytes are the vendor's traffic, the vendor's contract, or locally invented — and no fixture implies a fidelity it does not have

#### Scenario: Deriving from a contract beats transcribing from prose
- **GIVEN** a surface for which both a machine-readable contract and prose documentation exist
- **WHEN** a fixture is authored for it
- **THEN** it is derived from the contract, because a human transcribing prose reproduces the same shared-misreading failure the fixture exists to detect

#### Scenario: A contract that carries no required-set says so
- **GIVEN** a vendored contract whose response schemas declare no required fields
- **WHEN** a sibling change looks to it for authority to mark a member required
- **THEN** the sidecar records that it supplies field set and shape but no required-set, so the absence is a stated limit rather than a silent one

### Requirement: Contract drift is detected on a schedule, against a declared subset
A scheduled job SHALL re-fetch every vendored contract at its upstream head, compare it against the
pin, and report a difference. The comparison MUST be scoped by a checked-in scope manifest naming the
message and schema **subtrees** the SDK models, and MUST NOT diff the whole document — a
bot-regenerated contract accumulates content change in parts the SDK does not read, and an alarm that
fires on those is muted and then reports nothing while appearing green. Scoping MUST be by subtree
rather than by leaf field, so that a field **added** to a message the SDK reads is in scope. The
manifest MUST be machine-checked in both directions: every type reachable from a provider
serialization context maps to at least one manifest entry, and every manifest entry resolves in the
vendored contract. The job MUST NOT run on the pull request path and MUST NOT gate the merge queue: a
vendor's release cadence is not a contributor's problem.

#### Scenario: A vendor renaming a modelled field turns something red
- **GIVEN** a field the SDK models and pins
- **WHEN** the vendor renames it upstream
- **THEN** the scheduled job fails and publishes the diff, so the change is found by a job rather than by a production incident

#### Scenario: A vendor adding a field to a modelled message turns something red
- **GIVEN** a message the SDK models
- **WHEN** the vendor adds a field to it upstream
- **THEN** the job fails, because an addition is at least as common a shape change as a rename and a leaf-scoped comparison would make it invisible by construction

#### Scenario: Churn outside the declared subset stays quiet
- **GIVEN** an upstream edit to a part of the contract the SDK does not model
- **WHEN** the scheduled job runs
- **THEN** it passes, because an alarm that fires on irrelevant change is one that will be ignored when it matters

#### Scenario: The scope cannot rot away from what the SDK reads
- **GIVEN** a new DTO added to a provider serialization context
- **WHEN** no scope-manifest entry covers it
- **THEN** the governance test fails, so the scope is maintained with the code rather than discovered to be stale after a missed drift

#### Scenario: Vendor cadence never blocks a merge
- **GIVEN** an upstream contract change landing mid-review
- **WHEN** an unrelated pull request runs CI
- **THEN** nothing about the vendor's contract is consulted, and the merge queue is unaffected

#### Scenario: The detector is proven in every direction before it is trusted
- **GIVEN** the drift job
- **WHEN** it is accepted
- **THEN** an in-scope rename has been observed making it fail, an in-scope addition has been observed making it fail, and an out-of-scope edit has been observed leaving it green — so its scoping is demonstrated rather than asserted

### Requirement: A surface with no obtainable contract records the gap
Where no openly-licensed machine-readable contract exists for a provider surface, that MUST be
recorded per surface with the reason, and MUST NOT be substituted with a transcription of the vendor's
prose documentation presented as equivalent. An absent contract MUST read as a known, attributed hole
in coverage rather than as a surface nobody examined. A surface MUST NOT be recorded as a gap on a
protocol assumption that the SDK's own client contradicts.

#### Scenario: An unobtainable contract is distinguishable from an unexamined one
- **GIVEN** a provider with no contract in the repository
- **WHEN** a later reader asks why
- **THEN** they find the reason recorded against that surface — no specification is published, or the published one describes a retired version — rather than having to repeat the search

#### Scenario: A stale generated client is not treated as a current contract
- **GIVEN** a vendor whose only machine-readable artifact describes a superseded API version
- **WHEN** coverage for that surface is considered
- **THEN** it is recorded as uncovered, because a contract for a retired version would fence the parser against a protocol the SDK does not speak

#### Scenario: The recorded reason matches the transport the SDK actually uses
- **GIVEN** a surface excluded because its protocol is said to be unsuitable
- **WHEN** the exclusion is recorded
- **THEN** the client in `src/` is read first, so a REST client is not excluded as a streaming one and a surface with no client at all is not listed as coverage debt

## Architectural Risk

**Level:** LOW-MEDIUM.

**Affected:** the repository's license posture — third-party licensed material is checked into a
public MIT repository — and one scheduled CI job. No production code, no public API, no PR-path gate,
nothing that cascades downstream.

**Mitigation:** the compliance surface is bounded and made machine-checkable, which requires building
the checker: today the only automated rule over recording provenance scans for credential-shaped
strings, and nothing verifies a sidecar exists or is well-formed. This requirement set writes that
validator and applies it to captures and contracts alike. The strong attribution obligation in the set
— CC-BY-4.0 — is discharged in both the sidecar and every derived artifact, and Apache-2.0's license
copy travels with its material. The technical risk is not false alarms but ignored ones, which is why
the diff is required to be scoped by a declared, machine-checked subtree manifest and to be
demonstrated failing on an in-scope rename and an in-scope addition and passing on an out-of-scope
edit before it is accepted. Because the job is scheduled-only, every failure mode is a missing or
noisy signal, never a blocked merge.
