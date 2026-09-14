# client-connection-state — Delta

## ADDED Requirements

### Requirement: A connect attempt that ends without a connection leaves a terminal state
An ARI client SHALL leave its published connection state at a terminal value when a connect attempt
ends without a connection, and MUST NOT leave it reading as an attempt still in progress on a client
that will not dial again. Which terminal value it leaves is decided by **who ended the attempt**, read
from the caller's own cancellation token at the moment the attempt ends: a caller who withdrew the
attempt leaves the state a requested ending leaves, and every other ending leaves the faulted state
the client already uses when its reconnect loop stops for good. The decision MUST NOT be taken from
the exception's type, its message, or the token the exception carries, because a cancellation raised
inside the transport carries a token the caller never held. The attempt MUST still surface to its
caller exactly what it surfaces today — the same exception, unswallowed — because the state is a
second channel for observers who cannot see the throw, not a replacement for it. The terminal value
is a statement and not a gate: a later connect attempt on the same client MUST proceed exactly as it
does from any other state.

#### Scenario: The far end refuses the upgrade
- **GIVEN** a client whose first connect attempt is answered with a refused upgrade, such as `401 Unauthorized` or `503 Service Unavailable`
- **WHEN** the attempt ends
- **THEN** the exception reaches the caller unchanged, and the client's state reads as faulted rather than as an attempt in progress
- **AND** the health check reports Unhealthy, as it did before, with a message naming the terminal state

#### Scenario: The dial is refused before any upgrade
- **GIVEN** a client whose first connect attempt reaches no listener at all
- **WHEN** the attempt ends
- **THEN** the exception reaches the caller unchanged and the client's state reads as faulted

#### Scenario: The caller withdraws the attempt
- **GIVEN** a client dialling under a token its caller controls, with the far end holding the connection open and never answering the upgrade
- **WHEN** the caller cancels that token
- **THEN** an `OperationCanceledException` reaches the caller and the client's state reads as a requested ending, not as a fault, because a cancellation the caller asked for is never a failure
- **AND** nothing is logged at Error for it

#### Scenario: A successful connect is unchanged
- **GIVEN** a client whose connect attempt is answered with a completed upgrade
- **WHEN** the attempt ends
- **THEN** the state reads connected and the events loop is running, exactly as before — writing a terminal state on the success path is a defect the same tests must catch

#### Scenario: A failed first attempt dials nothing on its own
- **GIVEN** a client with auto-reconnect enabled whose first connect attempt failed
- **WHEN** several reconnect intervals pass
- **THEN** no further connection is attempted and no events loop exists, unchanged from before — the terminal state describes that inertness rather than causing it

#### Scenario: A later attempt on the same client still connects
- **GIVEN** a client whose state is terminal after a failed connect attempt
- **WHEN** the caller calls connect again and the far end answers
- **THEN** the client connects and its state reads connected, so the terminal value never became a gate that refuses to dial

## Architectural Risk

- **Level:** LOW.
- **Affected:** the published connection state of `AriClient` (`Verbara.Sdk.Ari`) and every consumer
  that reads it, including `AriHealthCheck` and anything downstream in Pro and Platform that branches
  on `AriConnectionState`. No public API change, so nothing downstream needs to recompile; the health
  check's status is the same before and after, and only the state it names moves.
- **Mitigation:** the connect path catches nothing — the outcome is written from a `finally` — so what
  the caller receives cannot change. Regression tests pin both endings against a loopback server that
  refuses deterministically rather than by timing, and controls pin the success path, the absence of a
  reconnect after a failed first attempt, and a later attempt still connecting. Removing the `finally`,
  writing one value for both endings either way round, writing the state before the dial, writing it
  on the success path, and adding a gate on the terminal value each fail at least one of them.
- **Residual:** whether the classification reads the caller's token rather than the exception cannot
  be separated by a committed test with the transport overload in use, so it is recorded as a design
  decision following ADR-0053's recorded trap rather than claimed as covered; a caller cancelling in
  the same instant a refusal arrives is booked as a withdrawal; and overlapping connect and disconnect
  calls on one client remain outside this requirement, as they are today.
