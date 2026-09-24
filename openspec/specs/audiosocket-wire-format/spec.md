# audiosocket-wire-format Specification

## Purpose

What the bytes of the AudioSocket protocol are, and where that answer is allowed to come from. The
format belongs to Asterisk's `res_audiosocket`, not to this repository: a header of one type byte and
two big-endian length bytes, the frame-type values that module defines, and an identification frame
carrying sixteen UUID bytes in RFC 4122 order. Nothing here may invent a value and nothing here may
redefine one.

The capability exists because the opposite was shipped. Both AudioSocket implementations in this SDK
read a **four**-byte header and believed `0x01` meant audio; one of them also mapped Asterisk's audio
frame to `Error` and ended its read pump on it. Neither had ever completed a handshake with Asterisk,
and both were covered: thirty-nine tests across twenty-five encoding sites, green for six months.

They were green because each parser was measured against its own encoder. A closed loop agrees with
itself perfectly and says nothing about the wire — the two halves drift together, and every assertion
still passes. So the requirements below are not only about which bytes are correct. They are about
**where the bytes come from**: a fixture captured from a real Asterisk, decoded by every parser in the
repository, one fixture serving all of them, because a per-package capture rebuilds the same closed
loop one level up.

A conformance claim about someone else's protocol is only as good as its evidence. This capability's
evidence is a recording, and the requirements keep it that way.

## Requirements

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
