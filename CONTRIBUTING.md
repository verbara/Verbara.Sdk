# Contributing to Asterisk.Sdk

Thank you for your interest in contributing to Asterisk.Sdk! This document provides guidelines and instructions for contributing.

## Getting Started

### Prerequisites

- [.NET 10.0.100+](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- Docker (for running Asterisk containers in integration/functional tests)
- An Asterisk 18-23 instance with AMI enabled (for manual testing)

### Setup

```bash
git clone https://github.com/Harol-Reina/Asterisk.Sdk.git
cd Asterisk.Sdk
dotnet build Asterisk.Sdk.slnx
dotnet test Asterisk.Sdk.slnx

# One-time: install the pre-commit hook that lints CLAUDE.md + .claude/
# on every commit using claudelint. Install claudelint first:
#   npm install -g claude-code-lint
./tools/install-hooks.sh
```

### Project Structure

```
src/                    # SDK packages (9 core + 7 VoiceAi + 1 SourceGenerators)
Examples/               # 16 example applications + PbxAdmin demo
Tests/                  # Unit, functional, integration, E2E tests
docker/                 # Docker Compose stacks for development and testing
docs/                   # Architecture, guides, plans, analysis
```

## Development Workflow

### Branch Naming

- `feat/description` — New features
- `fix/description` — Bug fixes
- `docs/description` — Documentation only
- `test/description` — Test additions or fixes
- `refactor/description` — Code refactoring

### Commit Messages

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(ami): add PJSIPShowRegistrationsAction
fix(live): fix race condition in ChannelManager state update
docs: update high-load tuning guide
test: add functional tests for queue events
refactor(agi): extract command parser into separate class
```

Scope is optional but recommended. Common scopes: `ami`, `agi`, `ari`, `live`, `sessions`, `push`, `hosting`, `voiceai`, `activities`, `config`, `audio`, `docker`.

### Code Conventions

- **AOT constraint:** No reflection at runtime. Use source generators, `[JsonSerializable]`, `[OptionsValidator]`.
- **Async-first:** All I/O uses `ValueTask`/`Task` with `CancellationToken` support.
- **Private fields:** `_camelCase` prefix.
- **File-scoped namespaces** (warning-level enforcement).
- **TreatWarningsAsErrors** is on globally — build must be 0 warnings.
- **Test naming:** `Method_ShouldExpected_WhenCondition`.
- **Test stack:** xUnit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0.
- **Central package management:** All NuGet versions in `Directory.Packages.props`.

### Build & Test

```bash
# Build entire solution
dotnet build Asterisk.Sdk.slnx

# Run all unit tests
dotnet test Asterisk.Sdk.slnx

# Run a specific test project
dotnet test Tests/Asterisk.Sdk.Ami.Tests/

# Run a single test by name
dotnet test Tests/Asterisk.Sdk.Ami.Tests/ --filter "FullyQualifiedName~AmiProtocolReaderTests"

# Run functional tests (requires Docker)
docker compose -f docker/docker-compose.test.yml up --build
```

## Pull Request Process

1. **Fork and branch** from `main`.
2. **Write tests first** — follow TDD. New features need unit tests. Bug fixes need a regression test.
3. **Build must pass** with 0 warnings (`TreatWarningsAsErrors` is on).
4. **All existing tests must pass.**
5. **Keep PRs focused** — one feature or fix per PR. Don't mix refactoring with features.
6. **Update CHANGELOG.md** if your change is user-facing.
7. **Submit PR** with a clear description of what and why.

### PR Review Criteria

- Does it build with 0 warnings?
- Are there tests? Do they pass?
- Does it follow the code conventions above?
- Is it AOT-safe (no runtime reflection)?
- Is the commit message conventional?

## Release Process (Maintainers)

Releases are driven by tag pushes matching `v*` (e.g. `v1.12.0`, `v1.12.1`). The `.github/workflows/publish.yml` workflow builds in Release, packs every shipping project, and pushes all `.nupkg` files to nuget.org via `dotnet nuget push --skip-duplicate`. No manual `dotnet nuget push` is needed.

### One-time setup — `NUGET_API_KEY` secret

The workflow reads `${{ secrets.NUGET_API_KEY }}` from GitHub repository secrets. If the secret is missing or expired, the push step fails with HTTP 403 on the first package. Set or rotate the key **without pasting it into chat, commits, or issue comments** — any secret that travels through a transcript should be rotated immediately after.

The safe flow is local-only — the value never appears in the command line or shell history:

```bash
# 1. Generate / rotate at https://www.nuget.org/account/apikeys
#    Scopes: "Push new packages and package versions"
#    Glob Pattern: Asterisk.Sdk.*
#    Expiration: 365 days (max)

# 2. Pipe from clipboard or password manager directly to gh secret set:
pbpaste | gh secret set NUGET_API_KEY --repo Harol-Reina/Asterisk.Sdk              # macOS
xclip -selection clipboard -o | gh secret set NUGET_API_KEY --repo Harol-Reina/Asterisk.Sdk   # Linux X11
wl-paste | gh secret set NUGET_API_KEY --repo Harol-Reina/Asterisk.Sdk              # Linux Wayland
pass show nuget/api-key | gh secret set NUGET_API_KEY --repo Harol-Reina/Asterisk.Sdk         # pass(1)

# 3. Verify the secret is registered (value stays encrypted):
gh secret list --repo Harol-Reina/Asterisk.Sdk
```

### Cutting a release

```bash
# 1. Ensure main is clean and CI is green on the latest commit.
git checkout main && git pull && git fetch --tags

# 2. Bump <PackageVersion> in Directory.Build.props + prepend a CHANGELOG.md section.
#    Commit as: chore: bump version X.Y.(Z-1) → X.Y.Z

# 3. Tag and push — this fires publish.yml.
git tag -a vX.Y.Z -m "vX.Y.Z — <headline>"
git push origin vX.Y.Z

# 4. Watch the workflow until green:
gh run watch --exit-status

# 5. Publish the GitHub Release from the CHANGELOG excerpt:
awk "/^## \\[X.Y.Z\\]/,/^## \\[/" CHANGELOG.md | sed "\$d" > /tmp/notes.md
gh release create vX.Y.Z --title "vX.Y.Z — <headline>" --notes-file /tmp/notes.md
```

If `publish.yml` fails partway through, the remaining packages were skipped; `--skip-duplicate` makes the workflow idempotent. To retry, fix the root cause and re-push the tag:

```bash
git push origin :refs/tags/vX.Y.Z   # delete remote tag
git push origin vX.Y.Z              # re-push → re-fires publish.yml
```

## Reporting Issues

- Use [GitHub Issues](https://github.com/Harol-Reina/Asterisk.Sdk/issues) for bugs and feature requests.
- For security vulnerabilities, see [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
