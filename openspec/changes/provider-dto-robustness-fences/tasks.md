# Tasks — provider-dto-robustness-fences

Execution follows Subagent-Driven Development with FCM batching:
**Phase A (batch)** = §1 baseline + §2 inventory · **Phase B (focused, one package at a time)** =
§3 receive-loop resilience + §4 triage + §5 the read-path fence + §6 required-attributes ·
**Phase C (batch)** = §7 tests + §8 guards + §9 records + §10 verification.

§3 lands **before** §5 and §6 in every package. Today a `JsonException` from a provider frame escapes
the receive loop, the writer completes as if the stream ended normally, and the exception resurfaces
from the task the consumer awaits — so one unexpected frame ends the session. Every fence below
deliberately creates that exception; placing one first would trade a silent wrong answer for a dropped
call.

The three packages are executed **in sequence, not in parallel** — `Stt`, then `Tts`, then
`OpenAiRealtime` — so a regression is attributable to one provider family.

## 1. Baseline — evidence before any edit

- [ ] 1.1 Reproduce the measurement in-repo rather than trusting the standalone probe: a temporary
      xunit theory over the mutation matrix against `VoiceAiSttJsonContext.Default`, run
      `-c Release`. Record the actual outcome per mutation. This is the row set §7 must turn from
      *observed* into *asserted*, and it must be produced against the real context, not a copy
- [ ] 1.2 Confirm the enumeration independently, **by direction**. The scope is a reachability closure
      from the real `Serialize` / `Deserialize` call sites in `src/`, not the registration list:
      **response** 22 types / 49 members (7 throw on explicit `null`, **24** silently accept one, 18
      nullable) — Stt 10, Tts 1, Realtime 13; **request** 24 types / 64 members (13 / 45 / 6); 46
      types and 113 members declared in total, of which 69 are silently nullable across both
      directions. Re-derive them from the tree; if the numbers differ, the difference is the finding
      and the scope is corrected before §4 starts. **69 is the whole-file figure and is not this
      change's scope** — 24 is
- [ ] 1.3 Confirm the reachability closure covers the two types `VoiceAiSttJsonContext` declares but
      does **not** register — `DeepgramChannel` and `DeepgramAlternative`, which hold `transcript` and
      `confidence`. Any scope taken from `[JsonSerializable]` registrations excludes them, and they
      are exactly where the headline rename scenario lands
- [ ] 1.4 Grep the read sites of every provider DTO member and record which ones already coalesce
      (`?? string.Empty`, `is { Length: > 0 }`, null checks). A member whose read site already
      handles null is evidence the vendor *does* send null there — that is a §4.1 answer arriving for
      free, and it is stronger evidence than reading the vendor's docs
- [ ] 1.5 Enumerate every read site whose type is a **collection element**, a **dictionary value** or
      the deserialization **root**. Measured: the nullability fence does not reach any of the three —
      `{"alternatives":[null]}`, `["a",null]`, `{"k":null}` and a root payload of `null` all still
      pass with it enabled. `DeepgramSpeechRecognizer` already guards its element hole
      (`Alternatives?.FirstOrDefault(); if (alt is null) continue;`); the rest must be checked
      individually and not assumed covered
- [ ] 1.6 Unit-lane wall clock before any edit, ≥3 runs, so §10.4 compares against a spread

## 2. Inventory — one row per member, checked in as working evidence

- [ ] 2.1 Produce a per-member table for all three contexts: declaring type, member, wire name, CLR
      type, nullable?, value type?, request or response, current classification. Keep it in the
      change directory for the duration; it is the worksheet §4 fills in and the artifact a reviewer
      checks the triage against
- [ ] 2.2 Split every reachable type into **request** (SDK → vendor) and **response**
      (vendor → SDK) from its actual call site. Measured today: the two sets are disjoint and nothing
      is unreachable — if that stops holding, a type used in both directions is called out explicitly
      rather than filed under one
- [ ] 2.3 Record, per provider, whether its decode is **union** (one DTO per socket, branch on a
      discriminator afterwards — all four WS recognizers and the three WS synthesizers) or
      **two-pass** (`ServerEventBase` first, then the specific DTO — `OpenAiRealtime`, six
      deserialize roots). §6 depends on this distinction and it is not derivable from the DTOs alone
- [ ] 2.4 For each response member, record whether an openly-licensed vendor contract exists for its
      surface and what it says about optionality — the input §6 needs. Where no contract exists
      (AssemblyAI has no spec repository at all), record that as the answer; do not substitute a
      reading of the vendor's prose documentation

## 3. Receive-loop resilience — first, and on its own

- [ ] 3.1 For each provider receive loop, bring the `JsonSerializer.Deserialize` call inside a `try`
      and catch `JsonException` explicitly. `JsonException` is caught **nowhere** in
      `src/Verbara.Sdk.VoiceAi.Stt/` or `.Tts/` today; the loops catch only
      `OperationCanceledException` and `WebSocketException`
- [ ] 3.2 A frame that fails to parse is logged and counted on the package's existing logging and
      metrics surfaces, then skipped. It must not complete the channel writer, and it must not
      propagate to the task the consumer awaits
- [ ] 3.3 Trace and record what happens today, per package, so the change is attributable: in
      `DeepgramSpeechRecognizer` the exception escapes `ReceiveLoopAsync`, the wrapping
      `finally { channel.Writer.TryComplete(); }` ends the consumer's `await foreach` **normally**,
      and the exception then resurfaces from `await Task.WhenAll(sendTask, receiveTask)`. Confirm the
      shape for each of the other loops rather than assuming it is identical
- [ ] 3.4 A test per provider: feed the loop a frame that cannot parse, assert the session continues
      and the next good frame is delivered, and assert the failure was counted. This is the test that
      makes every later fence safe, so it is written before them and not with them
- [ ] 3.5 Its own commit per package, ahead of §5 — a resilience fix reviewable on its own is worth
      having whether or not the rest of this change ships

## 4. Triage the 24 response-side silently-nullable members

- [ ] 4.1 For each of the 24, decide **one** of: (a) the vendor can send `null` here → re-declare
      `T?`; or (b) it cannot → leave non-nullable, to be enforced by §5. Every decision carries its
      evidence: the vendor contract, an existing coalescing read site from §1.4, or a recorded
      capture. "No evidence either way" is a valid third state and resolves to (a) — the tolerant
      choice — with the uncertainty recorded on the member
- [ ] 4.2 Every member moved to `T?` gets an explicit coalesce at each read site, chosen and
      commented rather than defaulted: an absent transcript is not the same as an empty one, and the
      code must say which it means
- [ ] 4.3 Close the §1.5 holes by hand in the same pass: a null collection element, a null dictionary
      value and a root `null` are outside the fence, so each read site handles them explicitly
- [ ] 4.4 `TreatWarningsAsErrors` will surface every read site a new `?` breaks. That list is the
      proof the annotation was previously decorative — record its size per package before fixing it
- [ ] 4.5 No member is left non-nullable by omission. A reviewer must be able to point at any of the
      24 and find a recorded decision; §2.1's worksheet is complete when every row has one

## 5. The read-path fence — one package at a time

- [ ] 5.1 Add one internal strict-options member per provider package —
      `new JsonSerializerOptions(Ctx.Default.Options) { RespectNullableAnnotations = true }`,
      `MakeReadOnly()`, resolved through `options.GetTypeInfo(...)` — and use it at the deserialize
      call sites only. Measured: this enforces on read while the plain context keeps serializing
      exactly as before, and it compiles clean under `IsAotCompatible=true`,
      `TreatWarningsAsErrors=true`, `WarningLevel=9999` with the trim / AOT / single-file analyzers
      on (0 warnings, 0 errors)
- [ ] 5.2 `Stt` first, then `Tts`, then `OpenAiRealtime`; each its own commit, so a bisect lands on
      one provider family. `RealtimeJsonContext` already carries
      `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]`,
      so its strict options must be derived from that context and not built from scratch, or the
      snake-case naming is lost
- [ ] 5.3 Prove the outbound path is untouched: serialize every request DTO before and after and
      assert the bytes are identical. This is the whole reason the fence is at the call site rather
      than on the context, so it is asserted rather than asserted-about
- [ ] 5.4 Record the rejected alternative explicitly rather than leaving it implicit:
      `[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]` on the context fences both
      directions — measured, a non-nullable member left `null` then **throws at serialize** instead
      of emitting `"field":null`. That is arguably the better end state, but it is a request-path
      behaviour change requiring a full construction-site audit, and it is deferred to §9.2 as a
      decision rather than foreclosed
- [ ] 5.5 Negative-test the fence per package with a mutation the suite did not already cover: feed a
      response member an explicit `null`, watch it throw with the fence and land silently null
      without it. Note that `RealtimeMessageSerializationTests.cs`'s 18 tests are **not** that net —
      the file contains no explicit JSON `null`, and `ServerErrorEvent_ShouldHandleNullError`
      deserializes `{"type":"error"}`, an absent member. Run them before and after regardless, and
      account for any that change

## 6. `[JsonRequired]` — vendor authority *and* DTO arity

- [ ] 6.1 Place `[JsonRequired]` only where §2.4 found a machine-readable vendor contract declaring
      the field required **and** §2.3 recorded the DTO as modelling exactly one message type. Cite
      the contract and its pinned commit in the member's XML doc — the attribute's justification must
      be checkable without re-reading the vendor's site
- [ ] 6.2 On a **union** DTO the attribute goes only on a field required by *every* message that DTO
      decodes. `DeepgramResultMessage` is the worked example: it decodes every frame the socket
      delivers and branches on `Type`, so `is_final` — required by Deepgram's `Results` schema — must
      **not** be marked, because `Metadata` and `UtteranceEnd` frames arrive through the same DTO. In
      practice this leaves the discriminator alone
- [ ] 6.3 Where a surface warrants more than the discriminator, the remedy is to split the union into
      a discriminator-first two-pass decode, as `OpenAiRealtime` already does. Scope that per surface
      as its own decision with its own commit; do not smuggle a decode-architecture change in under
      an attribute
- [ ] 6.4 Where no licensed contract exists, leave the attribute **off** and say so on the member: an
      absent attribute must read as "we could not establish this" and never as "we established it is
      optional". AssemblyAI is the worked example — its spec repository does not exist, and its SDK's
      generated types describe only the retired v2 surface
- [ ] 6.5 Do not mark a field required on the strength of a recorded capture alone. One response
      containing a field is not evidence the vendor always sends it; that is the inference this
      programme exists to stop making
- [ ] 6.6 Per member marked required, a test that a payload missing it fails, a test that a payload
      missing a *non*-required sibling still parses, and — on any union DTO — a test that every other
      message type on that socket still decodes. The second and third are what prove the attribute
      was placed deliberately

## 7. Wire-mutation test suites

- [ ] 7.1 One shared matrix definition, used identically by every provider, so the parser's
      behaviour is a stated theory and not a per-provider accident. Mutations: unknown sibling,
      absent, explicit `null`, renamed key, recased key, wrong scalar type, wrong shape, empty
      object, empty array, **null collection element**, **null dictionary value**, payload is JSON
      `null`. The last three are in the matrix precisely because the fence does not cover them
- [ ] 7.2 One file per provider in the existing per-provider folder, matching the suite's naming and
      namespace conventions — e.g.
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramDtoRobustnessTests.cs` in
      `Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram`. `InternalsVisibleTo` is already declared in both
      packages' csproj, so no plumbing is added
- [ ] 7.3 Tests assert the **contract**, not today's observation. Where a mutation's current
      behaviour is a silent default, the test asserts the required behaviour and fails until §4–§6
      fix the DTO. A test written to match current behaviour freezes the defect
- [ ] 7.4 Every test name follows `Method_ShouldExpected_WhenCondition`
- [ ] 7.5 Where a recorded capture exists for a surface, the matrix is applied **to the capture** —
      mutate the vendor's own bytes rather than a hand-authored payload, so the base case is real.
      Only Whisper, AzureWhisper (Stt) and Azure (Tts) qualify today; name the rest as
      hand-authored base cases so the difference in fidelity is visible in the test file
- [ ] 7.6 Realtime has 18 serialization tests in
      `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeMessageSerializationTests.cs`
      — round-trip and absent-field coverage, with **no** explicit JSON `null` anywhere in the file.
      Extend it to the shared matrix rather than adding a parallel one, and treat the explicit-null,
      rename, recase and null-element rows as **new** coverage rather than as overlap to reconcile

## 8. Governance guards

- [ ] 8.1 `UnmappedMemberHandlingScanner` — fails when unmapped members are disallowed on a provider
      type or context, in **any** of its forms: `JsonSourceGenerationOptions.UnmappedMemberHandling`,
      a type-level `[JsonUnmappedMemberHandling]`, or a `JsonSerializerOptions` initialized with it.
      Roslyn-syntactic like every scanner in `Tests/Verbara.Sdk.Governance.Tests/`, never regex over
      raw text, so the guard's own snippets and XML docs cannot self-flag. 1-based lines; both
      arguments null-guarded
- [ ] 8.2 Its failure message states *why*: tolerating unknown siblings is the deliberate default, a
      vendor adding a field must not break a released SDK, and this guard exists because a future
      "harden the parser" edit is exactly how that protection gets deleted
- [ ] 8.3 `ProviderDtoCoverageScanner` — every type **reachable** from a provider context must have
      wire-mutation coverage. Reachability, not `[JsonSerializable]` registration:
      `VoiceAiSttJsonContext` declares 19 types and registers 17, and the two omitted —
      `DeepgramChannel`, `DeepgramAlternative` — hold `transcript` and `confidence`. A
      registration-scoped guard would exempt them. Reports the type and the file that declares it
- [ ] 8.4 State the guard's limit in its own failure message and its XML doc: it is a **text** scanner
      and can prove which options object a call site names, not what that options object does at
      runtime. The runtime behaviour is §7's job. A guard that implies more than it checks is worse
      than one that states its boundary
- [ ] 8.5 Detector unit tests for both: true positive with the exact 1-based line and file in the
      message; immunity for the same text appearing in a comment, an XML doc and a plain string
      literal; immunity for non-provider contexts (AMI, ARI, AGI, Sessions), which are not
      third-party wire contracts and are out of scope
- [ ] 8.6 Liveness self-tests with a conservative `MinimumScannedFiles` floor below the real count,
      with the real count named in the comment — the established shape, so a broken locator fails
      instead of reporting a clean scan of nothing
- [ ] 8.7 Negative-test both guards end to end: introduce the violation, watch the guard fail naming
      file and line, remove it, watch the suite return to green
- [ ] 8.8 `Verbara.Sdk.Governance.Tests` has **zero** `ProjectReference`s by design — it reads the
      tree as text. Neither scanner may add one

## 9. Decision record and docs

- [ ] 9.1 Write `docs/decisions/0046-provider-dto-nullability-enforced-at-runtime.md`: the measured
      matrix (shipped defaults, every remedy candidate, and the three holes the nullability fence
      does not cover), the decision that the nullable annotation is the runtime contract on the read
      path, `[JsonRequired]` on vendor authority *and* DTO arity, and the explicit rejection of
      `UnmappedMemberHandling.Disallow`. Related: ADR-0003 (source generators over reflection),
      ADR-0014 (raw client transports for VoiceAi providers), ADR-0041 (recordings as the provider
      evidence class), ADR-0047 (vendor contracts as an evidence class — the authority this ADR's
      `[JsonRequired]` rule depends on)
- [ ] 9.2 Record what ADR-0041 does and does not say, precisely, rather than claiming to correct it.
      Its sentence — *"A parser that depends on field ordering, mishandles null-vs-absent, or would
      throw on an unmodelled sibling field passes today"* — is a true statement about the **suite's**
      discriminating power over a counterfactual parser. This ADR adds the measurement of the SDK's
      **actual** parser: two of those three defects are structurally impossible under STJ's defaults,
      and the third is real and splits into a loud half and two silent ones. ADR-0041 is
      `Accepted`-status and append-only; nothing in it is edited
- [ ] 9.3 Record two deferrals with their measured cost, so neither omission reads as an oversight:
      **(a)** the 45 request-side silently-nullable members and the context-level switch that would
      fence them — the switch is symmetric, so enabling it converts "silently sends `"field":null`"
      into "throws at send", which is the better end state but a request-path behaviour change
      needing its own construction-site audit. Record with it the measured trap that audit will hit:
      `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` **outranks** the nullability
      fence — a non-nullable member carrying it is silently omitted from the payload instead of
      throwing, so the obvious remedy for a newly-throwing serialize quietly disarms the fence that
      surfaced it; **(b)**
      `NumberHandling.AllowReadingFromString` — measured **read-only** (write output is byte-identical
      with and without it, so it carries no outbound cost), deferred because a vendor switching a
      number to a string is itself a contract change and tolerating it globally would hide the drift
      this programme exists to surface. No observed instance is on record in this repo
- [ ] 9.4 Add the ADR-0046 row to `docs/decisions/README.md` in numeric order, matching the existing
      row format
- [ ] 9.5 `CHANGELOG.md` — one `[Unreleased]` entry. This one is **not** test-only: it changes shipped
      deserialization behaviour and receive-loop failure handling in three packages, so it belongs
      under a `### Changed` heading describing the behaviour change for consumers, not under a tests
      heading. State that request serialization is unchanged, because that is the question a consumer
      will ask
- [ ] 9.6 State the residue explicitly: on surfaces with no openly-licensed vendor contract, and on
      union DTOs regardless of contract, a rename remains undetectable at parse time. Name those
      surfaces and point at `provider-schema-drift-train`

## 10. Verification

- [ ] 10.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors (`TreatWarningsAsErrors`)
- [ ] 10.2 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`
      green — the four-exclusion filter `ci.yml` actually uses
- [ ] 10.3 AOT validation unchanged. The strict options are built from the source-generated context's
      own options and resolved through `GetTypeInfo`, which introduces no reflection — measured
      warning-free under the AOT and trim analyzers — but `aot-validate.yml` is the proof rather than
      the assumption
- [ ] 10.4 Unit-lane wall clock before/after, ≥3 runs, reported as a spread. State plainly if the
      delta is inside the noise floor
- [ ] 10.5 Pack the three packages and confirm no public API movement — every affected type is
      `internal`, so `PublicAPI.*.txt` must be byte-identical. If it moves, the scope was wrong
- [ ] 10.6 `openspec validate provider-dto-robustness-fences --type change --strict` clean
- [ ] 10.7 CI green on the PR, zero warnings; enqueue with `gh pr merge <pr> --auto` (merge queue —
      never `--squash` / `--delete-branch`)
