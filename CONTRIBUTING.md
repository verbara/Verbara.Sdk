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
```

### Project Structure

```
src/                    # SDK packages (9 core + 7 VoiceAi + 1 SourceGenerators)
Examples/               # 14 example applications + PbxAdmin demo
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

## Reporting Issues

- Use [GitHub Issues](https://github.com/Harol-Reina/Asterisk.Sdk/issues) for bugs and feature requests.
- For security vulnerabilities, see [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
