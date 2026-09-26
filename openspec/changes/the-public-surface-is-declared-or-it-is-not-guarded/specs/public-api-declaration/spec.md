# Spec Delta

## Purpose

What this repository guarantees about the public surface of its published packages: that every public
member is declared, that an undeclared one stops the build instead of landing unnoticed, that the
guard's liveness can be demonstrated rather than assumed and its blind spot stated where it reports,
and that every hand-written member recorded as public was assessed, with the finding on record.

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

- **GIVEN** every project whose package the pull request's build produced
- **WHEN** one of them re-suppresses the diagnostic, lowers it below a build failure, stops the
  analyzer running, does not reference the analyzer, tells the analyzer to skip part of its
  surface, hands the analyzer a declared record other than the package's own two files, or has
  the diagnostic suppressed where it is raised — by a directive or an attribute in a source file,
  hand-written or emitted by a source generator, or by an analyzer that suppresses it
  programmatically — by any setting, wherever it is set, including a step that runs after the
  project is evaluated and before it is compiled
- **THEN** a check that runs on every pull request the build runs for fails, naming the project
  and what it found
- **AND** that check reads the arguments the compiler was handed by the compilation that produced
  the package — not the project's evaluation, not a second compile, and not the text of the
  project file — so that everything decided before the compiler ran is inside what it sees,
  including which files it was given as the declared record
- **AND** a suppression or demotion of the diagnostic at the point it was raised is found in the
  compiler's own diagnostic log of that same compilation, which records every instance the
  analyzer raised, suppressed ones included, with what suppressed it — not by reading source
  text, which does not include what a generator emitted and cannot see a suppressor at all
- **AND** a key in an analyzer-config file, which neither the arguments nor the log can show
  because the analyzer then raises nothing, is found by reading every analyzer-config file those
  arguments name — wherever it lives, the repository's root configuration file included, with no
  exempt file and no exempt directory — under one rule: no option addressed to the public-API
  analyzer, no bulk severity setting, and the diagnostic's identifier present only as its
  severity key set to a level that fails the build, every occurrence, in every section, however
  the key is cased
- **AND** a package with no such record, or no such log, is reported as not proven, never as a
  pass

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

### Requirement: The per-package check SHALL judge only what the compile left behind, and SHALL say so

The per-package check SHALL judge each compile from what that compile left behind — the arguments
the compiler was handed, the compiler's own diagnostic log, and the analyzer-config and
declared-record files those arguments name — read as they exist when the check runs, and its
output SHALL state that this is what it read. The two declared-record files of every package it
judges SHALL match the checkout after the build. What the check cannot see, and SHALL NOT claim
to: a step of this repository's own build that changes one of those files after the compiler
read it, or changes what the compiler read and restores it before the check runs; and an option
the analyzer reads under a name the rule does not know.

A guard that reads files after the fact is trusting the build not to have rewritten them, and
there is no observation a build step cannot forge — the build system runs any target after any
other. The boundary is stated instead: such a step is a change to a project file, to
`Directory.Build.targets` or to a package reference, and that is what review reads. Stating the
boundary is the difference between a limit and a blind spot.

#### Scenario: A record file the build left changed is caught

- **GIVEN** a build step that appends the undeclared symbols to a package's declared record before
  the compiler reads it and leaves the file changed
- **WHEN** the check runs after the build
- **THEN** it fails, naming the file, because a declared record that differs from the checkout is
  not the record the pull request shows

#### Scenario: A rewrite the build restores is named as unseen, not denied

- **GIVEN** a build step that changes a file the compiler read — a declared record, an
  analyzer-config file, the argument record or the log — and restores it before the check runs
- **WHEN** the pull request's build and the check run
- **THEN** the build succeeds and the check reports the package as proven
- **AND** the check's output states that it judged those files as they existed when it ran, and
  that a build step which changed and restored them is outside what it can see
- **AND** the step itself is a change to a project file, `Directory.Build.targets` or a package
  reference — never to a source file or a record file alone — which is what review reads

#### Scenario: An option under a name the rule does not know is outside it

- **GIVEN** a later analyzer version that reads a silencing option under a prefix other than
  `dotnet_public_api_analyzer`
- **WHEN** a package sets it in an analyzer-config file
- **THEN** the check passes, because the rule bans by prefix and the prefix is not yet named
- **AND** the version bump that brings it arrives as a pull request whose harness cases still
  measure the known silencers, so the rule is extended there, not discovered later

## Architectural Risk

**Level:** LOW.

**Affected:** the build configuration, and the declared public-API records of the **16** published
packages that have undeclared members — measured 2026-09-25, the 783 symbols fall in 16 of the 29
projects, the largest being `Verbara.Sdk.Ari` (218), `Verbara.Sdk.Sessions` (187) and
`Verbara.Sdk.VoiceAi` (128) — plus `Verbara.Sdk.Ami.SourceGenerators`, which the analyzer never
reached. The build configuration gains a `Directory.Build.targets` that records each compile's own
arguments and keeps its diagnostic log, and two global properties on the Release build; two steps
are added to an existing required CI job and **two** to the always-run script-test job — no new job
and no new check-run name. No source file under `src/` changes behaviour, no package contents change, and
no consumer is affected — the records are analyzer metadata and are not shipped. `PackageValidation`
compares a pack against the previously published package rather than against these records, so the
current baseline and the empty suppression set stay valid throughout.

**Mitigation:** the defect is already measured rather than suspected — 783 symbols, of which 112 are
hand-written and undeclared, plus the 12 the analyzer reports once it reaches the source-generator
package — so the work is bounded by a number that was counted, not estimated. The risk that matters
is the opposite of the usual one: not that the change breaks something, but that it lands and the
guard still does not fire, on one package or on all of them. That is why the guard's liveness is a
requirement with a negative control that runs on every pull request, and why its breadth is a
requirement with a per-package check that reads the arguments, the diagnostic log and the
analyzer-config files of the compile that produced each package — and says what it cannot see —
rather than steps in a task list. The second risk is that completing the record quietly
blesses members that were never meant to be public, which the assessment requirement addresses by
making the finding explicit while deliberately leaving removal to another change.
