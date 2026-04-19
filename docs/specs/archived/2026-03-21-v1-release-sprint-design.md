# v1.0 Release Sprint — Design Spec

**Goal:** Polish and release Asterisk.Sdk v1.0.0 — CHANGELOG, security policy, documentation (READMEs), benchmarks, version bump, and tag.

**Current state:** v0.6.0-beta.1, 16 packages, 878+ unit tests, 513 functional tests, 0 trim warnings, API stable (no experimental markers).

---

## 1. CHANGELOG.md

Single v1.0.0 block summarizing the full SDK. No per-beta-version breakdown.

Structure:
```markdown
# Changelog

## v1.0.0 (YYYY-MM-DD)

First stable release. API frozen — semver from now on. No breaking changes in 1.x.

### Core SDK (9 packages)
- AMI client: 115 actions, 249 events, 17 responses, zero-copy pipeline
- AGI server: FastAGI with 54 commands
- ARI client: REST + WebSocket, 8 resource APIs
- Live API: real-time domain objects (channels, queues, agents, bridges)
- Activities: state machine call activities (Dial, Hold, Bridge, etc.)
- Sessions: call session correlation with pluggable persistence and routing
- Config: Asterisk .conf file parser
- Hosting: DI registration via AddAsterisk()

### Voice AI (7 packages)
- Audio processing pipeline
- AudioSocket protocol implementation
- STT abstractions + providers
- TTS abstractions + providers
- VoiceAi pipeline orchestration
- OpenAI Realtime bridge with function calling
- Testing utilities

### Key Properties
- .NET 10 Native AOT: zero reflection, 0 trim warnings
- System.IO.Pipelines for zero-copy TCP parsing
- System.Threading.Channels for async event pump
- Source generators for AOT-safe serialization (4 generators)
- Multi-server support via IAmiConnectionFactory + AsteriskServerPool
- Reconnection with exponential backoff
- Observability: System.Diagnostics.Metrics + IHealthCheck
```

## 2. SECURITY.md

Standard vulnerability disclosure policy:
- Report via GitHub private vulnerability reporting
- 48-hour acknowledgment SLA
- No public disclosure until fix available
- Credit to reporters

## 3. Per-Package READMEs

Each src/ package gets a focused README.md (already packed into NuGet via `PackageReadmeFile`). Structure per README:
- One-line description
- Installation (`dotnet add package Asterisk.Sdk.Xxx`)
- Quick example (5-15 lines of code)
- Key features (bullet list)
- Link to main README for full docs

Packages needing READMEs (~12 — exclude Asterisk.Sdk.Ami.SourceGenerators which is an analyzer, and Asterisk.Sdk which already has the main README):
1. Asterisk.Sdk.Ami
2. Asterisk.Sdk.Agi
3. Asterisk.Sdk.Ari
4. Asterisk.Sdk.Live
5. Asterisk.Sdk.Activities
6. Asterisk.Sdk.Sessions
7. Asterisk.Sdk.Config
8. Asterisk.Sdk.Hosting
9. Asterisk.Sdk.Audio
10. Asterisk.Sdk.VoiceAi
11. Asterisk.Sdk.VoiceAi.AudioSocket
12. Asterisk.Sdk.VoiceAi.OpenAiRealtime

Skip VoiceAi.Stt, VoiceAi.Tts, VoiceAi.Testing — these are thin provider wrappers, a one-liner in the parent VoiceAi README is sufficient.

## 4. Example READMEs

Each of the 13 console examples (excluding PbxAdmin which already has one) gets a README.md:
- What it demonstrates (1-2 sentences)
- Prerequisites (Asterisk version, Docker)
- How to run
- Key SDK features used

## 5. Benchmarks in README-technical.md

Run `dotnet run --project Tests/Asterisk.Sdk.Benchmarks/ -c Release` and capture results. Add a "Performance" section to README-technical.md with:
- Event deserialization throughput (ops/sec)
- Protocol parsing latency (ns/op)
- Memory allocation per operation (bytes)
- Comparison notes vs asterisk-java (reflection-based) and AsterNET

## 6. README.md Updates

- Remove "beta" references
- Update version badges to v1.0.0
- Update "Getting Started" with stable package version
- Ensure all links work

## 7. Version Bump + Tag

- `Directory.Build.props`: change `0.6.0-beta.1` → `1.0.0`
- `git tag v1.0.0`
- Do NOT push tag until user confirms
