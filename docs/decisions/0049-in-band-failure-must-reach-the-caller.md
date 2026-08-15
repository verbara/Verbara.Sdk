# ADR-0049 — In-band failure must reach the caller

- **Status:** Accepted
- **Date:** 2026-08-15
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0048 (wire conformance by live probe with a negative control — this ADR is its
  direct continuation: 0048 established the method and characterised six defects, and applying the
  same method to four further credentials produced both a seventh defect and a measurement that
  generalises 0048's **D9**). ADR-0048 is `Accepted` and is **not edited here** — its `not
  characterised` rows for Cartesia STT and AssemblyAI STT were true on the day they were written,
  and this ADR supersedes them with evidence rather than rewriting the record. ADR-0014 (the
  provider clients are hand-rolled `ClientWebSocket` code, so this repo owns the receive loop).

## Context

ADR-0048 recorded one surface — Speechmatics realtime STT — whose credential is rejected *after* a
successful WebSocket upgrade, and drew a conditional rule from it (**D9**): a handshake alone is
sufficient evidence only where the vendor authenticates in the upgrade headers. That rule was
correct but rested on a single in-band example against a single header-auth example.

On 2026-08-15 four further credentials became available (ElevenLabs, Cartesia, AssemblyAI, Google).
Probing them under the ADR-0048 method — every surface with **two** controls, a wrong path and a
deliberately invalid credential, both on the same host — settles which vendors sit on which side of
D9, and the answer is not the one the SDK's code shape implies.

### Measured 2026-08-15 — where the credential is actually validated

Every row below carries both controls. "In-band" means the vendor returned `101 Switching
Protocols` for a credential it then rejected.

| Surface | Wrong path | **Invalid credential** | Where auth is validated |
|---|---|---|---|
| Deepgram TTS / STT | `404` | **not measured** | *inferred* handshake header — **not established** |
| Cartesia TTS | `404` | `401` | **handshake header** |
| Cartesia STT | `404` | `401` | **handshake header** |
| Speechmatics STT | — | `101` → close `4001 not_authorised` | **in-band** |
| ElevenLabs TTS | `403` | `101` → text frame `{code, error, message}`, `error=invalid_api_key` | **in-band** |
| AssemblyAI STT | `404` | `101` → text frame `{error, error_code, type}`, "Unauthorized Connection: Invalid API key" | **in-band** |

**Of the five surfaces carrying an invalid-credential control, three authenticate in-band.** Deepgram
is the sixth row and it carries no such control: ADR-0048 probed its *route* and never its
credential, so "handshake header" there is an inference, and it is recorded as one rather than
counted. That the single uncontrolled row is also the only one whose validation point this ADR cannot
state is D4 demonstrating itself on the evidence it was drawn from.

Credential *placement* is no guide either, and not uniformly: five of the six send it in a request
header (`Authorization`, `xi-api-key`, `X-API-Key`), while `SpeechmaticsSpeechRecognizer.cs:195` puts
it in the query string (`?jwt=`). The exception cuts against inference rather than for it —
Speechmatics is the query-string surface *and* an in-band validator, so neither placement predicts
the validation point. A reader of this repository — or of any of these clients — cannot infer the
answer from the source, and inferring it is what produced the wrong expectation in the first place.

### The seventh defect, and why it is a pattern rather than an incident

Each in-band vendor signals its rejection as a *message*. All three SDK clients discard it — and a
grep for the shape found it is not confined to them.

- `SpeechmaticsSpeechRecognizer` keeps `AddPartialTranscript` / `AddTranscript` and `continue`s past
  everything else — so the `Error` message is dropped (recorded in ADR-0048).
- `AssemblyAiSpeechRecognizer.cs:137` — `if (!string.Equals(msg.Type, "Turn", StringComparison.Ordinal)) continue;`
  Structurally identical. The auth-error frame is not a `Turn`, so it is dropped. **This is the
  seventh defect and it was not known when ADR-0048 was written.**
- `ElevenLabsSpeechSynthesizer` reads only `WebSocketMessageType.Binary`. ElevenLabs sends its audio
  *and* its errors as text, so the client loses the audio and the reason for losing it in the same
  branch.

In all three the caller observes the same thing: an `IAsyncEnumerable` that completes **normally**
and **empty**. No exception, no log, no counter. A rejected session is indistinguishable from a
silent one, which is why a fully green suite shipped every one of them.

The shape is wider than the three surfaces where it currently bites. `CartesiaSpeechRecognizer.cs:165`
(`Type != "transcript"`) and `DeepgramSpeechRecognizer.cs:120` (`Type != "Results"`) are the same
construction — **five sites in total**. Those two are latent rather than harmless: their vendors
happen to reject credentials at the handshake, so no auth frame reaches the discard branch today, but
every *other* error either vendor defines still falls into it, and a vendor that later moves
validation in-band converts them without a line changing. D1 therefore binds all five, not the three
with a live symptom.

This also answers the question ADR-0048 left open for ElevenLabs. Probed live, it closes `1000`
(normal closure) after emitting only text frames: it does complete successfully having yielded zero
audio bytes, exactly as Cartesia does. The zero-output failure is not a Cartesia quirk.

## Decision

**D1 — A receive loop MUST NOT silently discard a frame that carries a failure.** Filtering
lifecycle frames the caller does not need is legitimate and stays (Speechmatics `Info` is the
example: skipping it is correct and deliberate). Filtering by an allow-list of *content* message
types is not, because every unanticipated type — including every error the vendor defines — falls
into the discard branch by default. The discriminator is the frame's meaning, not its absence from
a content allow-list.

**D2 — Completing successfully with zero output is a failure, on recognizers as well as
synthesizers.** ADR-0048 adopted this for synthesizers on the strength of Cartesia. It is hereby
general: a provider session that produced no audio and no transcript, and that was not asked to
produce none, does not report success. A request whose input legitimately warrants no output stays
distinguishable — the discriminator is "frames arrived and were discarded" versus "no frames
arrived".

**D3 — Where a vendor validates the credential is a measured property, recorded per surface.** It is
never inferred from where the client places the credential — neither from a header nor from a query
string. Every WebSocket surface carries its validation point in the scoreboard, established by an
invalid-credential control on the same host; a surface without that control records the point as
**not established**, which is why Deepgram's row above says so rather than borrowing ADR-0048's
route result.

**D4 — An invalid-credential control is required alongside the wrong-path control for any surface
whose auth claim matters.** ADR-0048's negative control demonstrated the probe could distinguish
*routes*. It cannot, on its own, demonstrate the probe distinguishes *credentials* — and for the
three in-band vendors a route-only control would have reported a passing `101` for a session the
vendor refused. Two controls, two questions.

## Consequences

- Positive: the silent-failure class is now named and measured rather than met one provider at a
  time. Three surfaces share one root cause and get one remedy shape.
- Positive: D9 of ADR-0048 is no longer resting on one example per side. It is measured on five
  surfaces, and three of those five fall on the side a handshake-only probe would have got wrong.
- Positive: two surfaces recorded as `not characterised` in ADR-0048 (Cartesia STT, AssemblyAI STT)
  now carry route and auth evidence with both controls, and Google STT moves from an uncontrolled
  live capture to a controlled probe.
- Negative: D1 forbids a coding shape that is currently used in five clients — three with a live
  symptom, two latent — and is genuinely convenient. Auditing every receive loop for allow-list
  filtering, and remediating the latent pair that no measured defect forces, is work this ADR creates
  and does not fund; `provider-wire-protocol-conformance` carries the tasks.
- Negative: D4 doubles the control cost of every future surface probe. Accepted, because the thing
  it catches is the failure mode that shipped.
- Neutral: Cartesia TTS's **frame inventory is still not characterised**. The 2026-08-15 probe
  reached `101` and then sent a malformed synthesis request, so the vendor answered with an error
  frame rather than audio. Route and auth are established with both controls; the frame shape is
  not, and is not recorded as if it were. Cartesia's Class B defect continues to rest on the
  vendor-documentation read of 2026-08-14.
- Neutral: this ADR says nothing about *parsing* the error frame once surfaced — the DTO shape is
  the concern of the change reserving ADR-0046.

## Alternatives considered

- **Fix AssemblyAI as a one-line change and record nothing.** Rejected: the same line exists in
  Speechmatics, the equivalent branch exists in ElevenLabs, and each was written by someone who
  believed they were filtering harmlessly. A per-incident fix leaves the belief intact and the next
  receive loop repeats it.
- **Require every frame type to be modelled.** Rejected as the wrong layer and too strong. The
  requirement is that a failure reaches the caller, not that every lifecycle field is deserialised;
  modelling is the concern of the change reserving ADR-0046, and D1 deliberately keeps deliberate
  lifecycle filtering legal.
- **Treat in-band auth as a vendor quirk to be worked around per provider.** Rejected on the
  measurement: three of the five credential-controlled surfaces do it, which is too many to treat as
  per-vendor exception handling even before the sixth is controlled.
