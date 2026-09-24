# external-media-stream-routing Delta

## ADDED Requirements

### Requirement: An AudioSocket external media channel SHALL be findable by the channel id it returned
Where the transport identifies its connection with a caller-supplied value — AudioSocket does, in its
identification frame — the SDK SHALL make the ARI channel id and that value the same value, by
supplying one identifier to both parameters that carry them.

The SDK SHALL NOT leave the ARI channel id to be minted by Asterisk while keying the stream table on
a different value, because no lookup can succeed across those two namespaces. The identifier SHALL be
in the canonical lowercase hyphenated form, which is the form the server's table is keyed by; an
identifier in any other spelling produces a successful create and a lookup that misses.

The WebSocket transport is **not** covered by this capability until its key has been measured rather
than read from the code.

#### Scenario: A stream is found by the channel id the create call returned
- **GIVEN** an AudioSocket server owned by the caller and running
- **WHEN** the caller creates an external media channel with AudioSocket encapsulation pointing at it
- **AND** Asterisk connects and sends its identification frame
- **THEN** looking the stream up by the returned `Channel.Id` SHALL return that stream
- **AND** the identification frame's UUID SHALL equal that same `Channel.Id`

#### Scenario: An identifier in a non-canonical spelling does not silently miss
- **GIVEN** an identifier that is a valid UUID but not in canonical lowercase hyphenated form
- **WHEN** it is used to create an AudioSocket external media channel
- **THEN** the SDK SHALL either normalise it to the form the stream table is keyed by, or reject it
- **AND** SHALL NOT produce a created channel whose stream cannot be found

### Requirement: A request that Asterisk cannot route SHALL fail at the create call
The SDK SHALL supply the transport that the requested encapsulation requires, rather than leaving a
combination Asterisk rejects. Where the caller has supplied an audio server whose transport
contradicts the requested encapsulation, the SDK SHALL fail before creating a channel that can never
carry a stream.

A failure to create SHALL surface as the error Asterisk returned, at the create call, and SHALL NOT
be reported later as the audio server having failed to connect.

#### Scenario: AudioSocket encapsulation carries its required transport
- **GIVEN** a caller that asks for AudioSocket encapsulation and specifies no transport
- **WHEN** the external media channel is created
- **THEN** the SDK SHALL send the transport AudioSocket requires
- **AND** the create SHALL NOT fail for want of a transport the caller was never asked for

#### Scenario: An audio server that cannot be reached by the requested encapsulation is refused
- **GIVEN** an AudioSocket server supplied to an activity configured for a non-AudioSocket encapsulation
- **WHEN** the activity starts
- **THEN** it SHALL fail with an error naming the contradiction
- **AND** SHALL NOT wait out its connection timeout and report a connection failure

### Requirement: The ARI external media surface SHALL expose the parameter that names the channel
The SDK's external media create method SHALL accept the ARI `channelId` parameter, which Asterisk
documents as the unique id to assign the channel on creation. Without it a caller cannot choose the
channel id, and therefore cannot make it match anything.

Asterisk spells this parameter in camelCase while its siblings are snake_case, and ignores an
unrecognised parameter silently rather than rejecting it. The SDK's own test for the request SHALL
assert the literal parameter name, so a misspelling fails a test rather than becoming a silent no-op.

#### Scenario: A caller chooses the channel id
- **GIVEN** a caller that has generated an identifier
- **WHEN** it creates an external media channel supplying that identifier as the channel id
- **THEN** the returned channel's id SHALL be that identifier

### Requirement: A stream lookup contract SHALL say which identifier it takes and in what form
Any published contract for looking a stream up by key SHALL state what that key is, in what form, and
where a caller obtains it — rather than naming it only as a channel id. Where two implementations of
the same contract key on different values, the contract SHALL say so.

#### Scenario: A reader can tell what to pass
- **GIVEN** the published documentation of a stream lookup
- **WHEN** a reader holds an ARI channel id and wants the stream for it
- **THEN** the documentation SHALL state whether that value is a valid key for that implementation
- **AND** SHALL state the form the key takes

## Architectural Risk

**Level:** MEDIUM

**Affected:** `Verbara.Sdk` (the `IAriChannelsResource` interface — a signature change on a shipped
public interface, so external implementers break), `Verbara.Sdk.Ari` (the resource class and the
AudioSocket server's documented contract), `Verbara.Sdk.Activities` (`ExternalMediaActivity`), and
downstream `Verbara.Sdk.Pro` AgentAssist, which pairs that activity with an AudioSocket server and is
the consumer the route exists for.

The level is MEDIUM rather than LOW because the change alters a shipped signature in two packages:
`CP0002` in both plus `CP0006` on the interface, and any external implementation of
`IAriChannelsResource` stops compiling. The smaller fix that avoids all of this — mint the identifier
and pass it only as `data` — was considered and rejected in the proposal, because it leaves a
consumer holding only an ARI channel id unable to find the stream, which is the capability itself.

**Mitigation:** the break is declared in a committed `CompatibilitySuppressions.xml` with each entry
read before it is kept, `*REMOVED*` plus new rows in `PublicAPI.Unshipped.txt`, a migration note per
ADR-0028 and a `### Fixed — BREAKING` CHANGELOG entry. The behavioural change reaches only callers
that ask for AudioSocket encapsulation. The end-to-end claim is pinned by a functional test against a
real Asterisk with a subscribed Stasis application, and that test is negatively controlled by
reverting the fix rather than by breaking the assertion — because a mutation that only proves the
assertion is wired is what let a fixture measure nothing for six months.
