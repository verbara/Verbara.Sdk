# session-persistence-lifecycle Specification

## Purpose

Which phase owns the token session persistence runs under, and how a save that ends because of that
token is told apart from one that failed. `CallSessionManager` persists every session change
fire-and-forget, so no caller awaits those saves and no caller sees their exceptions — the only thing
that decides whether a shutdown cuts them short, lets them finish, or abandons them is the lifetime
of the token they were handed, and the only place their ending is visible is the error log.

Those two questions are one capability because the same mistake answers both wrongly. A hosted
service that stores the token it received in `StartAsync` is handing on a token whose documented
meaning is *"the start was aborted"* — and worse, one the host releases when the start returns, so
after a completed start nothing can cancel it. That single error makes a stop unable to cut a save
short *and* makes the filter written to keep a cut-short save out of the error log unreachable in the
phase it was written for. The fix is one rule: a token a component keeps past a phase comes from a
source that outlives that phase, and is cancelled by the phase it names (ADR-0059).

The consequence the requirements below protect is an operator's error log at shutdown. A save the
shutdown deliberately cut short is not a failure and must be silent; every other failed save — a
store-side deadline, a broken connection, a bad row — must still be logged, and the two are told
apart by whether the *persistence token* is cancelled, never by the exception's concrete type. Get
that backwards in either direction and shutdown either fills the log with noise nobody can act on or
swallows the one line that mattered.

This is ADR-0054's one-owner rule read across a **lifetime** boundary rather than between two
concurrent loops, and it is the sibling of `streaming-session-lifecycle`, which classifies a
*session's* ending by who caused it. Here the thing being classified is a *save*, and the phase that
can cancel it is the answer.

## Requirements

### Requirement: The token session persistence runs under is cancelled by the stop phase, never by the start phase
A hosted service that hands a longer-lived component a cancellation token SHALL hand a token from a
source it owns itself, and MUST NOT hand on the token it received in `StartAsync`, whose documented
meaning is that the start was aborted. The token `CallSessionManager` saves under SHALL be cancelled
by exactly two things: the host withdrawing its graceful shutdown — the token passed to `StopAsync` —
and the hosted service being disposed. Aborting a start SHALL NOT cancel it. The source SHALL be
cancelled before it is released, so no component is left holding a token that can never be cancelled.

#### Scenario: An aborted start does not cut a save short
- **GIVEN** a started session hosted service with a save in flight
- **WHEN** the token the host passed to `StartAsync` is cancelled, as it is when a stop is requested before the start completed or the startup budget elapses
- **THEN** the token the store was handed for that save is not cancelled, the save is still in flight, and nothing is logged at Error

#### Scenario: A shutdown that is no longer graceful cuts an in-flight save short
- **GIVEN** a started session hosted service with a save in flight
- **WHEN** the host calls `StopAsync` and the token it passed there is cancelled, because its shutdown budget elapsed or whoever asked for the stop cancelled it
- **THEN** the save ends with an `OperationCanceledException` rather than continuing past the host's shutdown

#### Scenario: A stop inside its budget leaves an in-flight save running
- **GIVEN** a started session hosted service with a save in flight
- **WHEN** the host calls `StopAsync` with a token that is not cancelled
- **THEN** the save is not cut short and is free to finish, because a graceful stop is exactly the case in which the session state is still worth persisting

#### Scenario: No new save starts from a server event after the stop
- **GIVEN** a session hosted service that is stopping
- **WHEN** the server raises a channel event after `StopAsync` has run
- **THEN** no new save is started, because the service unsubscribes from the server before it wires the host's stop token to its source

#### Scenario: Teardown without a graceful stop still ends the saves
- **GIVEN** a session hosted service with a save in flight that the host tears down without a completed graceful stop
- **WHEN** the hosted service is disposed
- **THEN** the token it handed the session manager is cancelled before its source is released, and the in-flight save ends with an `OperationCanceledException`

### Requirement: A save cut short by shutdown is not a persistence failure, and every other failed save still is
A save that ends with an `OperationCanceledException` while the persistence token is cancelled SHALL
NOT be reported as a persistence failure: it was cut short on purpose. Every other failed save MUST
still be logged at Error as `Failed to persist session {SessionId}`, including a save that a store
cancels on its own — a store-side deadline, say — while the persistence token is still live. The
ending is classified by whether the persistence token is cancelled, never by the exception's concrete
type, message, inner exception or token.

#### Scenario: A save the shutdown cut short is silent
- **GIVEN** a save in flight under a persistence token that is then cancelled by the stop phase
- **WHEN** the save ends with an `OperationCanceledException`, whether as a `TaskCanceledException` or as a plain one
- **THEN** nothing is logged at Error for that session

#### Scenario: A store that gives up on its own is still a failure
- **GIVEN** a save in flight while the persistence token is not cancelled
- **WHEN** the store ends the save with an `OperationCanceledException` of its own
- **THEN** the save is logged at Error as `Failed to persist session {SessionId}` carrying that exception

#### Scenario: Any other failed save is still a failure
- **GIVEN** a save in flight
- **WHEN** it ends with an exception that is not an `OperationCanceledException`
- **THEN** the save is logged at Error as `Failed to persist session {SessionId}`, whatever the state of the persistence token
