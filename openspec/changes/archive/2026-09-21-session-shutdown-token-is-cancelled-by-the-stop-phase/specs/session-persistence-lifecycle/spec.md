# session-persistence-lifecycle — Delta

## ADDED Requirements

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

## Architectural Risk

- **Level:** LOW.
- **Affected:** the persistence path of `CallSessionManager` (`Verbara.Sdk.Sessions`) in every host
  wired by the single-server session registrations in `Verbara.Sdk.Hosting`. Both symbols the change
  touches are `internal` and in no `PublicAPI.Shipped.txt`, so no downstream package recompiles and
  no telemetry or event changes meaning.
- **Mitigation:** the cancellation source has one owner, the object that hands it out, and exactly
  two cancellation sites. The requirement's scenarios pin both directions — an aborted start must not
  cut a save short, and a withdrawn grace must — and the in-budget scenario is what keeps a fix from
  over-correcting into cutting every save short the moment a stop begins, which would lose session
  state that a graceful shutdown exists to save. The multi-server registration path, which wires no
  hosted service and therefore has no lifetime token at all, is recorded as a known gap rather than
  covered here.
