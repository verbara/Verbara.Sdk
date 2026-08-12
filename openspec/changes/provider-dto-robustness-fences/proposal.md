---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Every consumer whose call drops or mis-transcribes because a provider changed a field name, sent an explicit null, or renamed a key — and every reviewer who has to judge whether a green VoiceAi suite means the parser is correct
decision_ref: Sdk/ADR-0046
---

# Proposal: provider-dto-robustness-fences

## Why

ADR-0041 justifies the recording programme by describing what the current fake-server substrate fails
to discriminate:

> *"A parser that depends on field ordering, mishandles null-vs-absent, or would throw on an
> unmodelled sibling field passes today."*

That sentence is a claim about the **suite**, and it is true: a hypothetical parser with any of those
three defects would pass. It is not a claim about System.Text.Json, and it must not be read as one.
So the first question is which of the three the SDK's *actual* parser has — and that was **measured**,
not reasoned about: the shipped DTOs copied verbatim into a standalone `net10.0` probe on SDK
10.0.302, `-c Release`, `Nullable=enable`.

### What System.Text.Json actually does with these DTOs

`VoiceAiSttJsonContext` and `VoiceAiTtsJsonContext` declare **no** `[JsonSourceGenerationOptions]` —
they ship on pure STJ defaults.

| Mutation applied to the vendor's JSON | Measured result |
|---|---|
| Unknown sibling field added | **OK** — skipped |
| Field order reversed | **OK** — irrelevant |
| Field absent | **OK** — CLR default |
| `"is_final": null` (non-nullable `bool`) | **THROW** `JsonException` |
| `"confidence": null` (non-nullable `float`) | **THROW** `JsonException` |
| `"transcript": null` (non-nullable `string`) | **OK — silently null** |
| `"isFinal"` instead of `"is_final"` | **OK — silently `false`** |
| `"confidence": "0.9"` (number as string) | **THROW** |
| Array field sent as an object | **THROW** |

So of the three parser defects ADR-0041 names, two are structurally impossible under STJ's defaults —
it skips unknown members and is order-independent — and the SDK therefore never had them. The third
is real, and worse than stated: it splits into a loud half (a `null` on a value type throws) and two
**silent** halves —

- a `null` lands inside a property the compiler guarantees is non-null, so nothing downstream
  null-checks it and the `NullReferenceException` surfaces at an arbitrary distance from its cause;
- a renamed or recased key is indistinguishable from an absent one, so the member silently takes its
  default — an empty transcript, a `false` finality flag, a zero confidence.

This does not weaken ADR-0041's case for recordings; it narrows what recordings are load-bearing
*for*. **A recording is a photograph: it is evidence about the day it was taken.** Re-capture after a
vendor renames a key and the existing tests, which assert on parsed values, *would* fail. But nothing
in this repository ever re-captures, so between captures the silent classes stay invisible — and the
vendor does not send an explicit `null` on the day you capture at all. The recording programme was
carrying a load it cannot bear alone.

### The exposure, enumerated — and split by direction

Full reachability closure from the **actual** `Serialize` / `Deserialize` call sites in `src/`, over
every member of every nested type. No type is reachable from both root sets; none is unreachable.

| Direction | Types | Throws on explicit `null` | **Silently accepts `null`** | Declared nullable |
|---|---|---|---|---|
| **response** (vendor → SDK) | 22 | 7 | **24** — Stt 10, Tts 1, Realtime 13 | 18 |
| request (SDK → vendor) | 24 | 13 | 45 — Stt 17, Tts 21, Realtime 7 | 6 |
| all declared | 46 | 20 | 69 | 24 |

The number that matters is **24**, not 69. A `null` arriving on a *response* member is the vendor
changing under us — the failure this change exists to catch. A `null` on a *request* member is our
own construction bug, a different failure with a different remedy and a different blast radius, and
conflating the two overstates the vendor-facing exposure by roughly 3×. Tts contributes exactly one
response member (`CartesiaTtsControlMessage.Type`); its two other server→client DTOs are already
fully nullable.

Direct-deserialization tests today: **zero** in `Verbara.Sdk.VoiceAi.Stt.Tests`, **zero** in
`Verbara.Sdk.VoiceAi.Tts.Tests`. Those members are exercised only indirectly, through a fake server
replaying JSON the SDK's own authors wrote — the shared-misreading shape ADR-0041 exists to retire.
`Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests` is the partial exception and the precedent: 18
serialization tests in `Internal/RealtimeMessageSerializationTests.cs`. They are round-trip and
absent-field tests — the file contains no explicit JSON `null` at all, and the one named
`ServerErrorEvent_ShouldHandleNullError` deserializes `{"type":"error"}`, an *absent* member. So the
precedent is the file's shape, not its coverage.

### The remedy is a switch, not 24 assertions

Also measured, on the same probe:

| Candidate | Measured effect |
|---|---|
| `RespectNullableAnnotations = true` | `"transcript": null` now **throws**. Members declared nullable still accept `null`. Unknown siblings and absent fields unaffected. **Turns the silent null class loud.** |
| `[JsonRequired]` on a member | A rename, a recase and an absent field all **throw**. |
| `NumberHandling.AllowReadingFromString` | Number-sent-as-string now parses. Write output is byte-identical either way — the option is **read-only**. |
| `UnmappedMemberHandling.Disallow` | Unknown siblings now **throw** — the switch that would *create* the third defect ADR-0041 names. Must never be set. |
| `PropertyNameCaseInsensitive = true` | Does **not** rescue `isFinal` vs `is_final`; they differ by more than case. |

So the fix is not 24 hand-written null assertions. It is: **make the nullable annotation mean
something at runtime**, then let the annotation carry the contract — `string` means the vendor always
sends a string, `string?` means it may not, and both are enforced instead of decorative.

Two measured limits on that switch, both of which change the design:

**It is member-level only.** `{"alternatives":[null]}` into `Alt[]?`, `["a",null]` into
`List<string>`, `{"k":null}` into `Dictionary<string,string>` and a root payload of `null` **all still
pass**. Collection elements, dictionary values and the root are outside the fence. The Deepgram loop
already defends its element hole by hand — `msg.Channel?.Alternatives?.FirstOrDefault(); if (alt is
null) continue;` — and every other collection read site must be checked the same way rather than
assumed covered.

**It follows the options instance, not the package.** Measured:

| | shipped context | strict call-site options | strict context |
|---|---|---|---|
| read `{"type":null}` | silently null | **THROW** | **THROW** |
| write a null non-nullable member | emits `"type":null` | THROW | THROW |

`new JsonSerializerOptions(Ctx.Default.Options) { RespectNullableAnnotations = true }`, resolved via
`options.GetTypeInfo(...)`, gives the receive path the fence while the send path keeps using the
plain context — read-side enforcement with **zero serialize collateral**. And it is AOT-clean: the
probe rebuilt with `IsAotCompatible=true`, `TreatWarningsAsErrors=true`, `WarningLevel=9999` and the
trim / AOT / single-file analyzers enabled compiles with **0 warnings, 0 errors**.

That measurement is why this change is `MEDIANO` and not `GRANDE`. The earlier scoping assumed the
switch was all-or-nothing per package — one context covering both directions — and therefore that
enabling it forced a full request-DTO audit and a staged rollout to contain the outbound behaviour
change. That premise is false. The outbound path can be left exactly as it is.

### The hazard the switch does *not* remove: union DTOs

The four WebSocket recognizers each funnel **every** frame the socket delivers through **one** DTO
and branch on `.Type` afterwards. `DeepgramResultMessage` is the model for all of them: one
`JsonSerializer.Deserialize` call, then `if (msg?.Type != "Results") continue;`. A vendor contract's
`required:` list is **per message type**, but the DTO is **per socket** — so marking `is_final`
required, correctly per Deepgram's `Results` schema, would throw on every `Metadata` and
`UtteranceEnd` frame the same socket delivers.

And a throw there is not survivable. In `DeepgramSpeechRecognizer`, the `try` around the receive loop
wraps only `ws.ReceiveAsync` and catches only `OperationCanceledException` and `WebSocketException`;
`JsonException` is caught **nowhere** in either `Stt` or `Tts`. It escapes `ReceiveLoopAsync`, the
wrapper's `finally { channel.Writer.TryComplete(); }` ends the consumer's `await foreach` *normally*,
and the exception then resurfaces from `await Task.WhenAll(...)`. One unexpected frame ends the
recognition session mid-call.

`OpenAiRealtime` is built the other way: a **two-pass decode** — `ServerEventBase` to read `type`,
then the specific DTO per branch. Each of its six deserialize roots models exactly one message type,
so a contract's `required:` list maps onto it directly.

Both facts are load-bearing, and neither was in the earlier scoping: **the receive loops must be able
to survive a frame they cannot parse before any throwing fence is placed on them**, and
`[JsonRequired]` is well-defined on a per-message DTO but not on a union DTO.

### Why now

`wiremock-http-provider-substrate` is mid-flight and five of its eight WebSocket surfaces cannot take
a payload recording at all. Reading that as "we are blocked on vendor credentials" is what prompted
the measurement — and the measurement says the highest-value instrument was never blocked on
anything. It needs no credential, no terms review and no vendor artifact.

## What Changes

- **Receive-loop resilience first.** Each provider receive loop gains an explicit decision for a frame
  it cannot parse: log it, count it, continue — never end the session. This is the precondition for
  every fence below, and it is a defect on its own terms today, before any fence exists.
- **Nullability enforced on the read path**, via strict `JsonSerializerOptions` derived from each
  provider context and used at the deserialize call sites. The send path keeps the plain context, so
  no request serialization behaviour changes. Enabling the option on the context instead — which
  fences both directions — stays available and is recorded as the rejected-for-now alternative with
  its cost, not silently foreclosed.
- **Each of the 24 response-side silently-nullable members is triaged**, one by one, into exactly one
  of two outcomes — the vendor can send `null` here, so the member becomes `T?` and every read site
  coalesces explicitly; or it cannot, so it stays non-nullable and the fence enforces it. No member is
  left as "non-nullable because nobody thought about it." Collection-element and dictionary-value read
  sites are checked by hand, because the fence does not reach them.
- **`[JsonRequired]` where the vendor's contract says the field is always sent, and only on a DTO that
  models exactly one message type.** On a union DTO the attribute may be placed only on a field the
  contract marks required for **every** message that DTO decodes — which in practice means the
  discriminator alone. Where a surface's frames warrant more, the fix is to split the union into a
  two-pass decode as `OpenAiRealtime` already does, and that is scoped explicitly rather than smuggled
  in. The authority for *"always"* is the vendor's own published contract, which is what
  `provider-schema-drift-train` (`Sdk/ADR-0047`) supplies; where no licensed contract exists the
  attribute stays **off** and the DTO's XML doc says why.
- **A per-provider wire-mutation test suite.** One file per provider beside the existing recognizer
  and synthesizer tests, driving the same matrix — unknown sibling, absent, explicit null, rename,
  recase, wrong scalar type, wrong shape, null collection element, root null — against the real
  context. `InternalsVisibleTo` is already declared for both suites, so the DTOs are reachable with no
  new plumbing.
- **A Governance guard that `UnmappedMemberHandling.Disallow` is never set** on a provider context —
  neither via `[JsonSourceGenerationOptions]` nor via a type-level `[JsonUnmappedMemberHandling]` —
  with the reasoning inline: tolerating unknown siblings is the *desirable* default, a vendor adding a
  field must not break a released SDK, and a future "let's harden the parser" edit is exactly how that
  protection gets deleted by accident.
- **A Governance guard that a **reachable** DTO cannot ship untested.** Reachability, not registration:
  `VoiceAiSttJsonContext` declares 19 types and registers 17, and the two it omits —
  `DeepgramChannel` and `DeepgramAlternative` — are exactly the types holding `transcript` and
  `confidence`. A guard scoped to registrations would exempt the members the change exists to fence.
- **`Sdk/ADR-0046`** records the durable decision, the measured matrix, and the explicit rejection of
  `UnmappedMemberHandling.Disallow`.

**Not in scope.** The 45 request-side members: their failure mode is our own construction bug, not a
vendor change, and fencing them means accepting a serialize-time behaviour change on the outbound
path. Recorded as deferred with the measured cost so a later reader sees a decision, not an oversight.
`NumberHandling.AllowReadingFromString` is likewise **considered and deferred**: it is read-only
(write output is byte-identical with and without it), so it carries no outbound cost — but a vendor
switching a number to a string is itself a contract change, and tolerating it globally would hide
exactly the drift this programme exists to surface. No observed instance is on record in this repo.
The AMI, ARI, AGI and Sessions JSON contexts are out of scope — they are not third-party wire
contracts under someone else's change control. No new package dependency; no public API surface
change.

## Capabilities

### New Capabilities

None. The requirements land in `provider-contract-fidelity`, the capability introduced by
`wiremock-http-provider-substrate`.

### Modified Capabilities

- `provider-contract-fidelity`: six ADDED requirements. That change established *where the bytes come
  from*; these establish *what the parser must survive when the bytes change*. The two are
  complementary and neither subsumes the other — a recording proves the parser read one real response
  on one day, a mutation matrix proves it fails loudly on every wire change it cannot read.

## Impact

- `src/Verbara.Sdk.VoiceAi.Stt`, `src/Verbara.Sdk.VoiceAi.Tts`,
  `src/Verbara.Sdk.VoiceAi.OpenAiRealtime`: receive-loop parse-failure handling, one strict options
  member per package, per-member annotation and `[JsonRequired]` changes, and the read-site coalescing
  each newly-nullable member forces.
- `Tests/Verbara.Sdk.VoiceAi.Stt.Tests`, `.Tts.Tests`, `.OpenAiRealtime.Tests`: one mutation-matrix
  file per provider.
- `Tests/Verbara.Sdk.Governance.Tests`: two scanners, their guard tests, detector unit tests and
  liveness self-tests.
- `docs/decisions/`: ADR-0046 + index row. `CHANGELOG.md`: one `[Unreleased]` entry.
- **Public API surface unchanged** — every affected type is `internal`, so `PublicAPI.*.txt` does not
  move and nothing cascades to `Sdk.Pro` or `Platform`. The behaviour *inside* those packages does
  change, which is the point.
- CI: no new dependency, no Docker, two more Governance tests plus the per-provider files. Runtime
  cost is negligible — these are in-memory deserializations with no I/O.

## Architectural Risk

**Level:** MEDIUM — the first change in this programme to alter production behaviour rather than test
behaviour.

**Affected:** the deserialization path of every VoiceAi provider. The outbound path is deliberately
left alone: the fence is applied per call site, so request serialization is byte-identical before and
after. A member wrongly triaged as non-nullable turns a previously-tolerated vendor `null` into a
thrown `JsonException` mid-session — which is the intended trade, but only once the receive loop can
survive a throw, which today it cannot.

**Mitigation:** the receive-loop work lands **first** and independently, so the ability to survive a
malformed frame exists before anything is made to throw. The triage is per-member and evidence-driven
rather than a blanket flip, and each of the three packages is staged independently so a regression is
attributable to one provider family. Every fence is negative-tested — remove it, watch the test fail;
restore it, watch it pass — so no rule is accepted on a green run alone. Reverting a package is one
options object, and the mutation tests survive a revert as a record of what the parser actually does.

**The residual risk this change does not close:** a rename can only be caught by `[JsonRequired]`;
`[JsonRequired]` can only be placed where the vendor's contract says the field is always sent; and on
a union DTO it can only be placed where *every* message that DTO decodes agrees. For the surfaces
with no openly-licensed contract — AssemblyAI above all, whose spec repository does not exist — the
attribute stays off and the rename class stays undetected at parse time. That gap is
`provider-schema-drift-train`'s to close, on a different instrument and a different cadence, and it is
named here so it is a known hole rather than an assumed cover.
