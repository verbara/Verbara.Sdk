# Spec Delta

## Purpose

What this repository guarantees about the public surface of its published packages: that every public
member is declared, that an undeclared one stops the build instead of landing unnoticed, that the
guard's liveness can be demonstrated rather than assumed, and that every hand-written member recorded
as public was assessed, with the finding on record.

## ADDED Requirements

### Requirement: Every public member of a shipped package SHALL be declared

Every public type and member of a package this repository publishes SHALL appear in that package's
declared public-API record. A build that finds a public member absent from that record SHALL fail.

The record is what a reviewer reads to answer "did this change widen the surface?", so a record that
covers only part of the surface answers that question wrongly while appearing to answer it. Partial
coverage is therefore not a lesser version of this requirement; it is a failure of it.

#### Scenario: The declared record is complete

- **GIVEN** the published packages as they stand
- **WHEN** the build runs with no diagnostic for undeclared public API suppressed
- **THEN** it reports no public member missing from a declared record
- **AND** the build succeeds

#### Scenario: A public member added without declaring it

- **GIVEN** a package whose declared record is complete
- **WHEN** a public member is added to a shipped type and the record is not updated
- **THEN** the build fails
- **AND** the failure names the member that was not declared

#### Scenario: A non-public member is not affected

- **GIVEN** the same package
- **WHEN** an internal or private member is added to a shipped type
- **THEN** the build succeeds with no diagnostic, and the record is unchanged

### Requirement: The suppression of the undeclared-API diagnostic SHALL NOT survive this change

The project-wide suppression that prevents the undeclared-public-API diagnostic from being reported
SHALL be removed, and no configuration SHALL reinstate it for a shipped package. Any remaining
configuration that describes the diagnostic's severity SHALL describe what actually happens.

A suppression paired with a comment claiming the diagnostic is still active is worse than an
unguarded build: it tells a reader the surface is watched when nothing watches it, and this repository
carried exactly that arrangement.

#### Scenario: The diagnostic is not suppressed for shipped code

- **GIVEN** the build configuration after this change
- **WHEN** the undeclared-public-API diagnostic would apply to a shipped package
- **THEN** it is reported
- **AND** no setting silences it — neither a repository-wide one nor one in that package's own project

#### Scenario: A package cannot opt itself out unnoticed

- **GIVEN** every packable project under `src/`
- **WHEN** one of them re-suppresses the diagnostic, lowers it below a build failure, does not
  reference the analyzer, or carries no declared record for the analyzer to check
- **THEN** a check that runs on every pull request the build runs for fails, naming the project and
  the setting
- **AND** for a setting the build evaluates, that check reads the evaluation rather than the text of
  the project file; for one the build does not evaluate — an analyzer-config severity or an
  in-source suppression — it scans the tree

#### Scenario: The configuration describes itself truthfully

- **GIVEN** any comment or setting in the build configuration that refers to this diagnostic
- **WHEN** a reader follows what it says
- **THEN** what they find matches what the build does

### Requirement: The guard's liveness SHALL be demonstrable from the repository

The repository SHALL carry a committed, repeatable procedure that proves the guard fires, and that
procedure SHALL be a negative control: it adds a public member that is not declared and shows the
build failing because of it. It SHALL leave every file it modified with the content it had before
the run, whether it completes or is interrupted, and it SHALL be safe to run on a tree that carries
uncommitted work. It SHALL also run on every pull request the build runs for, so that re-running it
is not left to memory.

A guard is only known to work when something has watched it fail. Re-enabling a diagnostic and
observing a green build demonstrates nothing — a green build is also what a silenced diagnostic
produces, and that ambiguity is what allowed this defect to persist. The restore is held to the same
standard: a clean `git status` after the run is also what a restore that overwrote uncommitted work
produces, so restoration is shown by comparing content, not by the absence of a diff.

#### Scenario: The negative control fails the build

- **GIVEN** the committed procedure
- **WHEN** it is followed on a clean tree
- **THEN** the build fails, and it fails for the undeclared member the procedure introduced
- **AND** the procedure reports success only on that failure — a build that fails for any other
  reason, or that succeeds, is reported as the guard not having been proven

#### Scenario: The tree is left as it was found

- **GIVEN** the same procedure, on a clean tree or on one carrying uncommitted work
- **WHEN** it completes
- **THEN** every file it modified has, byte for byte, the content it had before the run
- **AND** the set of tracked and untracked files version control reports is the same as before the
  run — the same, not necessarily empty
- **AND** build outputs that version control ignores are outside this guarantee, because a build
  necessarily writes them

#### Scenario: An interrupted run leaves no probe behind

- **GIVEN** the same procedure
- **WHEN** it is interrupted at any point after it has modified a file
- **THEN** every file it modified has, byte for byte, the content it had before the run
- **AND** a later run that finds the probe already present refuses to start and names it, rather
  than building on top of it

#### Scenario: The control runs without being remembered

- **GIVEN** a pull request on which the build runs
- **WHEN** the required checks run
- **THEN** the negative control runs among them, after every step that consumes the build output
- **AND** it passes only if the build failed with the undeclared-API diagnostic naming the member it
  introduced

### Requirement: A hand-written member recorded as public SHALL have been assessed, and the finding recorded

Before a previously undeclared hand-written member is recorded, it SHALL be assessed — intended
public API, or not — and that finding SHALL be recorded in the same change, where the next reader
will see it. A member found not to have been intended is still declared, because the build fails
otherwise and removal is out of scope; what this requirement forbids is recording it silently.

Recording an accidental export is what turns an accident into a contract: once declared, it reads to
every later reviewer as a deliberate decision, and withdrawing it is then a breaking change. After
this change every one of them is declared, so the assessment is the only step at which the accident
is still visible as one — the recorded finding is what stops the declaration from reading as a
decision.

**Out of scope:** removing any such member. A removal is a breaking change and belongs to a change
that carries its own release tier.

#### Scenario: An unintended export is surfaced, not buried

- **GIVEN** a hand-written public member that was never declared
- **WHEN** it is assessed and found not to have been intended as public API
- **THEN** that finding is recorded where the next reader will see it
- **AND** the member is still declared, so the build passes and the surface is stated honestly

#### Scenario: Compiler-emitted members need no assessment

- **GIVEN** a public member the compiler or a source generator emits rather than an author writing it
- **WHEN** the record is completed
- **THEN** it is declared without being assessed as a design decision

## Architectural Risk

**Level:** LOW.

**Affected:** the build configuration, and the declared public-API records of the **16** published
packages that have undeclared members — measured 2026-09-25, the 783 symbols fall in 16 of the 29
projects, the largest being `Verbara.Sdk.Ari` (218), `Verbara.Sdk.Sessions` (187) and
`Verbara.Sdk.VoiceAi` (128) — plus `Verbara.Sdk.Ami.SourceGenerators`, which the analyzer never
reached; two steps
added to an existing required CI job and one to the always-run script-test job — no new job and no
new check-run name. No source file under `src/` changes behaviour, no package contents change, and
no consumer is affected — the records are analyzer metadata and are not shipped. `PackageValidation`
compares a pack against the previously published package rather than against these records, so the
current baseline and the empty suppression set stay valid throughout.

**Mitigation:** the defect is already measured rather than suspected — 783 symbols, of which 112 are
hand-written and undeclared, plus the 12 the analyzer reports once it reaches the source-generator
package — so the work is bounded by a number that was counted, not estimated. The risk that matters
is the opposite of the usual one: not that the change breaks something, but that it lands and the
guard still does not fire, on one package or on all of them. That is why the guard's liveness is a
requirement with a negative control that runs on every pull request, and why its breadth is a
requirement with an evaluated per-package check, rather than steps in a task list. The second risk is
that completing the record quietly blesses members that were never meant to be public, which the
assessment requirement addresses by making the finding explicit while deliberately leaving removal
to another change.
