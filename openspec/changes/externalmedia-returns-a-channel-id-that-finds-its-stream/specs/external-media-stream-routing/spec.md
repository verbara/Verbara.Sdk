# external-media-stream-routing Delta

## ADDED Requirements

### Requirement: An external media channel SHALL be findable by the identifier its creator holds
A caller that creates an external media channel and owns the audio server it points at SHALL be able
to find that channel's stream using the identifier the create call returned, without correlating two
identifier namespaces by hand.

Where the transport identifies its connection with a caller-supplied value — AudioSocket does, in its
identification frame — the SDK SHALL make the ARI channel id and that value the same value, by
supplying one identifier to both parameters that carry them. The SDK SHALL NOT leave the ARI channel
id to be minted by Asterisk while keying the stream table on a different value, because no lookup can
succeed across those two namespaces.

#### Scenario: A stream is found by the channel id the create call returned
- **GIVEN** an AudioSocket server owned by the caller and running
- **WHEN** the caller creates an external media channel with AudioSocket encapsulation pointing at it
- **AND** Asterisk connects and sends its identification frame
- **THEN** looking the stream up by the returned `Channel.Id` SHALL return that stream
- **AND** the identification frame's UUID SHALL equal that same `Channel.Id`

#### Scenario: A create that cannot be routed does not silently become a timeout
- **GIVEN** a request for AudioSocket encapsulation that omits the identifier Asterisk requires
- **WHEN** the channel is created
- **THEN** the failure SHALL surface as the error Asterisk returned, at the create call
- **AND** SHALL NOT be reported later as the audio server having failed to connect

### Requirement: The ARI external media surface SHALL expose the parameter that names the channel
The SDK's external media create method SHALL accept the ARI `channelId` parameter, which Asterisk
documents as the unique id to assign the channel on creation. Without it a caller cannot choose the
channel id, and therefore cannot make it match anything.

The parameter SHALL be optional and additive: an existing call site that omits it SHALL behave exactly
as before, and the change SHALL NOT alter the shipped public API surface in a breaking way.

#### Scenario: A caller chooses the channel id
- **GIVEN** a caller that has generated an identifier
- **WHEN** it creates an external media channel supplying that identifier as the channel id
- **THEN** the returned channel's id SHALL be that identifier

#### Scenario: An existing caller is unaffected
- **GIVEN** an existing call site that supplies no channel id
- **WHEN** it creates an external media channel
- **THEN** the call SHALL behave as it did before this change

### Requirement: A stream lookup contract SHALL say which identifier it takes
Any published contract for looking a stream up by key SHALL state what that key is and where a caller
obtains it, rather than naming it only as a channel id. Where two implementations of the same contract
key on different values, the contract SHALL say so.

The documentation SHALL be specific enough that a reader can tell whether a value they already hold is
a valid key, without reading the implementation.

#### Scenario: A reader can tell what to pass
- **GIVEN** the published documentation of a stream lookup
- **WHEN** a reader holds an ARI channel id and wants the stream for it
- **THEN** the documentation SHALL state whether that value is a valid key for that implementation

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Sdk.Ari` (the ARI channels resource and the AudioSocket server's documented
contract), `Verbara.Sdk.Activities` (`ExternalMediaActivity`), and downstream `Verbara.Sdk.Pro`
AgentAssist, which pairs that activity with an AudioSocket server and is the consumer the route exists
for.

**Mitigation:** the API change is a new optional parameter, so no shipped signature changes meaning
and `PackageValidation` reports nothing. The behavioural change reaches only callers that ask for
AudioSocket encapsulation — RTP callers take an unchanged path. The end-to-end claim is pinned by a
functional test that originates against a real Asterisk rather than a fake, because the defect being
fixed survived six months of tests that never reached the code they appeared to cover.
