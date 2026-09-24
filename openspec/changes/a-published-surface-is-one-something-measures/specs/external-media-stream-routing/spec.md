# external-media-stream-routing Delta

## ADDED Requirements

### Requirement: The WebSocket audio path's key SHALL be measured before it is designed
The identifier under which a WebSocket audio stream is registered SHALL be established from bytes
captured off a real Asterisk, and a design for routing that path MUST NOT be derived from a reading
of this repository's own code. The capture SHALL record the literal HTTP request line Asterisk sends,
for each Asterisk feature that can reach this server — the ARI `externalMedia` endpoint with
`transport=websocket`, and the `WebSocket()` dialplan application of `chan_websocket` — because the
server does not distinguish between them today and they need not agree.

Where Asterisk publishes an identifier for the connection by another route, such as a channel
variable, the capture SHALL record its value alongside the request path, so the choice of key is made
between measured candidates rather than between remembered ones.

Until that capture exists, the published contract SHALL continue to state that this path is not
addressable by an Asterisk identifier, rather than naming its key a channel id.

#### Scenario: A capture precedes the design
- **GIVEN** a proposal to route the WebSocket audio path by some identifier
- **WHEN** no capture of what Asterisk puts in the upgrade request exists
- **THEN** the design is not accepted, and the probe is the work that runs instead

#### Scenario: Two producers are distinguished
- **GIVEN** a server reachable both by `externalMedia` with `transport=websocket` and by the `WebSocket()` dialplan application
- **WHEN** the request path is captured
- **THEN** each producer's path is recorded separately
- **AND** a difference between them is carried into the contract rather than averaged away

### Requirement: A stream table SHALL NOT be keyed on a value every connection shares
A server that registers streams under a key taken from the connection SHALL take it from something
the connection is free to make unique, and MUST NOT silently drop a registration whose key is already
present. Where the documented way of configuring the far end produces the same key for every call —
a literal path segment in a dialplan line, for instance — that configuration SHALL be corrected in
the same change as the contract, including in this repository's own examples.

A registration that loses to an existing key SHALL be reported, not discarded, because a server whose
active-stream count disagrees with the number of live connections is reporting a number no consumer
can act on.

#### Scenario: Two concurrent calls configured the documented way
- **GIVEN** the dialplan line this repository's example publishes, used for two calls at once
- **WHEN** both connect
- **THEN** both streams are addressable, or the second connection is refused with a reason
- **AND** the stream count equals the number of live connections either way

#### Scenario: The example stops teaching the collision
- **GIVEN** an example whose dialplan line yields one key for every call
- **WHEN** the key's meaning is settled by the capture
- **THEN** the example is corrected in every place it repeats that line, and says what the segment must contain

### Requirement: An aggregate over servers with different keyspaces SHALL say which server answered
An aggregate that forwards one lookup key to servers that key on different things SHALL NOT report
"no such stream" and "that key belongs to another server's keyspace" as the same answer. It SHALL
either resolve the ambiguity — by routing the key to the server whose keyspace it belongs to — or
expose the per-server result, so a caller can tell a miss from a category error.

#### Scenario: A key from the wrong keyspace
- **GIVEN** an aggregate over an AudioSocket server and a WebSocket server
- **WHEN** a caller passes an identifier only one of them could ever hold
- **THEN** the caller can distinguish "that stream is not connected" from "that identifier means nothing to the server that would hold it"

## Architectural Risk

**Level:** MEDIUM

**Affected:** `Verbara.Sdk.Ari` (`Audio/WebSocketAudioServer.cs`, `Audio/CompositeAudioServer.cs`),
the `IAudioServer` / `IAudioStream` contract text in `Verbara.Sdk`, `Examples/WebSocketMediaExample`,
and any downstream consumer that reaches a stream through `CompositeAudioServer` — which includes the
`Verbara.Sdk.Pro` AgentAssist path.

MEDIUM rather than LOW because the outcome is not known before the probe runs. If the measured key
turns out to be something the server must be told rather than something it can read, the fix reaches a
shipped signature and costs what its sibling change cost: `CP0002`, `PublicAPI` rows, a committed
suppressions entry and a migration note. If it turns out the path simply is not addressable, the fix
is contract text and an example, and costs nothing. Pricing that fork before the capture exists is
guessing, and this capability forbids it.

**Mitigation:** the probe is the first task and its capture is committed beside the change, in the
shape the sibling change's `probe-capture.txt` takes, with the full request recorded rather than
described. The two Asterisk versions the lane builds are both probed, because the merge queue is the
only place both run and it is the wrong place to learn of a difference. The change declares in writing
that it splits if the probe does not land, so the two findings travelling with it are not held behind
an unknown.
