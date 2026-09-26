# Spec Delta

## Purpose

What this repository guarantees about a defect or follow-up it discovered but did not fix: that it is
recorded where something can find it, that it names an owner which is an open change or a decision
rather than a label, that a change cannot close while carrying an unowned one, and that the mechanism
saying all of this is itself demonstrable.

## ADDED Requirements

### Requirement: A recorded finding SHALL name an owner, and the owner SHALL be a place work happens

Every finding this repository records but does not fix SHALL name an owner, and that owner SHALL be an
open change or a decision record — something that can be opened and worked. A prose label SHALL NOT
count as an owner, however clearly it is written.

*Out of scope*, *noted for the maintainer*, *tracked separately*, *needs its own change* and *not yet
owned* all read as diligence and none of them can be acted on: nothing can be opened, nothing appears
in a backlog, and the next reader has no way to tell a finding that was handled from one that was
merely described. The distinction this requirement draws is between a sentence and an address.

#### Scenario: A finding recorded with a label and no owner

- **GIVEN** a change that records a finding it does not fix
- **WHEN** the finding names only a label such as out of scope or tracked separately
- **THEN** a check that runs on every pull request the build runs for fails
- **AND** it names the finding and the change that recorded it

#### Scenario: A finding recorded with an owner

- **GIVEN** the same change
- **WHEN** the finding names an open change or a decision record as its owner
- **AND** the owner names the finding by its registry identifier — a task line in the change's
  `tasks.md`, or the decision record's body or a dated addendum
- **THEN** the check passes
- **AND** the finding is reachable from the owner by that identifier, and from the registry by the
  owner's name

#### Scenario: An owner that exists but does not carry the finding

- **GIVEN** a registry row naming an owner that resolves
- **WHEN** the owner's `tasks.md` or decision record contains no occurrence of the row's identifier
- **THEN** the check fails, naming the row and the owner, and says the owner does not carry the
  finding

#### Scenario: An owner that does not exist

- **GIVEN** a finding naming an owner
- **WHEN** the named change or decision record cannot be found in the repository
- **THEN** the check fails and says which name could not be resolved

### Requirement: The answer to "does this finding have an owner?" SHALL be one file

The set of recorded findings and their owners SHALL live in a single tracked registry, so the question
is answered by reading one file rather than by searching the changes, the archive and the pull-request
history. A finding that is recorded anywhere SHALL have a row in that registry.

This mirrors what this repository already does for published figures, and for the same reason: a
property that can only be established by a search is a property nobody establishes. The registry is
the artifact a reviewer can be pointed at.

#### Scenario: A finding recorded without a registry row

- **GIVEN** a change whose task list records a finding
- **WHEN** the registry carries no row for it
- **THEN** the check fails, naming the finding and the change

#### Scenario: The registry is the reviewable surface

- **GIVEN** a reviewer asking whether every known finding has somewhere to be worked
- **WHEN** they read the registry
- **THEN** every recorded finding appears with its owner, without reading any other file

### Requirement: A recorded finding SHALL be discoverable by a machine, not only by a reader

A finding a change records but does not fix SHALL appear under a designated, consistent heading, so
that finding it does not depend on the words the author happened to choose.

Recording findings in free prose is what produced the present state: of 28 closed changes, six carry
any recognisable phrase at all, while the follow-ups themselves number in the tens. The heading is not
bureaucracy — it is the difference between a property that can be checked and one that can only be
hoped for.

#### Scenario: A finding under the designated heading

- **GIVEN** a change that records a finding it does not fix
- **WHEN** it places the finding under the designated heading
- **THEN** the check reads it without depending on the phrasing of the finding itself

#### Scenario: A finding recorded outside the heading

- **GIVEN** the same change
- **WHEN** a finding is recorded in prose elsewhere in the task list carrying a phrase the archive
  guidance names as non-exempting — an ownership label, or *out of scope* as a bold or
  sentence-initial label — and the phrase is used, not quoted
- **THEN** the check fails rather than passing silently
- **AND** the failure says the finding belongs under the heading

#### Scenario: A finding recorded outside the heading in words the check does not know

- **GIVEN** the same change
- **WHEN** a finding is recorded elsewhere in the task list with none of the named phrases
- **THEN** the check does not detect it
- **AND** the registry's header and the archive guidance state this limit, so the heading — not the
  check — is what an author is asked to satisfy

### Requirement: A change SHALL NOT close while it carries a finding with no owner

A change SHALL NOT be archived while any finding it records lacks a registry row naming an owner. The
check enforcing this SHALL be reachable on the pull request that closes the change, not only after it.

Closing is the moment the finding becomes invisible: the task list moves into the dated record, and
from then on nothing surfaces it. A check that runs afterwards reports a loss rather than preventing
one.

#### Scenario: A close-out carrying an unowned finding

- **GIVEN** a pull request that archives a change recording a finding with no owner
- **WHEN** the required checks run
- **THEN** one of them fails, and it fails on that pull request rather than on a later one

#### Scenario: A close-out whose findings are all owned

- **GIVEN** a pull request that archives a change whose recorded findings all have rows with owners
- **WHEN** the required checks run
- **THEN** they pass, and the registry rows remain after the change's own files have moved into the
  dated record

#### Scenario: The closing pull request has already moved the change into the dated record

- **GIVEN** a pull request whose diff renames a change from `openspec/changes/<name>/` to
  `openspec/changes/archive/<date>-<name>/`, so that on the ref the checks run against the task list
  exists only under the dated record
- **WHEN** the required checks run
- **THEN** the check reads the task list at its archived path and applies the rules it applies to an
  open change
- **AND** an unowned finding there fails the check on that pull request

### Requirement: Records that are already closed SHALL NOT be rewritten to satisfy this capability

A change that was closed before this capability existed SHALL NOT be edited to add ownership
information and SHALL NOT be subject to the check; findings already recorded in such a change SHALL
be carried by registry rows instead. The set of such changes SHALL be a committed list of archived
directory names, frozen at the merge that lands this capability and never appended to. Every other
task list under `openspec/changes/`, the dated record included, SHALL be read by the check exactly as
an open change is read. Reading a closed record is not rewriting it; the archive path alone SHALL NOT
exempt a record — only the list does.

This repository treats its dated records as period-correct and leaves them verbatim; that principle is
already stated for published figures and applies here unchanged. Rewriting closed records to satisfy a
guard introduced afterwards would make the record describe a process that was not followed at the
time. The list is what lets the check land over records that could not have followed it.

#### Scenario: A finding already recorded in a closed change

- **GIVEN** a finding recorded in a change that is already archived
- **WHEN** it is given an owner
- **THEN** the owner is recorded in the registry
- **AND** the archived change's own files are unchanged

#### Scenario: A closed record from before this capability

- **GIVEN** an archived change whose directory name is in the committed pre-guard list
- **WHEN** its task list carries finding-shaped prose that would fail an open change
- **THEN** the check reports nothing for it

#### Scenario: A closed record from after this capability

- **GIVEN** an archived change whose directory name is not in the committed pre-guard list
- **WHEN** the check runs on any later pull request
- **THEN** every finding under its designated heading still has a registry row naming an owner that
  resolves
- **AND** a row removed after the close-out fails the check on the pull request that removes it

#### Scenario: The guard does not claim the closed records are complete

- **GIVEN** the set of already-closed changes
- **WHEN** the registry and its guard are in place
- **THEN** neither asserts that every finding those changes recorded has been found
- **AND** what is known to be unrecovered is stated in the registry rather than left implied

### Requirement: The guard's liveness SHALL be demonstrable from the repository

The repository SHALL carry a committed, repeatable procedure that proves the guard fails on an unowned
finding, and it SHALL restore every file it modified on every exit path, including interruption.

A guard over a registry that is already correct passes whether it works or not, and a passing check is
the same observation either way. The only evidence that distinguishes them is a failure that was
caused on purpose.

#### Scenario: The negative control fails the guard

- **GIVEN** the committed procedure
- **WHEN** it is followed
- **THEN** the guard fails, and it fails because of the unowned finding the procedure introduced
- **AND** any other failure is reported as the guard not having been proven

#### Scenario: The tree is left as it was found

- **GIVEN** the same procedure
- **WHEN** it completes or is interrupted
- **THEN** every file it modified has, byte for byte, the content it had before the run
- **AND** the restoration is shown by comparing content, not by the absence of a difference report

## Architectural Risk

**Level:** LOW.

**Affected:** one new tracked document; one shell check, its harness and its negative control under
`scripts/`; two existing always-run required CI jobs (`OpenSpec Validate`, `Coverage Script Tests`),
each gaining one step; and the archive guidance in the OpenSpec configuration. No test project
changes, no source file under `src/` changes, no public API moves, no package contents change, and
no consumer is affected in any way. What changes is the close-out routine: a change that records a
finding cannot archive without a registry row naming an owner.

**Mitigation:** the defect this capability addresses is measured rather than suspected — 83 follow-ups
verified open against the tree, two of them re-verified by hand at the same line numbers a month
later, against six recognisable phrases across 28 closed changes. The risk that matters is not that
the guard breaks something but that it lands and changes nothing: either because it passes over a
registry that is empty, or because it can be satisfied by a row that names an owner nobody opens.
Both are addressed as requirements rather than as care — the registry ships seeded, an owner must
resolve to something that exists, and the guard's liveness carries a negative control. The residual
risk is stated rather than solved: this capability cannot establish that the closed records have been
fully harvested, and it is required to say so instead of implying otherwise.
