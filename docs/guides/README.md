# Guides

Practical how-to guides for working with Verbara Sdk.

| Guide | Description |
|-------|-------------|
| [ari-connection-state-and-accept-loop-migration.md](ari-connection-state-and-accept-loop-migration.md) | Moving to the ARI connect attempt that leaves a terminal state and the outbound listener that survives an accept failure -- what `AriClient.State` reports now, which workarounds can be deleted, and the Error line a persistent accept failure produces. |
| [asterisk-version-compatibility.md](asterisk-version-compatibility.md) | AMI event coverage matrix across Asterisk 18-23, listing typed classes and fallback behavior per version. |
| [asterisk-version-matrix.md](asterisk-version-matrix.md) | Supported Asterisk versions (22 LTS primary, 23 Standard secondary), Docker test infrastructure, and known-divergent behavior. |
| [audiosocket-wire-format-migration.md](audiosocket-wire-format-migration.md) | Moving to the corrected AudioSocket frame header -- the three-byte shape Asterisk actually sends, the enum values that changed, and why a rebuild is what picks them up. |
| [externalmedia-channel-id-migration.md](externalmedia-channel-id-migration.md) | Moving to the `CreateExternalMediaAsync` signature that takes a `channelId` -- why the break was bought, which callers have to be edited rather than rebuilt, and how to make an AudioSocket stream findable by the channel id the create returned. |
| [high-load-tuning.md](high-load-tuning.md) | Configuration guidance for high-load scenarios (1K-100K+ agents), including EventPump sizing and buffer capacity recommendations. |
| [log-analysis-prompt.md](log-analysis-prompt.md) | Ready-to-use LLM prompt for analyzing Verbara.Sdk structured log files with extract, classify, and diagnose phases. |
| [log-analysis-reference.md](log-analysis-reference.md) | Tag catalog for SDK structured logs -- lists every `[TAG]`, its domain, source classes, and emitted events. |
| [manual-asterisk-realtime-setup.md](manual-asterisk-realtime-setup.md) | How to run Asterisk in Realtime mode with PostgreSQL so external tooling can manage PJSIP endpoints, queues, and voicemail via SQL. |
| [provider-recording-protocol.md](provider-recording-protocol.md) | How a real speech-provider response is captured, redacted, documented with a provenance sidecar, and committed -- plus the per-provider terms-of-service findings, the binary size cap, and the redaction guard. |
| [provider-wire-conformance.md](provider-wire-conformance.md) | What is actually known about each AI-provider surface the SDK talks to -- route status, frame status, where the vendor validates the credential, the evidence class behind each claim, and its own date. Includes the surfaces nobody has characterised. |
| [provider-test-substrate.md](provider-test-substrate.md) | Which fake server a provider test suite runs against (WireMock for HTTP, `WebSocketTestServer` for duplex), why the split is by transport, the origin seam for providers that compose their own URL, and the checklist for adding a suite. |
| [session-store-backends.md](session-store-backends.md) | Choosing and configuring a session store backend (InMemory, Redis, or Postgres) for `Verbara.Sdk.Sessions`. |
| [troubleshooting.md](troubleshooting.md) | Common issues and fixes for AMI connections, ARI, AGI, and other SDK components. |
