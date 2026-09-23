# server-accept-lifecycle — Delta

## ADDED Requirements

### Requirement: An accept failure the server can survive does not end its accept loop in silence
A server that accepts connections in a loop SHALL distinguish an accept that ended because the server
is stopping from one that failed while the server was still meant to be running, and MUST NOT let the
second end the loop without saying so. A stopping server ends its loop, which is the ending its stop
path asks for. A running server that fails to accept — because the process is out of file descriptors,
because the kernel is out of buffers, or because a connection was aborted in the backlog — SHALL report
the failure at Error and SHALL go on accepting, after a wait that grows with each consecutive failure
up to a cap so that a persistent failure can neither spin the loop nor flood the log.

The discriminator SHALL be the server's own published running state, never the loop's cancellation
token: a stop clears that state before it aborts the pending accept, so the state cannot be late, while
the token is cancelled afterwards and can be. The wait SHALL be driven by an injected time source so it
is observable in a test without a real delay.

The loop MUST NOT write a terminal state for a failure it survives. A server whose loop is still bound
and still accepting is not faulted, and marking it so would refuse a later start over a loop that is
still working.

#### Scenario: An accept fails while the server is running
- **GIVEN** a running server whose next accept fails for a reason that is not its stop
- **WHEN** the failure reaches the accept loop
- **THEN** it is reported once at Error, the loop waits, and the server accepts the next connection

#### Scenario: Consecutive failures back off, and a success starts the wait over
- **GIVEN** a running server whose accepts keep failing
- **WHEN** each failure follows the last
- **THEN** each wait is longer than the one before it, up to a cap it never exceeds
- **AND** an accept that succeeds between two failures returns the next wait to its initial value

#### Scenario: A stopping server ends its loop rather than backing off
- **GIVEN** a running server with a connection accepted and its loop parked on the next accept
- **WHEN** the server is stopped
- **THEN** the loop ends, the stop runs to completion, and nothing is reported as an accept failure —
  because an accept aborted by the stop is the stop

#### Scenario: A survived failure is not a terminal state
- **GIVEN** a server that has reported an accept failure and gone on accepting
- **WHEN** its published state is read
- **THEN** it still reads as listening, because the listener is bound and the loop is running — the
  failure is reported through the log, and a health check built on that state is unchanged

## Architectural Risk

- **Level:** LOW.
- **Affected:** the accept path of `FastAgiServer` (`Verbara.Sdk.Agi`) and any consumer reading
  `AgiHealthCheck`. The type is public and shipped, so external SDK consumers are in scope; neither
  Sdk.Pro nor Platform starts this server. No public API change — every addition is `internal`.
- **Mitigation:** the stop path keeps its own clauses and is pinned by a case asserting a clean stop
  reports no accept failure, which is what catches a filter transplanted without checking that the stop
  clears the running state first. The backoff is driven by a fake clock rather than a real wait.
- **Residual:** under a persistent failure the health check still reports healthy, because the loop is
  alive and retrying. This requirement removes the silent death, not the optimistic health check;
  whether a retrying server should read as degraded is a separate decision about what the state means.
