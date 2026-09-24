# external-media-stream-routing Specification

## Purpose

How an application that created an external media channel finds the audio stream that belongs to it.
The answer sounds like it should be free — the create call returns a channel, the server holds a table
of streams, look one up by the other — and it was not free. The two sides were keyed on different
identifiers drawn from different namespaces, and nothing in either contract said so.

Asterisk decides this, not the SDK. For AudioSocket, the `data` parameter of
`POST /channels/externalMedia` becomes the UUID in the identification frame, which is what the stream
table is keyed by; the separate `channelId` parameter becomes the ARI `Channel.Id`. Supply one value
to both and the two are the same value. Supply neither — which is what this SDK did — and Asterisk
mints its own channel id, which appears in no table, so the lookup cannot succeed by construction.

This capability exists because that could not be reasoned out. It was measured, with two distinct
byte-order-asymmetric UUIDs per request so the capture said both *which* parameter travelled and in
*which* byte order. Reading the code would have produced a plausible answer, and the review that
tried it produced a wrong one.

So the requirements here cover three things that are easy to state and were each got wrong: that a
request the SDK builds must be one Asterisk can actually route, and must fail at the create rather
than as a connection timeout thirty seconds later; that the identifier's *form* is part of the
contract, because the table is an ordinal dictionary and an uppercase UUID creates a channel whose
stream cannot be found; and that a published lookup must say which identifier it takes, since the
same sentence — "Get an active stream by channel ID" — sat on an interface and on two implementations
that key on different things.

The WebSocket transport is deliberately outside this capability until its key has been measured
rather than read.

## Requirements

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
