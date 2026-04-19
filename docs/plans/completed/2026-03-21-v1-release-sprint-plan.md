# v1.0 Release Sprint — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Polish and release Asterisk.Sdk v1.0.0 with CHANGELOG, security policy, package/example READMEs, benchmarks, and version bump.

**Architecture:** Documentation-focused sprint. All code is already stable (0.6.0-beta.1). Tasks create markdown files, run benchmarks, update version metadata, and tag the release.

**Tech Stack:** Markdown, BenchmarkDotNet, .NET 10, git

**Spec:** `docs/superpowers/specs/2026-03-21-v1-release-sprint-design.md`

---

## File Structure

```
Root:
  CHANGELOG.md                          ← CREATE
  SECURITY.md                           ← CREATE
  README.md                             ← MODIFY (remove beta refs)
  README-technical.md                   ← MODIFY (add benchmarks)
  Directory.Build.props                 ← MODIFY (version bump)

src/ (per-package READMEs):
  Asterisk.Sdk.Ami/README.md            ← CREATE
  Asterisk.Sdk.Agi/README.md            ← CREATE
  Asterisk.Sdk.Ari/README.md            ← CREATE
  Asterisk.Sdk.Live/README.md           ← CREATE
  Asterisk.Sdk.Activities/README.md     ← CREATE
  Asterisk.Sdk.Sessions/README.md       ← CREATE
  Asterisk.Sdk.Config/README.md         ← CREATE
  Asterisk.Sdk.Hosting/README.md        ← CREATE
  Asterisk.Sdk.Audio/README.md          ← CREATE
  Asterisk.Sdk.VoiceAi/README.md        ← CREATE
  Asterisk.Sdk.VoiceAi.AudioSocket/README.md  ← CREATE
  Asterisk.Sdk.VoiceAi.OpenAiRealtime/README.md ← CREATE

Examples/ (example READMEs):
  BasicAmiExample/README.md             ← CREATE
  AmiAdvancedExample/README.md          ← CREATE
  FastAgiServerExample/README.md        ← CREATE
  AgiIvrExample/README.md               ← CREATE
  LiveApiExample/README.md              ← CREATE
  AriChannelControlExample/README.md    ← CREATE
  AriStasisExample/README.md            ← CREATE
  MultiServerExample/README.md          ← CREATE
  SessionExample/README.md              ← CREATE
  SessionExtensionsExample/README.md    ← CREATE
  PbxActivitiesExample/README.md        ← CREATE
  VoiceAiExample/README.md              ← CREATE
  OpenAiRealtimeExample/README.md       ← CREATE
```

---

### Task 1: CHANGELOG.md + SECURITY.md

**Files:**
- Create: `CHANGELOG.md`
- Create: `SECURITY.md`

- [ ] **Step 1: Create CHANGELOG.md**

Single v1.0.0 block. Structure:
- Header with API freeze declaration
- Core SDK section (9 packages with key features)
- Voice AI section (7 packages)
- Key Properties (AOT, pipelines, source generators, metrics)
- Infrastructure (513 functional tests, Toxiproxy, multi-server cluster)

Read the existing README.md and README-technical.md for feature descriptions to pull from.

- [ ] **Step 2: Create SECURITY.md**

Standard GitHub vulnerability disclosure policy:
- Report via GitHub private vulnerability reporting (Security tab → Report a vulnerability)
- 48-hour acknowledgment SLA
- Coordinated disclosure (no public disclosure until fix available)
- Credit to reporters in CHANGELOG

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md SECURITY.md
git commit -m "docs: add CHANGELOG v1.0.0 and SECURITY.md"
```

---

### Task 2: Per-package READMEs (12 files)

**Files:**
- Create: 12 README.md files in src/ subdirectories

Each README follows this template:
```markdown
# Asterisk.Sdk.Xxx

One-line description.

## Installation

\`\`\`bash
dotnet add package Asterisk.Sdk.Xxx
\`\`\`

## Quick Start

\`\`\`csharp
// 5-15 lines showing primary usage
\`\`\`

## Features

- Feature 1
- Feature 2
- Feature 3

## Documentation

See the [main README](../../README.md) for full documentation.
```

For each package, read its .csproj (for Description) and key source files to understand the primary use case. Also check if there's already a README.md in the package directory.

The 12 packages:
1. Asterisk.Sdk.Ami — AMI client (115 actions, 249 events, reconnection, heartbeat)
2. Asterisk.Sdk.Agi — FastAGI server (54 commands, mapping strategies)
3. Asterisk.Sdk.Ari — ARI REST + WebSocket client (8 resource APIs)
4. Asterisk.Sdk.Live — Real-time domain objects (channels, queues, agents)
5. Asterisk.Sdk.Activities — Call activity state machines (Dial, Hold, Bridge)
6. Asterisk.Sdk.Sessions — Call session correlation + routing + persistence
7. Asterisk.Sdk.Config — Asterisk .conf file parser
8. Asterisk.Sdk.Hosting — DI registration (AddAsterisk)
9. Asterisk.Sdk.Audio — Audio processing (resampling, format conversion)
10. Asterisk.Sdk.VoiceAi — Voice AI pipeline (STT + TTS + handler orchestration)
11. Asterisk.Sdk.VoiceAi.AudioSocket — AudioSocket protocol for Asterisk
12. Asterisk.Sdk.VoiceAi.OpenAiRealtime — OpenAI Realtime API bridge

- [ ] **Step 1: Create all 12 READMEs**

Read each package's source to write accurate quick-start examples. Use the actual API (constructors, methods) — not placeholder code.

- [ ] **Step 2: Verify no build warnings**

READMEs should be referenced in .csproj via `<PackageReadmeFile>README.md</PackageReadmeFile>`. Check if this property already exists. If not, add it with:
```xml
<None Include="README.md" Pack="true" PackagePath="\" />
```

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/*/README.md
git commit -m "docs: add per-package README.md for all 12 SDK packages"
```

---

### Task 3: Example READMEs (13 files)

**Files:**
- Create: 13 README.md files in Examples/ subdirectories

Each README follows this template:
```markdown
# ExampleName

What this example demonstrates (1-2 sentences).

## Prerequisites

- .NET 10 SDK
- Asterisk PBX (Docker: `docker compose -f docker/functional/docker-compose.functional.yml up -d`)

## Run

\`\`\`bash
dotnet run --project Examples/ExampleName/
\`\`\`

## What It Shows

- Feature 1 demonstrated
- Feature 2 demonstrated

## Key SDK Features Used

- `AmiConnection` for AMI protocol
- `Subscribe()` for event handling
```

For each example, read its Program.cs to understand what it does. Write accurate descriptions.

The 13 examples:
1. BasicAmiExample — Connect to AMI, send PingAction
2. AmiAdvancedExample — Subscribe events, originate calls, command execution
3. FastAgiServerExample — Start FastAGI server, handle incoming scripts
4. AgiIvrExample — IVR with DTMF navigation via AGI
5. LiveApiExample — Real-time channel/queue monitoring
6. AriChannelControlExample — ARI channel origination and control
7. AriStasisExample — ARI Stasis application handling
8. MultiServerExample — Connect to multiple Asterisk servers
9. SessionExample — Call session tracking
10. SessionExtensionsExample — Session extensions and custom data
11. PbxActivitiesExample — Dial, Hold, Bridge activities
12. VoiceAiExample — Voice AI pipeline with STT/TTS
13. OpenAiRealtimeExample — OpenAI Realtime integration

- [ ] **Step 1: Create all 13 READMEs**

Read each example's Program.cs to write accurate descriptions.

- [ ] **Step 2: Commit**

```bash
git add Examples/*/README.md
git commit -m "docs: add README.md for all 13 example applications"
```

---

### Task 4: Benchmarks in README-technical.md

**Files:**
- Modify: `README-technical.md`

- [ ] **Step 1: Run benchmarks**

Run: `dotnet run --project Tests/Asterisk.Sdk.Benchmarks/ -c Release -- --filter "*"`

Capture results for:
- EventDeserializerBenchmark (NewChannel, VarSet, QueueParams)
- Any other benchmarks in the project

- [ ] **Step 2: Add Performance section to README-technical.md**

Add a "## Performance" section with:
- BenchmarkDotNet results table
- Key metrics: ops/sec, ns/op, bytes allocated
- Comparison notes (vs reflection-based alternatives)
- Note: "Benchmarks run on [machine specs], .NET 10, Release build"

- [ ] **Step 3: Commit**

```bash
git add README-technical.md
git commit -m "docs: add benchmark results to README-technical.md"
```

---

### Task 5: README.md updates

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README**

- Replace any "beta" references with stable v1.0 language
- Update version in installation examples: `0.6.0-beta.1` → `1.0.0`
- Add/update NuGet badge if present
- Verify all links work
- Keep the existing balanced audience tone

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: update README.md for v1.0.0 stable release"
```

---

### Task 6: Version bump

**Files:**
- Modify: `Directory.Build.props` (version property)

- [ ] **Step 1: Find and update version**

Search Directory.Build.props for the version property (likely `<Version>0.6.0-beta.1</Version>` or `<VersionPrefix>`). Change to `1.0.0`.

- [ ] **Step 2: Verify build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 warnings, version 1.0.0 in output assemblies.

- [ ] **Step 3: Run all tests**

Run: `dotnet test Asterisk.Sdk.slnx --filter "FullyQualifiedName!~IntegrationTests&FullyQualifiedName!~Spike&FullyQualifiedName!~Benchmarks"`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 1.0.0"
```

- [ ] **Step 5: Tag (DO NOT PUSH — wait for user confirmation)**

```bash
git tag v1.0.0
```
