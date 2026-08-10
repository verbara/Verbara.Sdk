# Provider Test Substrate

> Which fake server a speech-provider test suite runs against — and why that is decided by the
> provider's transport rather than by preference.

Verbara.Sdk tests fourteen speech-provider surfaces against in-process fakes. There are two
substrates, and **which one a suite uses is not a choice**: it follows from whether the provider
speaks HTTP request/response or a bidirectional WebSocket session.
[ADR-0041](../decisions/0041-wiremock-as-http-provider-test-substrate.md) D2/D3 fixes the split so
that "use WireMock everywhere" cannot be adopted by drift, and so that a WebSocket suite's use of a
hand-rolled server reads as a decision rather than as an unfinished migration.

---

## 1. The rule

| Provider transport | Substrate | Lives in | Reference suite |
|---|---|---|---|
| HTTP request/response | **WireMock.NET**, via the shared `HttpProviderMockServer` fixture | `Tests/Verbara.Sdk.TestInfrastructure.Http` | `Tts.Tests/Azure/AzureTtsSpeechSynthesizerTests.cs` |
| Bidirectional WebSocket | **`WebSocketTestServer`** + a per-provider protocol fake | `Tests/Verbara.Sdk.TestInfrastructure` | `Stt.Tests/Deepgram/DeepgramSpeechRecognizerTests.cs` |

WireMock.NET's contract is HTTP/1.1 request matching. The WebSocket suites need a server that holds
a duplex session, receives client audio frames, and can close or abort abnormally on command —
behaviours those suites already assert, and which `WebSocketTestServer` exists to get right on Linux.
Trading a validated purpose-built server for uniformity would pay real reliability for cosmetic
consistency (ADR-0041 Option F).

**Neither substrate is what closes the fidelity gap.** The *recording* does. A WebSocket suite gets
the same D4 treatment as an HTTP one: its frames are recorded provider frames, not hand-authored
JSON. See [provider-recording-protocol.md](provider-recording-protocol.md).

The two substrates are **separate projects on purpose.** Referencing WireMock adds a
`FrameworkReference` to `Microsoft.AspNetCore.App`, which stops ~30 `Microsoft.Extensions.*`
assemblies reaching the output directory; coverlet's Cecil resolver then silently skips instrumenting
the modules that reference them. When WireMock briefly lived in the shared `TestInfrastructure`
project, line coverage fell 80.42% → 61.96% with every test still green. Do not merge them back.

---

## 2. A provider that ships both transports

Split by **transport, not by suite** (ADR-0041 D3). One file, one test class per transport, each on
its own substrate. `Tts.Tests/Lmnt/LmntSpeechSynthesizerTests.cs` is the worked example:
`LmntSpeechSynthesizerWsTests` stays on `WebSocketTestServer`, `LmntSpeechSynthesizerHttpTests` moves
to WireMock. Keeping them in one file keeps the provider's behaviour readable in one place; putting
them in one class would force one substrate onto both.

---

## 3. HTTP suites: matching is strict

The shared fixture sets `AllowPartialMapping = false`, so a request that does not match **404s**
instead of receiving the canned body. Configure the expected request exhaustively — method, exact
path, every query parameter, the auth header — because everything you leave out is an assertion you
are not making. A misrouted or unauthenticated request should fail the test, which is precisely what
the old `MockHttpMessageHandler` (one canned response for every call, whatever it was) could not do.

Assert the *unmatched* shape too. A test that sends a deliberately wrong API key and expects the
provider to fail is what proves the matcher is actually matching.

### The origin seam (ADR-0041 D12)

A loopback server is reachable only through `HttpClient.BaseAddress`. A provider that composes an
absolute URL itself ignores that, so it takes an `internal` test-only origin parameter — the
precedent is `SpeechmaticsSpeechSynthesizer` (`_fakeBaseUri`) and `LmntSpeechSynthesizer`
(`_fakeHttpBaseUri`).

**The seam substitutes scheme/host/port and nothing else.** The route stays in production code, so
the strict matcher asserts the path the provider really builds rather than one the test handed it.
A seam that accepted a full URL would delete the assertion it exists to enable.

Use the IPv4 loopback literal (`127.0.0.1`), never `localhost` — [ADR-0044](../decisions/0044-ipv4-loopback-literal-for-test-servers.md).

Providers that read a configurable endpoint from their own options (`WhisperOptions.Endpoint`,
`AzureWhisperOptions.Endpoint`) need no seam at all. Check for one before adding it.

---

## 4. Adding a new provider suite

1. Identify the transport. That picks the substrate; there is nothing to decide.
2. Capture a real response and commit it with its provenance sidecar —
   [provider-recording-protocol.md](provider-recording-protocol.md). Do this *before* writing the
   tests: the capture routinely disagrees with what you expected the vendor to send, and finding
   that out after the tests are written means rewriting them.
3. HTTP: build the request matcher exhaustively, and add the wrong-credential unmatched case.
   WebSocket: seed the protocol fake from recorded frames.
4. Do not hard-code payload values the recording already contains — read them from the capture, so
   a re-capture does not force a test edit and two independent readers must agree on the bytes.
5. Keep any existing `*_ShouldAbort_WhenCancelled` test **verbatim**. It is the `test-determinism`
   tripwire for the substrate swap, not something the swap gets to redesign. Verify it under the
   30× repeat-run protocol.
6. Run the coverage gates locally before the PR — `scripts/check-coverage-floor.py` (a two-sided
   band: too *high* fails as a stale floor) and `scripts/check-patch-coverage.py`.

---

## 5. Where the substrate does not reach

Recordings are photographs. Nothing in this repository re-captures them, so a fixture ages into
asserting a wire format the vendor no longer sends. ADR-0041 accepts that explicitly: this closes the
*shared-misreading* gap, not the *drift* gap. Detecting drift needs contract tests against the live
APIs, which the ADR rejected for CI (real keys on a public repo, per-run billing, a third-party
outage on every PR's critical path) and left open as a possible opt-in, non-gating scheduled job.
