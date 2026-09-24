# Spec Delta

## Purpose

What the SDK guarantees about live call state when it reloads from Asterisk after a reconnect: that
the reload reconciles rather than replaces, that a call the reload proves is gone ends where every
consumer is already listening, that correlation survives the reload, and that a reload it cannot
trust ends nothing.

## ADDED Requirements

### Requirement: A reload reconciles held state; it SHALL NOT discard it

A reload of live state after a reconnect SHALL compare the snapshot Asterisk returns against the
state already held, and SHALL NOT drop held state before that comparison. Clearing the tracked
channels silently is prohibited: it removes the evidence the comparison needs and leaves every
observer of channel removal unaware that anything changed.

#### Scenario: The reload is compared, not applied over a cleared table

- **GIVEN** a tracked call whose two channels are held
- **WHEN** the connection reconnects and a reload returns both channels
- **THEN** the held channels are reconciled against the snapshot
- **AND** no observer of channel removal is notified for a channel the snapshot still contains

#### Scenario: A silent wipe is observable as a defect

- **GIVEN** any component subscribed to channel removal
- **WHEN** a reconnect occurs
- **THEN** that component is never asked to handle the disappearance of a channel that is still live

### Requirement: A call the reload proves is gone MUST end through the completion path consumers already observe

A call whose channels are absent from a reload that completed SHALL be ended through the same path a
hangup takes, so that the ending reaches a consumer as `CallEndedEvent`. An ending that only changes
internal state is not sufficient: it is invisible to every consumer written against this SDK, and the
reload is the last notification such a call will ever produce.

**BREAKING**: a consumer that today observes such a session remain in a connected state for the life
of the process will now observe it end.

#### Scenario: The call ended during the outage

- **GIVEN** a connected call with two participants
- **WHEN** the connection reconnects and the reload returns no channels
- **THEN** that call ends
- **AND** exactly one `CallEndedEvent` is raised for it
- **AND** it is no longer reported among the active calls

#### Scenario: The ending is not merely a state change

- **GIVEN** the same call
- **WHEN** it is ended by the reload
- **THEN** the ending is delivered through the same event a hangup would have produced
- **AND** a consumer that subscribes only to call endings observes it

### Requirement: Correlation SHALL survive the reload

A reload SHALL carry the correlation identifier Asterisk provides for each channel, and a channel
already held SHALL NOT be re-admitted as a newly appeared channel. One call before a reconnect
remains one call after it.

**BREAKING**: one surviving call stops producing additional call records across a reconnect, so a
consumer counting calls across a reconnect observes a different, lower, correct number.

#### Scenario: Both legs of one call survive the outage

- **GIVEN** a connected call whose two channels share one correlation identifier
- **WHEN** the connection reconnects and the reload returns both channels with that identifier
- **THEN** exactly one call is reported as active
- **AND** it is the same call, under the identity it had before the reconnect

#### Scenario: A reload without correlation does not invent calls

- **GIVEN** a connected call
- **WHEN** the reload returns a channel for which Asterisk supplies no correlation identifier
- **THEN** that channel does not create an additional call for a call already held

### Requirement: An ending produced by a reload MUST be distinguishable from an observed hangup

A call ended because a reload proved it gone SHALL carry a marker saying the ending came from a
reload, and SHALL NOT be attributed a hangup cause. No hangup was observed, so no cause is known: a
consumer MUST be able to tell "the reason is unknown" from "the call ended abnormally", because
downstream classifiers treat an unrecognised cause as an abnormal ending and act on it.

#### Scenario: The ending carries its provenance

- **GIVEN** a connected call that a completed reload proves is gone
- **WHEN** the call is ended
- **THEN** the ending is marked as having come from a reload
- **AND** the departing participants carry no hangup cause

#### Scenario: An observed hangup is unchanged

- **GIVEN** a call whose channels were hung up while the connection was live
- **WHEN** it ends
- **THEN** it carries the hangup cause Asterisk reported, and no reload marker

#### Scenario: A consumer can separate the two

- **GIVEN** one call ended by an observed hangup with an abnormal cause, and one ended by a reload
- **WHEN** a consumer classifies both
- **THEN** it can distinguish them without inferring anything from the absence of a value

### Requirement: A reload that cannot be trusted SHALL end nothing

A reload that fails, is interrupted, or cannot be shown to have completed SHALL leave every held call
untouched. Absence from an unfinished snapshot is not evidence that a call is gone. The failure
direction is deliberate: ending a live call in error is worse than the defect this capability exists
to remove.

#### Scenario: The reload fails partway

- **GIVEN** two connected calls
- **WHEN** the connection reconnects and the reload fails before it completes
- **THEN** neither call is ended
- **AND** both remain active with their participants intact

#### Scenario: The reload is never answered

- **GIVEN** a connected call
- **WHEN** the connection reconnects and Asterisk never answers the state request
- **THEN** the call is not ended

## Architectural Risk

**Level:** MEDIUM.

**Affected:** `Verbara.Sdk.Live` and, through it, every consumer that holds call state — including
Sdk.Pro's cluster layer and Platform, which inherit this behaviour unchanged from the root of the
dependency chain. No public API is added or removed; what changes is which events a consumer receives
after a reconnect, and how many calls it then holds.

**Mitigation:** the two behaviours this change corrects are already measured rather than assumed, and
those measurements land as failing tests before any production code moves, so the fix is bound by a
test that fails without it. The risk that matters is the opposite direction — ending a call that is
still live — which the last requirement addresses as a contract rather than as implementation
carefulness: a reload that cannot be shown to have completed ends nothing. Because the ending travels
the existing completion path, no consumer needs a code change to receive it, and no consumer receives
a new kind of event it has never handled. The rejected alternative is recorded in the proposal: a
clock-based sweep over calls in an early state would, after this reload defect is understood, mark
healthy calls dead after every reconnect.
