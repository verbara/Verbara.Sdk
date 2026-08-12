# provider-contract-fidelity — Delta

## ADDED Requirements

### Requirement: A provider receive loop survives a frame it cannot parse
A provider's WebSocket receive loop SHALL treat a frame it cannot deserialize as an observable,
recoverable event and MUST NOT let it end the session. The loop MUST log the failure, count it on a
metric, and continue reading. This is a precondition for every parse-time fence in this capability: a
fence that throws is only usable in a loop that can absorb a throw.

#### Scenario: One unparseable frame does not end a live call
- **GIVEN** a recognition session in progress
- **WHEN** the provider sends a frame the SDK's DTO cannot deserialize
- **THEN** that frame is logged and counted, the loop reads the next frame, and the consumer's stream continues — rather than the exception escaping the loop, the writer completing as if the stream had ended normally, and the failure resurfacing later from the task the consumer awaits

#### Scenario: A parse failure is visible rather than silent
- **GIVEN** a frame that fails to deserialize
- **WHEN** the loop absorbs it
- **THEN** it is recorded on the package's existing logging and metrics surfaces, because a swallowed parse failure is a worse outcome than a loud one and the only acceptable middle is a counted one

### Requirement: Nullability annotations on provider DTOs are enforced on the read path
Deserialization of a third-party provider response SHALL enforce the DTO's nullability annotations, so
that a member declared non-nullable rejects an explicit JSON `null` instead of silently accepting one.
The annotation MUST therefore be treated as the contract: `T` means the vendor always sends a value,
`T?` means it may send `null`, and a member declared `T` that the vendor can legitimately null MUST be
re-declared `T?` with an explicit coalesce at every read site rather than left to the default. The
enforcement MUST be scoped to the read path; a change that also fences serialization alters the
outbound request path and is a separate decision with a separate audit.

#### Scenario: An explicit null on a non-nullable member fails at the boundary
- **GIVEN** a provider response whose field the SDK models as a non-nullable reference type
- **WHEN** the provider sends that field as an explicit JSON `null`
- **THEN** deserialization fails naming the property and its declaring type, rather than assigning `null` into a property the compiler guarantees is non-null and surfacing a `NullReferenceException` at an arbitrary distance from the cause

#### Scenario: A member the vendor may legitimately null is declared nullable
- **GIVEN** a member for which the vendor's contract permits `null`
- **WHEN** this requirement is applied
- **THEN** that member is declared nullable and its read sites coalesce explicitly, so the payload still parses and the handling of the missing value is visible in the code rather than implied

#### Scenario: Request serialization is unchanged
- **GIVEN** the SDK building any provider request
- **WHEN** the read-path enforcement is in place
- **THEN** the serialized request bytes are identical to what the SDK sent before, because the fence is applied where responses are read and nowhere else

#### Scenario: The holes the annotation fence does not cover are closed by hand
- **GIVEN** a member whose type is a collection, a dictionary, or the deserialization root
- **WHEN** the vendor sends a null element, a null value, or a null payload
- **THEN** the read site handles it explicitly, because annotation enforcement is member-level and does not reach inside a collection, a dictionary or the root — and a fence assumed to cover them would be worse than no fence

### Requirement: A field the vendor always sends is declared required, on the vendor's authority and the DTO's arity
A member whose absence would silently yield a CLR default SHALL carry `[JsonRequired]` only when two
conditions hold together: the vendor's published contract states the field is always present, and the
DTO models exactly one message type. A renamed or recased key is indistinguishable from an absent one,
and `[JsonRequired]` is the only instrument this capability permits that detects either —
`UnmappedMemberHandling.Disallow` would also detect a rename but is banned for a separate and
overriding reason. Because a contract's required-set is declared **per message** while a union DTO
decodes **every** message on its socket, the attribute MUST NOT be placed on a union DTO except on a
field required by every message that DTO decodes. Where a surface needs more, the DTO MUST be split
into a discriminator-first two-pass decode before any further attribute is placed.

#### Scenario: A renamed field fails loudly instead of defaulting
- **GIVEN** a member marked required because the vendor's contract lists the field as always sent
- **WHEN** the vendor renames or recases that key
- **THEN** deserialization fails, rather than the member taking its CLR default and the SDK reporting an empty transcript, a false finality flag or a zero confidence as if the vendor had said so

#### Scenario: A field required by one message type does not fence a whole socket
- **GIVEN** a DTO that decodes every frame a provider socket delivers and branches on a discriminator
- **WHEN** the vendor's contract marks a field required for one of those message types only
- **THEN** the attribute is not placed, because it would reject every other frame the same socket legitimately sends — and the recorded remedy is to split the decode by discriminator, not to fence the union

#### Scenario: A genuinely optional field is not marked required
- **GIVEN** a field the vendor's contract declares optional
- **WHEN** the vendor omits it
- **THEN** the payload parses and the member takes its documented default, because marking an optional field required would turn a legal response into a failure

#### Scenario: An unverifiable field records the gap
- **GIVEN** a surface with no openly-licensed machine-readable contract
- **WHEN** its DTO is written
- **THEN** the members carry no required attribute and say why on the member itself, so a later reader can tell "we confirmed this is optional" apart from "we could not confirm anything"

### Requirement: Unknown sibling fields are tolerated, and the switches that would break that are banned
Provider DTO deserialization SHALL tolerate fields the SDK does not model, and a provider DTO or
context MUST NOT disallow unmapped members — neither through `JsonSourceGenerationOptions`, nor
through a type-level `[JsonUnmappedMemberHandling]`, nor through options constructed at a call site. A
vendor adding a field is routine and MUST NOT break an already-released SDK. A source-scanning guard
MUST fail the build when any of those forms appears on a provider type or context, because the failure
mode is a well-intentioned future edit that "hardens" the parser and silently deletes this protection.

#### Scenario: A vendor adding a field does not break a released SDK
- **GIVEN** a released SDK version parsing a provider response
- **WHEN** the vendor adds a field the SDK does not model
- **THEN** the response still parses and the unmodelled field is ignored

#### Scenario: The guard fails the build when the tolerance is removed
- **GIVEN** an edit that disallows unmapped members on a provider context or on a provider DTO
- **WHEN** the guard suite runs
- **THEN** the build fails naming the file and the 1-based line, and the failure message states why the default is the deliberate choice

### Requirement: Every reachable provider DTO is covered by a wire-mutation test
Each type **reachable** from a provider `JsonSerializerContext` — not merely each type registered in
one — SHALL be exercised by a test that applies the full wire-mutation matrix: unknown sibling field,
absent field, explicit `null`, renamed key, recased key, wrong scalar type, wrong shape, null
collection element, and a root payload of `null`. Reachability is the scope because a nested type
carrying load-bearing fields need not be registered to be deserialized, and a scope taken from
registrations would exempt exactly those. The matrix MUST be identical across providers so the
behaviour is a stated theory of the parser rather than a per-provider accident, and each test MUST
assert the outcome the requirement demands rather than recording whatever the serializer happens to do
today.

#### Scenario: A nested unregistered type is still covered
- **GIVEN** a DTO reachable only as the member type of a registered DTO
- **WHEN** coverage is computed
- **THEN** it is in scope, because the fields the SDK actually reads can live there and a registration-based scope would silently skip them

#### Scenario: A mutation test asserts a contract, not an observation
- **GIVEN** a mutation whose current behaviour is a silent default
- **WHEN** the test for it is written
- **THEN** it asserts the behaviour the requirement demands, so the test fails until the DTO is fixed rather than freezing the defect as expected

### Requirement: A new provider cannot ship without contract coverage
A source-scanning guard SHALL fail the build when a type reachable from a provider
`JsonSerializerContext` has no corresponding wire-mutation coverage, so that adding a provider carries
its fence with it. The guard MUST report the offending type and the file that declares it, and MUST
verify it scanned a plausible number of files so an empty enumeration cannot read as a pass.

#### Scenario: Adding a provider without a mutation test fails the build
- **GIVEN** a developer adding a new DTO reachable from a provider context
- **WHEN** they open a pull request without a mutation test for it
- **THEN** the guard fails naming the type, so the coverage arrives with the provider rather than as follow-up work that never happens

#### Scenario: A broken locator cannot pass as green
- **GIVEN** the guard's file enumeration
- **WHEN** it returns fewer files than the conservative floor the suite declares
- **THEN** the suite fails on the liveness self-test, because a scan that walked nothing would otherwise report no violations

## Architectural Risk

**Level:** MEDIUM.

**Affected:** production deserialization for every VoiceAi provider. This is the first requirement set
in this programme that changes shipped behaviour rather than test behaviour: a member wrongly triaged
as non-nullable converts a previously-tolerated vendor `null` into a thrown `JsonException`
mid-session. Request serialization is explicitly out of scope and unchanged, because the enforcement
is applied where responses are read rather than on the context both directions share.

**Mitigation:** the receive-loop requirement lands first, so the loops can absorb a parse failure
before anything is made to throw — without it, every fence below converts a recoverable frame into a
dropped session. The triage is per-member and evidence-driven rather than a blanket flip, and each
package is staged independently so a regression is attributable to one provider family. Every rule is
enforced by something that fails — two Governance scanners and a mutation matrix — and each is
negative-tested by removing the fence, observing the failure, and restoring it. Reverting a package is
one options object, and the mutation tests survive a revert as a record of what the parser actually
does.

**Not closed by these requirements:** a renamed field on a surface with no openly-licensed vendor
contract stays undetectable at parse time, because `[JsonRequired]` cannot be placed without an
authority for "always sent" — and on a union DTO it cannot be placed even with one, unless every
message on that socket agrees. That residue belongs to the schema-drift instrument, on a different
cadence, and is named here rather than assumed covered.
