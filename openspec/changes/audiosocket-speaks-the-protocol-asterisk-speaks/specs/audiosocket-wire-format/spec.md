# audiosocket-wire-format — Delta

## ADDED Requirements

### Requirement: The AudioSocket frame format is the one Asterisk sends, and is pinned by captured bytes
Every implementation of the AudioSocket protocol in this SDK SHALL read and write the frame header
Asterisk actually puts on the wire: **one byte of type followed by two bytes of big-endian length**, and
SHALL use the frame-type values `res_audiosocket` defines rather than any value invented here. An
identification frame carries a sixteen-byte UUID in RFC 4122 order, which MUST be parsed big-endian.

No implementation may treat its own encoder as the definition of the format. The format SHALL be pinned
by **bytes captured from a real Asterisk**, decoded by every parser in the repository, so that a parser
and its encoder cannot agree with each other while both disagree with the wire. Where more than one
package speaks the protocol, one captured fixture SHALL serve them all, because a per-package fixture
reproduces the closed loop it exists to break.

The protocol's source SHALL be cited where the format is defined, so that the next reader can check it
rather than inherit it.

#### Scenario: The identification frame Asterisk sends is understood
- **GIVEN** the first bytes a real Asterisk sends after connecting, `01 00 10` followed by sixteen bytes
- **WHEN** a parser reads them
- **THEN** it reports an identification frame carrying that UUID, rather than an audio frame of some
  length taken from the UUID's own bytes

#### Scenario: A parser is held to captured bytes rather than to its own encoder
- **GIVEN** a fixture of frames captured from a real Asterisk
- **WHEN** any parser in the repository decodes it
- **THEN** it yields the frame types and payloads Asterisk sent
- **AND** a test that instead built its input with the encoder under test would not satisfy this
  requirement, because it cannot fail when both sides share a wrong format

#### Scenario: The round trip reaches a real Asterisk
- **GIVEN** a dialplan extension that hands a call to this SDK over AudioSocket
- **WHEN** a call is originated into it
- **THEN** the server reports a session for the UUID the dialplan named

#### Scenario: A frame type the SDK does not act on is still recognised
- **GIVEN** a frame whose type the implementation has no behaviour for, such as DTMF
- **WHEN** it arrives
- **THEN** it is identified as that type and skipped by its declared length, rather than resynchronising
  the stream or being mistaken for audio

## Architectural Risk

- **Level:** MEDIUM. Small, exactly evidenced, but it changes a wire format and the values of two enums
  on shipped public types (`Verbara.Sdk.Ari`, `Verbara.Sdk.VoiceAi.AudioSocket`). Enum values are
  inlined at compile time, so a consumer must recompile.
- **Affected:** both AudioSocket packages, and any consumer that implemented the SDK's previous invented
  dialect. That population is believed empty because the dialect cannot complete a handshake with
  Asterisk, but it is believed rather than known, and the changelog says so.
- **Mitigation:** the captured-bytes fixture is written before the fix and is the test that would have
  failed six months ago. A functional test closes the loop against a real Asterisk. Both packages move
  in one change so the two parsers cannot drift while one is corrected.
- **Residual:** correcting the wire format does not make the ARI AudioSocket route work end to end — its
  stream table is keyed by the wire UUID while its only consumer looks up by ARI channel id — nor does
  it wire the ten declared-but-unemitted audio metrics. Both are tracked separately; this is their
  prerequisite, not their fix.
