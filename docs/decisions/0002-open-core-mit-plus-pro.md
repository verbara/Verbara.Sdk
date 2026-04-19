# ADR-0002: Open-core model — MIT SDK in public repo, commercial features in private Pro repo

- **Status:** Accepted
- **Date:** 2026-03-21 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (AOT-first), `docs/README-commercial.md`

## Context

The SDK covers the full Asterisk surface area: AMI (148/152 actions, 278 events), ARI (94/98 endpoints, 27 models, 46 events), AGI, Live tracking, Sessions, VoiceAi, Push. That's substantial infrastructure for any contact-center product — valuable as a shared foundation, too big to charge for directly.

At the same time, there is a separate tier of features that only make sense for paying enterprise contact centers: skill-based routing with proficiency scoring, predictive/progressive dialer campaigns, real-time analytics dashboards, event sourcing, multi-tenant isolation, AI-powered agent assist. Those features do not belong in a library people pick up to "connect to Asterisk from .NET."

## Decision

We will ship the SDK as an **open-core** product:

- This repo (`github.com/Harol-Reina/Asterisk.Sdk`, **public, MIT**): the full foundation — every package needed to build a call flow, a softphone backend, or a custom AGI. Zero hidden functionality; zero "upgrade to unlock" hooks.
- A separate private repo (`github.com/Harol-Reina/Asterisk.Sdk.Pro`, **not open-source**): commercial features that layer on top of the MIT SDK via its published interfaces. Consumers install `Asterisk.Sdk.Pro.*` packages alongside the MIT ones.

The MIT SDK must be **complete on its own**. A contributor or user must never hit a wall that says "this requires Pro." Pro adds capabilities; it never gates existing SDK features.

## Consequences

- **Positive:** Maximum contributor friendliness — a developer evaluating the SDK can ship production workloads without a commercial license. The MIT core builds goodwill; Pro serves the narrow slice of users with enterprise needs. Pro development is not held up by public discussion or backward-compatibility promises of the open-source repo.
- **Negative:** Two repos, two release cadences, two release notes. Consumers who go from MIT → Pro have to pick the right package versions from `Directory.Packages.props` coordination. Documentation must be careful not to leak Pro internals into the public repo (see below).
- **Trade-off:** We accept some friction in Pro's packaging to keep the MIT SDK pure.

## Public-repo discipline

Because this repo is public, **nothing in tracked files may reveal Pro internals**:

- No absolute paths under `/…/Asterisk.Sdk.Pro/…`.
- No Pro module names, class names, or internal metrics in tracked docs.
- Cross-repo analysis belongs in `docs/superpowers/` (local-only) — see the audit in the first commit of this PR for the three files that were leaking and had to be redacted.

See `.gitignore` and `CLAUDE.md` for the list of local-only folders.

## Alternatives considered

- **Dual-licensed repo (AGPL + commercial)** — rejected because AGPL would scare off the library's primary audience (telephony integrators embedding the SDK in closed-source products).
- **All-in-one commercial product** — rejected because the SDK benefits from external scrutiny (bugs, PRs, integration feedback), and Asterisk itself is GPL — a proprietary-only wrapper would feel out of step with the Asterisk ecosystem.
- **Single monorepo with optional `Pro/` directory gitignored** — rejected because `.gitignore` is a weak boundary (a careless `git add` leaks everything), and pack/publish boundaries between MIT and Pro become hard to enforce.
