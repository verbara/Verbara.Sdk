# client-connection-state Specification

## Purpose

What an ARI client's published connection state says, and when. The state is a second channel for
observers who cannot see the throw — a supervisor, a retry policy, a health check — and its value is
only useful if it distinguishes an attempt still in progress from one that is over. An attempt that
ends without a connection is over: nothing dials again on its own, because the events loop starts only
after a successful connect and the reconnect loop is reachable only from it. So such an attempt leaves
a **terminal** value, never the one that means "still dialling".

Which terminal value is decided by **who ended the attempt**, read from the caller's own cancellation
token and never from the exception — a cancellation raised inside the transport carries a token the
caller never held, so neither the exception's type nor the token it carries can say whether the caller
withdrew (ADR-0053). A withdrawal the caller asked for is not a failure and rests where a requested
disconnect leaves the client; every other ending is a fault.

The terminal value is a **statement, not a gate**: nothing reads it to decide anything, and a later
attempt on the same client proceeds exactly as it would from a client that had never dialled. The
attempt still surfaces to its caller exactly what it surfaced before — the same exception, unswallowed
— because the state supplements the throw rather than replacing it (ADR-0056).

## Requirements

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
