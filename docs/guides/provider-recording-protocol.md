# Provider Recording Protocol

> How a real third-party provider response is captured, redacted, documented and committed to this
> repository — and the rules that make doing so safe in a public MIT repo.

Verbara.Sdk owns wire-protocol parity for fourteen speech-provider surfaces it does not control (the
§1 table enumerates them: 6 HTTP + 8 WebSocket). Hand-authored
test fixtures assert what their author believed the vendor sends; a **recording** asserts what the
vendor actually sent. [ADR-0041](../decisions/0041-wiremock-as-http-provider-test-substrate.md)
makes recordings the fixture of record (D4) and bounds what may be committed (D5, D6). This guide is
the operational form of that decision.

Read it before capturing anything. Every recording in this repository is world-readable, permanent
(Git keeps every version forever) and redistributed with every clone.

---

## 1. Scope

| Applies to | Does not apply to |
|------------|-------------------|
| HTTP request/response captures replayed through the shared WireMock fixture (OpenAI Whisper, Azure OpenAI Whisper, Google Speech-to-Text, Azure TTS, Speechmatics TTS, LMNT HTTP) | Provider client code under `src/` |
| WebSocket frame captures used to re-seed the in-process protocol fakes (Deepgram, AssemblyAI, Cartesia, Speechmatics STT; Cartesia, Deepgram, ElevenLabs, LMNT WS) | Anything outside a `Recordings/` tree |
| Hand-authored fixtures that live in a `Recordings/` tree — these are legal, and MUST be labelled `synthetic` (ADR-0041 D4). When authored from the vendor's published protocol documentation they also carry a `source_schema` block (§5) and `terms.verdict: "not-applicable"` (§7) | Audio assets used as *input* by a test that never leave the machine |

---

## 2. Layout

Recordings live under the test project that replays them, never in a shared bucket — a fixture that
no suite can point at is a fixture nobody re-captures.

```text
Tests/<TestProject>/Recordings/
  README.md                                  # one-paragraph pointer to this guide
  <provider-slug>/
    <scenario-slug>.<ext>                    # the capture
    <scenario-slug>.provenance.json          # its provenance sidecar (§5)
```

- **`<provider-slug>`** — kebab-case, transport-qualified where a provider ships more than one:
  `openai-whisper`, `azure-openai-whisper`, `google-stt`, `azure-tts`, `speechmatics-tts`,
  `lmnt-http`, `lmnt-ws`, `deepgram-stt`, `deepgram-tts`, …
- **`<scenario-slug>`** — what the capture *is*, not what it proves:
  `transcribe-short-en-us`, `synthesize-short-en-us`, `error-429-rate-limited`,
  `results-frame-final`.
- **`<ext>`** — the payload's real media type: `.json`, `.wav`, `.mp3`, `.opus`, `.raw`, `.txt`.
- Any file ending in **`.provenance.json` is metadata, never a capture.** Loaders must skip it.

---

## 3. Capture procedure

> **Steps 4–8 are automated for five of the six HTTP surfaces** by
> `scripts/capture-provider-recording.py` (`openai-whisper`, `azure-openai-whisper`,
> `google-speech`, `speechmatics-tts`, `lmnt-http`). It issues the request each SDK client issues
> — the multipart shape for the Whisper surfaces (file part without a `Content-Type`, text parts
> as `text/plain; charset=utf-8`), the compact JSON with raw LINEAR16 for Google, the form
> encoding for LMNT — then redacts, normalizes, writes the sidecar and enforces the cap. What it
> commits follows the surface: the vendor's JSON, the vendor's bytes, or — for LMNT, whose
> verdict is `not-cleared` — the §7 response envelope, with the audio counted and discarded
> rather than written. Credentials are read from the environment and never written or echoed.
> Steps 1–3 and 9–10 are still yours: a tool cannot re-read a terms page or revoke a key.
> Extending it to a new provider means adding one `*_plan` function; doing the capture by hand
> instead means re-deriving the request shape, which is the part that is easy to get subtly wrong.

1. **Confirm the provider's terms still permit it.** Read §7 for the standing per-provider finding,
   then re-read the provider's live terms page. A finding recorded months ago is evidence, not
   permission — the `terms.checked_utc` field in the sidecar exists so the age of that check is
   visible.
2. **Prepare source audio that satisfies §6** (STT captures, and any TTS capture whose input text is
   read aloud from a recording). Synthetic or public-domain only.
3. **Capture with a credential that has never seen production.** That is the invariant, and the
   reason for it is narrow and specific: a key with no access to production data cannot leak
   production identifiers through a response body, no matter what the vendor echoes back. Two ways
   to satisfy it, both acceptable:

   - **A throwaway key** — created for the capture, revoked immediately afterwards.
   - **A standing capture-only account** — a provider account or resource that exists solely for
     this, holds no production or customer data, and is never used by any deployed service. This is
     the better fit when captures recur, and it is what this repository actually does: creating and
     revoking a key per capture is friction that eventually tempts someone to reach for a production
     key instead, which is the failure this rule exists to prevent.

   A standing capture account carries obligations the throwaway does not: its credentials live in a
   local environment file **outside the repository**, never in the working tree, never in a shell
   history, and never pasted into a terminal whose output is transcribed; the variables carry a
   distinguishing prefix so they cannot be confused with deployment secrets; and the key is rotated
   on any suspected exposure rather than on a schedule. **Nothing here relaxes §4** — redaction is
   what keeps a capture safe to publish, and it applies identically whichever credential produced it.
4. **Capture the full exchange, not just the body.** Save the request line, the request headers you
   sent, the response status, the response headers and the response body. The request side is what
   the strict matcher (ADR-0041 D1) is configured from; the response side is the stub.
5. **Redact per §4, immediately, before the file is ever `git add`-ed.** A redaction applied in a
   later commit does not remove the value from history.
6. **Normalize the payload.**
   - JSON: pretty-print with 2-space indent, LF line endings, trailing newline. A capture whose
     bytes shift between platforms is not a fixture.
   - Binary: keep the vendor's bytes exactly. Do not transcode, re-encode or trim — the point of a
     binary capture is that real codec bytes traverse the frame-chunking path.
   - Prune only what is unbounded: a word-level array with 4 000 entries proves nothing a 40-entry
     one does not. Record the pruning in `redaction.notes`.
7. **Write the provenance sidecar (§5).**
8. **Check the size cap (§8).**
9. **Run the guard locally** before pushing:

   ```sh
   python3 scripts/check-recording-redaction.py .
   ```

10. **Reference the capture from a test**, and label the assertion so a reader knows it is replaying
    a real response rather than an invented one.

---

## 4. Redaction rule (ADR-0041 D5)

Nothing in the list below may reach a committed file, in any casing, in any encoding, in a body, in
a header, in a URL, in a filename or in a binary metadata chunk (WAV `LIST`/`INFO`, MP3 ID3, …).

**Never commit:**

- **Credentials** — API keys, subscription keys, bearer tokens, JWTs, refresh tokens, client
  secrets, private keys, or any value that authenticated the capture.
- **Signed URLs** — pre-signed blob/object URLs and their query signatures (`sig`, `X-Amz-Signature`,
  `X-Goog-Signature`, …). They are credentials with an expiry, and the expiry is not a defence.
- **Account identifiers** — account, tenant, subscription, project, organization, billing or
  customer IDs; Azure resource IDs; Google project numbers; deployment names that encode an account.
- **Correlating request/session identifiers** — request IDs, trace IDs, session IDs, operation IDs,
  job IDs. Individually harmless-looking, they tie a public artifact to a real, billed account.
- **Host names that identify an account** — a regional endpoint is fine, a per-resource endpoint is
  not: `https://<resource>.openai.azure.com` must be placeholdered, `https://<region>.tts.speech.
  microsoft.com` may keep the `<region>` placeholder form.

**Placeholders to write instead** — these are the forms the guard recognizes, so use them verbatim:

| Redacted thing | Write |
|----------------|-------|
| API key / subscription key | `REDACTED-API-KEY` |
| Bearer token | `Bearer REDACTED-TOKEN` |
| Query-string key (`?key=…`) | `?key=REDACTED-API-KEY` |
| Any GUID-shaped request/session/trace ID | `00000000-0000-0000-0000-000000000000` |
| Account / tenant / project / billing ID | `REDACTED-TENANT`, `REDACTED-PROJECT`, … |
| Signed URL | `https://example.invalid/redacted` |
| Account-scoped host segment | `<resource>`, `<region>`, `<deployment>` |

Two properties make these safe: they are recognizable to a human reviewer, and the guard's
placeholder allowlist accepts them so a correctly redacted capture stays green.

> **Do not "redact" by shortening.** Truncating a key to its first 8 characters still commits 8
> characters of a live secret. Replace the whole value.

---

## 5. Provenance sidecar

Every capture carries a sibling `*.provenance.json`. **A capture without a sidecar is not
reviewable and must not be merged.**

**Why a JSON sidecar** rather than front-matter, a header comment or a directory-level manifest: a
binary `.wav` cannot carry front-matter at all, so a sidecar is the only format that works
identically for JSON and binary captures. JSON is already this repo's format for machine-read
metadata (`coverage-floor.json`, `coverage-exclusion-baseline.json`), it parses from the standard
library with no new dependency, and a per-file sidecar cannot drift out of sync with a manifest
listing files it no longer describes.

**Schema** (`verbara.recording-provenance/1`):

| Key | Required | Meaning |
|-----|----------|---------|
| `schema` | ✅ | `"verbara.recording-provenance/1"` |
| `class` | ✅ | `"recorded"` (real provider traffic) or `"synthetic"` (hand-authored — ADR-0041 D4) |
| `provider` | ✅ | The `<provider-slug>` from §2 |
| `product` | ✅ | Human-readable product name, e.g. `"Azure AI Speech — text to speech"` |
| `endpoint` | ✅ | Method + URL template, account-scoped segments placeholdered |
| `api_version` | ✅ | The version/deployment the capture was taken against, or `"n/a"` |
| `captured_utc` | ✅ | `YYYY-MM-DD`, UTC. For `synthetic`, the date the fixture was authored |
| `source_audio` | ✅ | Object — see below |
| `redaction` | ✅ | `{ "applied": [ … ], "notes": "…" }`. `applied` lists what was stripped, by kind, never by value |
| `terms` | ✅ | `{ "verdict": …, "basis": …, "checked_utc": … }` — see §7 |
| `source_schema` | conditional | **Required when the fixture was authored from vendor documentation** (`class: "synthetic"`, `terms.verdict: "not-applicable"` — see §7). Object; see below |
| `media_type` | ➖ | IANA media type of the capture |
| `bytes` | ➖ | Size of the capture file |
| `sha256` | ➖ | Hex digest of the capture file (use SHA-256, never MD5 — a 32-hex string is credential-shaped) |
| `notes` | ➖ | Anything a reviewer needs |

`source_schema` (§7, documentation-derived fixtures):

| Key | Required | Meaning |
|-----|----------|---------|
| `url` | ✅ | The vendor documentation page the field set was taken from |
| `revision` | ✅ | The page's own version/last-updated marker, verbatim, or `"undated"` if it publishes none |
| `read_utc` | ✅ | `YYYY-MM-DD`, UTC — when that page was read. Decays exactly like `terms.checked_utc` |
| `method` | ✅ | How the fixture was derived. States that the schema was conformed to and that no vendor example text was copied (§7) |

**`revision: "undated"` is a finding, not a formality.** A vendor that publishes no revision marker
on its protocol page gives a reader no way to tell a silent breaking edit from no edit at all, so a
fixture built from it decays invisibly. Record it and move on — but it is the first thing to re-read
when that provider's suite starts failing.

`source_audio` (§6):

| Key | Required | Meaning |
|-----|----------|---------|
| `origin` | ✅ | `"synthetic"`, `"public-domain"` or `"not-applicable"` |
| `description` | ✅ | How it was produced or where it came from |
| `url` | conditional | Required when `origin` is `"public-domain"` |
| `license` | ✅ | e.g. `"CC0-1.0"`, `"public-domain"`, `"n/a"` |

**Example** — `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/synthesize-short-en-us.provenance.json`:

```json
{
  "schema": "verbara.recording-provenance/1",
  "class": "recorded",
  "provider": "azure-tts",
  "product": "Azure AI Speech — text to speech",
  "endpoint": "POST https://<region>.tts.speech.microsoft.com/cognitiveservices/v1",
  "api_version": "v1",
  "captured_utc": "2026-08-02",
  "media_type": "audio/wav",
  "bytes": 41644,
  "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
  "source_audio": {
    "origin": "not-applicable",
    "description": "TTS input is a fixed English sentence authored for this fixture; no source recording exists.",
    "license": "n/a"
  },
  "redaction": {
    "applied": [
      "ocp-apim-subscription-key request header",
      "x-requestid response header",
      "region-scoped host segment"
    ],
    "notes": "Body bytes are the vendor's, unmodified. Only headers were touched."
  },
  "terms": {
    "verdict": "permitted-with-conditions",
    "basis": "docs/guides/provider-recording-protocol.md section 7 (Azure TTS)",
    "checked_utc": "2026-08-02"
  }
}
```

---

## 6. Source-audio rule (ADR-0041 D5)

Audio submitted to a provider in order to produce a committed capture, and audio committed as a
capture, must be **synthetic or public-domain. Nothing else is admissible.**

**Permitted:**

- Audio generated by a local synthesizer or signal generator (tones, sweeps, noise, an offline TTS
  voice the repo may lawfully use), described in `source_audio.description`.
- Public-domain or CC0 recordings, with the source URL and license in the sidecar. Verify the
  license at the source; a mirror's claim is not evidence.

**Never permitted, without exception:**

- **Customer audio.** Any recording that reached this project through a customer, a deployment, a
  support ticket or a production system — regardless of consent, redaction or age.
- **An identifiable person's voice.** A real person's speech is biometric data in several
  jurisdictions and its subject cannot withdraw it from a public Git history. This applies to
  colleagues and to the capturer's own voice.
- **Copyrighted material** — broadcast clips, music, audiobook excerpts, podcast segments.
- **Audio containing personal data** even if the speaker is synthetic: no real names, phone numbers,
  addresses, account numbers or booking references in the spoken content. Use obviously fictional
  values.

The same rule governs the *text* a TTS capture speaks: fictional content only, nothing that reads as
a real person's statement.

---

## 7. Per-provider terms-of-service findings (ADR-0041 D5)

The six HTTP surfaces were checked on **2026-08-02**, the four remaining WebSocket vendors on
**2026-08-03**. The verdict, the clause it rests on and the residual uncertainty are recorded below.
Copy the verdict into each capture's `terms` block and re-check before capturing.

**How to read the verdicts:**

| Verdict | Meaning |
|---------|---------|
| `permitted` | A clause grants the customer rights in the output that cover committing it here. |
| `permitted-with-conditions` | As above, plus a condition this repo must actively satisfy. |
| `not-cleared` | The terms do not clearly grant it. The conservative fallback applies; do not commit the payload. |
| `not-applicable` | **No Output was captured at all** — the fixture is hand-authored to the vendor's *published protocol documentation*. Legal only with `class: "synthetic"` and a `source_schema` block (§5). See below. |

### How to read a provider's terms — the method, not just the answers

Four of these findings turned on a document that was **not** the ownership clause. Read all three
layers before recording a verdict, in this order:

1. **The ownership / output-rights clause.** Necessary, never sufficient. Its absence is decisive;
   its presence is not.
2. **The acceptable-use or prohibited-use policy the grant is subordinated to.** ElevenLabs and
   Cartesia carry a *word-for-word identical* grant — *"you are permitted to use such Output outside
   of the Services but always subject to these Terms and our Acceptable Use Policy"* — and land on
   opposite verdicts purely on what their AUPs say. A grant routed through an AUP is worth exactly
   what the AUP allows.
3. **Which contract actually binds the capture.** A favourable clause on paper we are not party to
   grants nothing. Deepgram's Master Service Agreement has a clean Output grant that applies only
   under an executed Order Form; the self-serve console terms that govern a throwaway API key have
   no concept of Output at all.

Two further habits earned their keep: **check the revision history** (AssemblyAI's customer
ownership-of-Outputs clause was deleted between contract generations, which reverses the inference a
reader of the current text alone would draw), and **check whether the vendor publishes conflicting
documents** (Deepgram serves two live MSAs whose §8.2 clauses disagree on the single word that
matters).

### Documentation-derived fixtures — the route that needs no verdict

Five of the eight WebSocket surfaces landed on `not-cleared`, which would leave their fakes seeded
with the same hand-authored minimal JSON ADR-0041 D4 exists to retire. There is a second source of
authority that sidesteps the terms question entirely, and where it exists it is **preferred over a
capture**: the vendor's own **published protocol documentation**.

**Why it is not a terms question.** Every verdict in this section is about redistributing a vendor's
*Output* — the transcript or audio its model generated from our input. A fixture authored to a
documented schema contains no Output. It carries the vendor's *interface*: message types, field
names, nesting, cardinality, control-frame ordering. That is the vendor's published authority about
its own wire format, and it is precisely the authority a parser should be checked against — closer to
the thing under test than a single captured sample, which is one draw from the distribution.

**Where the line is, and it matters.** Documentation is copyrighted prose; an interface is not.

- **Do** author frames that carry the documented field set, nesting and types, with **fictional
  values of our own** — the same fictional-content bar §6 sets for spoken text.
- **Do not** paste a vendor's example payload, prose or field descriptions verbatim into the tree.
  Copying their example blob is copying their expression; conforming to their schema is not.

**What it costs, stated plainly.** A documented schema is what the vendor *says* it sends. A capture
is what it actually sent on one day. Where the two disagree the capture wins, and this route cannot
detect that disagreement — it closes the field-set and frame-ordering half of the D4 gap, not the
does-the-vendor-honour-its-own-docs half. Say so in the sidecar's `notes` rather than implying a
fidelity the artifact does not have.

Such a fixture is `class: "synthetic"`, carries `terms.verdict: "not-applicable"`, and **must** carry
the `source_schema` block from §5 so the authority it was built from is checkable and its decay is
visible.

| Provider | Verdict | Rests on |
|----------|---------|----------|
| OpenAI Whisper (STT) | `permitted-with-conditions` | Services Agreement §4.1 assigns all right/title/interest in Output to the customer (read first-hand, v.010126); §3.3 has no publication restriction |
| Azure OpenAI Whisper (STT) | `permitted` | "Output Content is Customer Data. Microsoft does not own Customer's Output Content." |
| Google Speech-to-Text (STT) | `permitted` | "Generated Output is Customer Data … Google does not assert any ownership rights" |
| Azure TTS | `permitted-with-conditions` | Same Product Terms clause + synthetic-voice disclosure duty |
| Speechmatics TTS | `permitted-with-conditions` | ToS §10.3 assigns IP in outputs to the customer; §10.5 disclaims ownership of derivatives |
| LMNT HTTP (TTS) | **`not-cleared`** | No output-rights clause exists; the AUP restricts sharing synthesized speech |
| Cartesia STT + TTS (WS) | `permitted-with-conditions` | ToS §5.3 disclaims ownership of Outputs and §5.3(b) expressly permits use of Output outside the Services; AUP has no sharing restriction. Requires a commercial-use tier |
| Speechmatics STT (WS) | `permitted` | Same ToS §10.3 as the TTS row, and this is the direction it was actually **written about** — it assigns IP in **Transcripts**. The TTS verdict is the weaker inference of the two, not this one. *(Row added 2026-08-14: the finding existed in the §5 prose and in `wiremock-http-provider-substrate`'s task list but had no row here.)* |
| Deepgram STT + TTS (WS) | **`not-cleared`** | The only Output grant (MSA §8.2) binds solely via an executed Order Form; the self-serve console terms have no Output concept and reserve everything |
| AssemblyAI STT (WS) | **`not-cleared`** | §4.4 "Output" grants nothing and §4.1 reserves all rights not expressly granted — the express customer-ownership-of-Outputs clause was **deleted** in the 2026-01-22 revision |
| ElevenLabs TTS (WS) | **`not-cleared`** | ToS §4(c)(ii) grants Output rights but §4(a) subordinates them to the PUP, whose 9(n) bars terms "more permissive than" ElevenLabs' own (MIT is) and 9(l) bars Output as a dataset used for "testing" AI |

### OpenAI Whisper — `permitted-with-conditions`

**Basis — read first-hand (2026-08-03).** `openai.com/policies/*` returns HTTP 403 to automated
fetchers, but OpenAI publishes the same contract as a PDF on its own CDN, which does not:
`https://cdn.openai.com/osa/openai-services-agreement.pdf`, version string **`OpenAI Services
Agreement ONLINE v.010126`**. Section 4.1 verbatim:

> *"4.1. Generally. Customer and Customer's End Users may provide Input and receive Output. As
> between Customer and OpenAI, to the extent permitted by applicable law, Customer: (a) retains all
> ownership rights in Input; and (b) owns all Output. OpenAI hereby assigns to Customer all OpenAI's
> right, title, and interest, if any, in and to Output."*

**Section 3.3 (Restrictions) contains no restriction on publishing or redistributing Output.** Its
nine clauses cover unlawful use, third-party rights, minors, reverse engineering, competing-model
development, data extraction, API-key transfer, service interference and usage limits.

**Restriction that binds us.** §3.3(e): Output may not be used to develop AI models that compete
with OpenAI's products and services, outside the defined "Permitted Exception" (classifiers and
embeddings not distributed commercially, plus OpenAI's own fine-tuning). Committing a handful of
transcripts as test fixtures is not that — the same shape as the Microsoft restriction above, and
the second reason §8's cap is tight.

**Conditions this repo must satisfy.** The provenance sidecar is the attribution and disclosure: it
names the provider, the endpoint and the capture date, and the `class: "recorded"` label states
plainly that the artifact is model output.

**⚠ Residual — narrower than it was.** §4.1's definition of "OpenAI Policies" incorporates the
*Sharing and Publication Policy* by reference, and that page still 403s to automated fetchers, so it
has not been read first-hand. This is a materially smaller gap than the original flag: what was in
question was whether we hold redistribution rights at all, and §4.1 grants them expressly. The
sharing policy imposes *conditions* (attribution, disclosure of the AI's role) rather than a
prohibition, and the sidecar discharges both by construction. Re-read it if a capture is ever
published anywhere other than as a repository test fixture.

### Azure OpenAI Whisper — `permitted`

**Basis.** Microsoft Product Terms, Universal License Terms for Online Services, read directly:
*"Output Content is Customer Data. Microsoft does not own Customer's Output Content."* Output
Content being Customer Data places it under the customer's control, which covers publishing a
transcript the customer generated from its own synthetic audio.

**Restriction that binds us.** The same terms prohibit using Microsoft Generative AI Services to
generate Output Content *"for the express purpose of creating synthetic training data to develop or
train AI models or systems that have substantially similar functionality to a Microsoft AI service."*
Capturing a handful of transcripts as test fixtures is not that. Publishing a corpus would start to
look like it — which is one reason §8 caps size.

### Google Speech-to-Text — `permitted`

**Basis.** Google Cloud Service Specific Terms, AI/ML section: *"Generated Output is Customer Data.
As between Customer and Google, Google does not assert any ownership rights in any new intellectual
property created in the Generated Output."* Reinforced by Google Cloud Terms of Service §5.1:
*"Customer retains all Intellectual Property Rights in Customer Data and Customer Applications."*

**Restrictions that bind us.** Generated Output may not be used to develop a similar or competing
product or service, nor to create or improve models similar to a Google Model. Separately, §7 of the
Service Specific Terms conditions the public disclosure of *benchmark results* on publishing enough
information to replicate the test and on reciprocity. A committed fixture is not a benchmark result
— **but annotating a capture with accuracy or latency comparisons against another provider would
engage that clause.** Do not put comparative measurements in a sidecar or a Recordings README.

**⚠ Uncertainty — flagged.** The Service Specific Terms page is long enough that the AI/ML section's
enumeration of which products count as "AI/ML Services" could not be retrieved verbatim; Speech-to-
Text is expected to be enumerated there but this was not read first-hand. **Confirm Speech-to-Text
appears in that enumeration before the first Google capture is committed.** If it does not, the
clause above does not apply and the verdict drops to `not-cleared`.

### Azure TTS — `permitted-with-conditions`

**Basis.** The same Product Terms clause as Azure OpenAI Whisper: *"Output Content is Customer Data.
Microsoft does not own Customer's Output Content."*

**Conditions this repo must satisfy.**

1. **Disclose the synthetic nature of the voice.** The Microsoft Enterprise AI Services Code of
   Conduct (v4.0, effective 2026-05-01) requires customers to *"Disclose when the output, decisions,
   or actions are generated by AI, including the synthetic nature of generated voices"*. The
   provenance sidecar and the `Recordings/README.md` are that disclosure; do not commit an Azure TTS
   capture without them.
2. **Prebuilt voices only.** The Code of Conduct's voice-services section governs voice models
   trained on a real person's recordings. Never capture from a custom neural voice, a personal
   voice, or any voice built from a real person — which the §6 source-audio rule already forbids.
3. **Smallest useful sample.** The synthetic-training-data restriction cited above is the second
   reason §8's cap is tight: a few seconds of speech is a fixture, minutes of it starts to be a
   corpus.

### Speechmatics TTS — `permitted-with-conditions`

**Basis.** Speechmatics Terms of Service §10.3: *"Speechmatics assigns to You all present and future
Intellectual Property Rights in such Transcripts and You grant Speechmatics a non-exclusive,
worldwide, perpetual, irrevocable license to use the Transcripts solely for the purpose of machine
learning and improving the Software."* §10.5: *"We do not claim ownership in any of your content,
including any audio/video that you may provide to us or the derivatives of that such as
transcription."* The ToS's definition of "Software" explicitly covers *"the speech synthesis
software … which converts words from Text into Audio"*, so the agreement governs the TTS direction.
No clause restricting publication or redistribution of outputs was found.

**⚠ Uncertainty — flagged.** §10.3's express IP assignment is written about **Transcripts** — the
speech-to-text direction. Synthesized *audio* is covered only by §10.5's broader "derivatives of
your content" language, which disclaims Speechmatics' ownership without expressly assigning rights.
That is a weaker footing than the STT direction, and the published terms page showed no effective
date. **Treat synthesized-audio captures as permitted by inference, not by an express grant:** keep
them to the minimum length §8 allows, and re-read §10 before each new capture. If a reviewer is not
comfortable with the inference, the LMNT fallback below applies equally here.

### LMNT HTTP — `not-cleared`

**Basis for the negative finding.** LMNT's Terms of Service (last updated 2023-06-12) contain **no
clause at all** addressing ownership of, or rights in, generated audio output — the document is a
website ToS whose content provisions cover user-submitted content, not service output. LMNT's
Acceptable Use Policy (last updated 2023-08-28) contains the only clause in either document that
speaks to publishing synthesized speech, and it is a restriction: users must not *"Share, publish,
publicly demonstrate any Contributions, Clones, or other synthesized speech outside of Your Entity
without Your Entity's permission."*

**Why this is not read as a permission.** The clause's literal condition is the *entity's* own
permission, which an authorized decision could satisfy. But it is the sole provision on point, both
documents predate the current API by roughly three years, and neither grants the customer any
affirmative right to redistribute output. Reading an express public-redistribution licence out of
that is exactly the kind of confident inference this section exists to avoid.

**Conservative fallback — this is what to implement.** Do **not** commit LMNT-generated audio bytes.
For the LMNT HTTP surface, capture and commit only the **response envelope**: status code, response
headers, media type, content length and observed chunk boundaries, as a `.json` capture with
`class: "recorded"`. Pair it with a body assembled locally from public-domain or synthetic audio
encoded to the same codec and container, committed as a separate `class: "synthetic"` file. The
suite then gets real request matching, a real status/header set and real byte lengths through the
frame-chunking path — everything ADR-0041 wanted from the LMNT migration — without redistributing
LMNT's synthesized speech.

**Revisit when** LMNT publishes API terms or a developer agreement that expressly grants
redistribution of generated audio. Record the new finding here and raise the verdict then, not
before.

### Cartesia (STT + TTS, WebSocket) — `permitted-with-conditions`

**Basis — read first-hand (2026-08-03).** Cartesia AI Terms of Service,
`https://www.cartesia.ai/legal/terms`, *"Last Revised on June 14, 2024"*. One contract governs both
directions and reaches the API, not merely the website: its scope sentence covers *"the website,
cartesia.ai, and its subdomains and APIs"*, and the Data Protection Addendum confirms the public ToS
*is* the Master Service Agreement absent a signed one.

§5.3: *"Cartesia does not claim ownership of any of your Inputs to the Services … or any of the
Outputs you create with the Services."* The clause the verdict actually rests on is §5.3(b):

> *"We may enable you to download Your Outputs from some (but not all) of the Services; in such
> cases, you are permitted to use such Output outside of the Services but always subject to these
> Terms and our Acceptable Use Policy."*

**The AUP is what makes this a `permitted`.** `https://www.cartesia.ai/legal/acceptable-use`
(*"Last revised on July 23, 2025"*) contains **no restriction on sharing, publishing or redistributing
synthesized speech** — its prohibitions are content-category ones. Its scope sentence even assumes
out-of-service use: *"including use outside our website and the Services"*. This is precisely where
LMNT failed and ElevenLabs failed.

**Conditions this repo must satisfy.**

1. **Capture on a commercial-use tier.** §4.1 permits *"personal, non-commercial use only (unless
   commercial use is expressly permitted by your subscription tier)"*, and §5.3(b) reaches use *"for
   or in connection with any commercial purposes"*. This SDK is the open-core base of a commercial
   line; a Free-tier capture is `not-cleared` on its face. Record the tier in the sidecar's `notes`.
2. **Stock/prebuilt library voices only** — never an instant or professional clone.
3. **Smallest useful sample**, per §4.2's bar on using Output to develop competing products.

**⚠ Residuals.** The commercial-use permission appears as a **pricing-page feature bullet**, not a
contract clause, while §4.1 and §5.3(b) both defer to "your subscription tier" — thin footing that a
written confirmation from Cartesia would firm up. Separately, Cartesia's library voices are built
from real voice actors and no published statement describes the scope of their consent; §4.2 forbids
making available *"any other person's voice without that person's express permission"*. Its defined
term "Make Available" is scoped *"through the Services"*, which is a real mitigant but a definitional
one. **The redistribution right is express; the voice-identity question rests on inference.** Finally,
the ToS predates Cartesia's STT product, so transcripts ride the Output definition's catch-all
(*"or other content or outputs based on such Inputs"*) rather than a product-specific clause.

### Deepgram (STT + TTS, WebSocket) — `not-cleared`

**This is not a finding that Deepgram forbids it.** It is a finding that Deepgram publishes no
agreement granting a *self-serve* API customer the right to redistribute Output — and that the one
agreement which does grant it is not the agreement a throwaway key is captured under.

**The favourable clause is real, and on the wrong paper.** Master Service Agreement,
`https://static.deepgram.com/business/MSA_20240315.pdf`, read first-hand. §8.2:

> *"Customer owns and retains all right, title, and interest in and to the Content, Training Data,
> and the Output…"*

Its Software definition covers both directions (*"translating speech to text, text to speech, or text
to text"*) and Output is deliberately excluded from §7.1's confidential-information list. On its own
terms this would be a clean `permitted-with-conditions`. But the MSA opens:

> *"This Master Service Agreement ("Agreement") is entered into as of the Effective Date of the Order
> Form to which it is attached … by and between Deepgram and the customer named there ("Customer")."*

Every right in it is granted under that Agreement, and §1 defines an Order Form as *"an ordering
document to purchase a Subscription to the Software executed between"* the parties. **A throwaway
console key — which §3 of this guide requires — is not an executed Order Form.**

**What governs a console key is adverse.** `Business_TOS_Console.pdf` (*"Last Modified: [07-21-21]"*,
still live) has **no concept of "Output" at all**; its only ownership clause is input-side (*"received,
directly or indirectly, from you"*), which a generated transcript or audio frame is not, leaving it
inside Deepgram's reserved Intellectual Property. `deepgram.com/terms` is a website Terms of Use that
never mentions API output.

**⚠ Deepgram serves two live MSAs whose §8.2 clauses disagree.**
`https://static.deepgram.com/business/Business_TOS.pdf` (Feb 2024, at the more canonical URL) reads
*"…in and to the Content and Training Data"* — **"and the Output" is absent** — and defines Output as
*"a machine created audio to text transcript of Content and associated metadata"*, so synthesized
audio is not Output on that paper at all. Verified by downloading both PDFs and diffing the clause.
That is not a foundation for a permanent public artifact.

**Restrictions that bind regardless**, since capturing means calling the API: MSA §2.2(a) bars use of
Output *"for … competitive purposes"* — broader and vaguer than the competing-model clauses
elsewhere in this section — and the console terms bar *"benchmarking or competitive analysis"*
outright, a prohibition rather than Google's disclosure condition. §2.2(e) forbids representing that
audio Output was human-created, which the sidecar's `class: "recorded"` discharges.

**⚠ Residual.** A dated self-serve API terms document (control number `4142-6587-5017`, Last Modified
2024-03-20) is consistently surfaced by search indexing but returns 404 at every URL tried, has no
Wayback capture in its window, and could not be read. Indexing claims it lets Deepgram *"require you
to cease using [Output] (and delete any copies of them)"* — which a permanent public Git history
structurally cannot honour. Even taken at face value, the best-indexed reading of the likeliest
governing document is worse for us than silence.

**Revisit when** Deepgram republishes a dated self-serve API terms document containing an express
Output-rights clause — read first-hand, not through indexing. The verdict also rises to
`permitted-with-conditions` under an **executed Order Form incorporating the `MSA_20240315` form**;
confirm the countersigned copy carries both the three-way Software definition and the *"and the
Output"* wording, since the Feb-2024 form has neither.

### AssemblyAI (STT, WebSocket) — `not-cleared`

**The grant existed and was removed.** This is the strongest negative finding in this section,
because the current silence is documented as deliberate rather than accidental.

Terms of Service, `https://www.assemblyai.com/legal/terms-of-service`, *effective 2026-07-01*, read
first-hand. §4.4 is **titled "Output"** and contains a disclaimer plus two restrictions — no
ownership clause, no licence. §4.1 closes the gap silence might leave:

> *"Subject to the limited rights expressly granted hereunder, AssemblyAI and its licensors reserve
> all of their right, title and interest in and to the Services. No rights are granted to Customer
> hereunder other than as expressly set forth herein."*

The customer-rights clause that does exist (§4.3) covers *Customer Data*, defined in §1.7 as content
*"submitted … by or on behalf of Customer"* — the input side. Output is generated by the Platform,
so it falls outside.

**Verified by diffing revisions.** The 2025-04-18 version carried §4.2 *"Customer's Property"*:

> *"Customer exclusively owns all right, title and interest in and to the Customer Materials,
> **Outputs**, Inputs, and Customer's Confidential Information, unless otherwise herein provided."*

That clause is absent from the current text, while §4.3 for Customer Data was kept and two further
ownership clauses in AssemblyAI's own favour were added. Reading a redistribution licence back into
this document is not a stretch — it is a reversal. Corroborating practice: AssemblyAI's own MIT-
licensed Node SDK commits audio **input** fixtures only, no recorded output.

**Restrictions that bind regardless.** §2.4(f) bars *"competitive analysis or benchmarking"*
outright — stricter than Google, which conditions only disclosure. §2.4(ii) makes the source-audio
rule contractual: material provided *"for purpose of testing, evaluating, or benchmarking"* must have
been *"independently created by the Customer"* or fully licensed; §6's synthetic-or-public-domain
rule is what satisfies it.

**⚠ Residual.** There is no express *prohibition* on publishing Output either — the true position is
no grant and no ban. This guide's rule resolves that against us, and more firmly here than for LMNT,
because a grant existed and was withdrawn. Treat `terms.checked_utc` as expiring quickly for this
vendor.

### ElevenLabs (TTS, WebSocket) — `not-cleared`

**A grant that its own use policy takes back.** ToS §4(c)(ii) (`https://elevenlabs.io/terms-of-use`,
Last Updated 2026-03-31, read first-hand) says *"you retain all rights in and to your Output"*, and
§4(a) permits using Output outside the Services — *"but always subject to these Terms and our
Prohibited Use Policy"*. That routing is decisive, because the PUP
(`https://elevenlabs.io/use-policy`, Last Updated 2025-09-03) contains two clauses that describe this
exact act, both verified verbatim against the live page:

> **9(n)** *"Making any B2B2B …, B2B2C …, or other similar use of our Services or their Output
> available to your end users on terms that are less restrictive or more permissive than the terms
> under which our Services and their Output have been made available to you."*

MIT grants downstream cloners rights *"without restriction … to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell"*. ElevenLabs' grant to us is *"limited, non-exclusive,
non-transferable, non-sublicensable, revocable"*. MIT is categorically more permissive.

> **9(l)** *"Using any part of our Services or their Output as part of a dataset that may be used for
> training, fine-tuning, developing, **testing**, or improving any machine learning or artificial
> intelligence technology."*

A `Recordings/` tree is a dataset whose stated purpose is testing, in a Voice AI SDK. The word is in
the prohibition verbatim. 9(j) separately bars Output used to develop anything competing with
ElevenLabs, which sells conversational agents into contact-centre workloads.

**Tier dependence is real and does not rescue it.** ToS §1(c) limits Free users to non-commercial
use. A paid plan is a *precondition to reaching* the 9(n) problem, not a solution to it.

**⚠ Residual.** 9(n) is drafted for reseller and bundling scenarios, and *"to your end users"* is a
strained fit for anonymous repository cloners; 9(l)'s *"artificial intelligence technology"* is
arguably not a WebSocket frame parser. A narrow reading might exclude us. But this guide's standard
is an express grant, and what exists is an express grant expressly subordinated to two restrictions
that describe our act.

**Fallback fits unusually well.** ElevenLabs' TTS WebSocket returns JSON carrying base64 `audio`
alongside `alignment` / `normalizedAlignment` objects. Commit the real frame envelope and alignment
structure as `class: "recorded"`, with the audio payload replaced by locally-synthesized bytes of
identical length as `class: "synthetic"`. Note for review that alignment data is still *derived from*
Output. Use only default/premade voices — Voice Library voices are contributed by real people under
a separate addendum and must never be captured.

---

## 8. Size cap and Git-LFS (ADR-0041 D6)

**The cap is 256 KiB (262 144 bytes) per binary capture file.**

**Why 256 KiB.**

- *It is far more than the assertion needs.* The frame-chunking path is proven by crossing the frame
  boundary repeatedly, not by running long. At the 320-byte frame the TTS suites already use, 256
  KiB is 819 frames — roughly 16 s of 8 kHz 16-bit mono PCM, 8 s at 16 kHz, or about a minute of
  32 kbps mono MP3. No defect visible at 800 frames is invisible at 80.
- *Every clone pays, forever.* This is a public MIT repository and Git keeps every version of every
  binary in history. Six HTTP surfaces at two captures each sit near 3 MiB at the ceiling; one
  re-capture generation doubles it. That is affordable precisely because the ceiling exists — an
  uncapped "just commit the whole response" habit is what turns a source repo into a media repo.
- *It is a compliance instrument, not only a bandwidth one.* Azure's Product Terms forbid generating
  Output Content to build synthetic training data for a substantially similar service, and Google's
  AI/ML terms forbid using Generated Output to create or improve similar models (§7). Keeping every
  sample to seconds keeps the `Recordings/` tree unmistakably a set of test fixtures rather than a
  redistributable voice corpus.

**Text captures** are not formally capped, but a JSON capture above **64 KiB** is a smell — it
almost always means an unpruned word-level or timing array. Prune it (§3.6) instead of committing it.

**Git-LFS.** Captures at or below the cap are committed as **ordinary Git blobs, not LFS objects.**
LFS is deliberately not the default: a clone or CI job that does not fetch LFS receives a pointer
file instead of the fixture, and not every job in this repo's CI checks out LFS. Two of the guard's
behaviours follow from that choice — it refuses to scan an unfetched pointer rather than reporting a
false green, and this is why the exception path is narrow.

A capture that genuinely must exceed 256 KiB requires all three of:

1. an explicit justification in review, recorded in the sidecar's `notes`;
2. placement under `.../Recordings/large/`, which `.gitattributes` tracks under Git-LFS; and
3. `lfs: true` on the checkout of every CI job that reads it — including the job that runs the
   redaction guard.

The `.gitattributes` rules that support this:

```text
**/Recordings/**/*.wav  binary
**/Recordings/**/*.mp3  binary
**/Recordings/**/*.opus binary
**/Recordings/**/*.ogg  binary
**/Recordings/**/*.raw  binary

**/Recordings/**/*.json text eol=lf

**/Recordings/large/**  filter=lfs diff=lfs merge=lfs -text
```

The `binary` attributes stop Git from attempting line-ending conversion or textual diffs on codec
bytes; the `text eol=lf` rule keeps JSON captures byte-identical across platforms, which matters
when a test asserts a content length. The LFS rule is written **last** on purpose — attribute
precedence is last-match-wins, and placing it after the `binary` rules keeps an over-cap capture on
the canonical `filter=lfs diff=lfs merge=lfs -text` set that `*.onnx` already uses.

---

## 9. Enforcement

Documentation alone does not enforce a redaction rule (ADR-0041 D5), so a guard does:

```sh
python3 scripts/check-recording-redaction.py [repo-root]
```

It walks every directory named `Recordings/` (skipping `bin/`, `obj/` and other build output),
scans every file underneath — text *and* binary, because an API key can sit in a WAV `LIST` chunk or
an ID3 tag — and exits non-zero on the first credential-shaped hit. It reports the file, the line
and the pattern name, and deliberately **never prints the matched value**, so a CI log cannot become
the second place the secret leaked.

Three behaviours worth knowing:

- **Placeholder-aware.** The §4 placeholder forms, the nil GUID and single-character fills are
  recognized and pass. Redact properly and the guard stays quiet.
- **Self-checking.** Before scanning, the guard runs every pattern against a built-in positive
  canary and a negative one. A regex broken by a careless edit fails the build loudly instead of
  silently matching nothing — the same liveness posture as the coverage guards' minimum-file-count
  fence.
- **LFS-pointer aware.** A file that is still an unfetched LFS pointer fails the run rather than
  reading as clean.

Its unit tests live in `scripts/tests/test_check_recording_redaction.py` and run in CI alongside the
other guard-script tests.

**The guard is a backstop, not the rule.** It recognizes credential *shapes*. It cannot know that a
particular deployment name encodes an account, that a spoken phrase names a real customer, or that a
capture came from a provider whose terms do not permit it. Those are review's job.

---

## 10. Maintenance and decay

A recording is a photograph, not a contract. Nothing in this repository re-captures it, so a
fixture slowly ages into asserting a wire format the vendor no longer sends. ADR-0041 accepts that
explicitly: this protocol closes the *shared-misreading* gap, not the *drift* gap.

Practical consequences:

- `captured_utc` is the recording's age. Treat a capture more than a year old as due for review when
  its suite is touched.
- Re-capturing means re-running §3 in full, including §7 — terms change.
- When a capture is replaced, delete the old file rather than versioning it alongside the new one.
  The history keeps it; the working tree should not.
- If a provider is dropped from the SDK, delete its `Recordings/` subtree in the same change.
