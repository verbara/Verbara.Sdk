# ADR-0055: A release proves itself, and a release that never happens is not silent

- **Status:** Accepted
- **Date:** 2026-08-26
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0051 (where a check runs decides what it can gate — the same reasoning puts these
  two checks on `push: main` rather than on `pull_request`), ADR-0042 (guard classes: both checks
  added here are ENFORCING, and the cadence budget is the "cheap enough to run every week" half of
  that split)

## Context

`publish.yml` was 50 lines when `v2.4.0` shipped. It restored, packed, pushed everything in
`./artifacts` to nuget.org and created a GitHub Release. Every gate in it was a statement about
**shape** — the ref looks like a tag, the files look like packages — and none was a statement about
**evidence**. Three consequences followed from that, and all three are things a green run could hide:

- `dotnet nuget push --skip-duplicate` turns "this version is already on the feed" into a success.
  A run could go entirely green having uploaded nothing.
- Asserting `<PackageVersion>` proves nothing about what reaches the feed. NuGet normalises versions
  on the way into a filename: `2.5.0.0` packs as `…2.5.0.nupkg` and `2.5.0+ci` drops the metadata.
  The only honest assertion is on the packed `.nupkg` filenames.
- Nothing checked that the tagged commit had ever been built. A tag cut off a side branch would have
  published.

The 2.5.0 cut is what surfaced all of this, and the timeline of that cycle is the second half of the
problem:

| | |
|---|---|
| 2026-07-26 | `v2.4.0` tagged |
| 2026-08-02 | first change lands that a consumer could see (`90aa8739`) |
| 2026-08-22 | `<PackageVersion>` bumped to 2.5.0 (`fe666f66`) |
| 2026-08-25 | `v2.5.0` tagged |

88 commits landed in that window, 19 of them touching `src/`. Nothing was broken and nothing was
red — because nothing was *looking*. `publish.yml` triggers on `push: tags: v*` and nothing else,
and `ci.yml` runs on `pull_request` + `merge_group` and deliberately drops the post-merge push run.
Between them sits a blind spot exactly the shape of a release: once a PR lands, nothing examines the
repo again until somebody pushes a tag. "A release is overdue" had no signal anywhere.

The cost of that silence did not show up as a broken release. It showed up as **one CHANGELOG
section holding a month of entries**, which measured 117,711 characters against GitHub's
125,000-character release-body cap — 94% of a limit that, once crossed, fails the release outright.

A third thing went stale in the same quiet way. `PackageValidationBaselineVersion` — the only
mechanism in this repo that catches an unintended binary break — sat at `2.1.0` while 2.2.x, 2.3.x
and 2.4.0 shipped. Package validation kept running and kept passing the whole time; comparing
against an older package is still a valid comparison, it just stops covering what shipped in
between. It was moved to 2.4.0 by hand during the 2.5.0 cut, where it went stale again the moment
`v2.5.0` was tagged.

## Decision

### D1 — A release publishes only against evidence it produced itself

`publish.yml` (now 348 lines, #221/#222/#223) refuses to push unless, in order: it has resolved
whether this is a real publish or a rehearsal; the tagged commit is an ancestor of `origin/main` and
its completed check runs are all green; the packed `.nupkg` **filenames** carry the expected version
and their count matches the total packed; and the release notes have been extracted and measured.
The API key is passed by `env:` rather than interpolated into the command line, and the feed is
polled afterwards to confirm the release is actually resolvable.

Two details worth writing down because neither is guessable:

- **Job-level `permissions:` is exhaustive.** Any scope not listed becomes `none`, so adding a gate
  that calls the API means adding its scope. The provenance gate needed `checks: read`, and its
  absence would have failed the gate rather than degraded it.
- **A skipped step reports `success`.** That is why the release-notes step is *not* gated behind the
  publish flag: gated, the rehearsal skipped the only step that can hard-fail, and the dry run
  reported a green pass over the newest and least-exercised code in the file (#223).

### D2 — The tag ruleset is the authorisation boundary; an `if:` in the workflow is not

On a tag push, GitHub runs the workflow file **as it exists in the tagged commit**. Every condition
inside `publish.yml` therefore comes from the same object it is meant to police. `if:` conditions in
that file are correctness checks — they stop mistakes — and must never be described as
authorisation. The only real boundary is a **repository tag ruleset on `v*`** restricting who may
create the tag.

This was proven twice during the design, in the same direction. The first version of the identity
gate was `if: github.ref_type == 'tag'`, which reads like "only publishes on a tag push" and is not:
`workflow_dispatch` lets a tag be selected from the ref dropdown, satisfying the exact condition
that was supposed to close that door. It is now
`github.event_name == 'push' && github.ref_type == 'tag'` — a better correctness check, and still
not a boundary.

**The ruleset does not exist yet. 2.5.0 shipped without it.** It is recorded here as an accepted
decision with an outstanding action rather than left implied, because the workflow reads as though
it is protected and it is not.

### D3 — The provenance gate peels `$GITHUB_SHA` to a commit

`repos/.../commits/<sha>/check-runs` cannot resolve a sha that is not a commit, and on a tag push
`$GITHUB_SHA` is only reliably a commit when the tag is **lightweight**; for an annotated tag it can
be the tag object's sha. This repo's history contains both — `v2.2.1` and `v2.3.0` are annotated,
`v2.3.1` through `v2.5.0` are lightweight — so a maintainer creating the next tag with `git tag -a`
out of habit is an ordinary event, not an unlikely one.

The gate resolves `git rev-parse "${GITHUB_SHA}^{commit}"` before it asks anything else. That is a
no-op on a lightweight tag and peels an annotated one, which makes the release indifferent to how
the tag was created. The alternative — documenting "always use a lightweight tag" — puts a
correctness requirement in a place nobody reads at the moment they need it.

### D4 — The release-body cap is per section, and it is measured before the push

GitHub's 125,000-character limit applies to the body of **one release**, and the notes are extracted
between `## [<version>]` and the next `## [`. So it constrains a single CHANGELOG section, not the
file. There is nothing to split: `[Unreleased]` starts empty after every cut, and the next version's
section starts at zero. What nearly overflowed was a month accumulated into one section, which makes
this a **cadence** problem and not a formatting one.

The gate measures the extracted body before the push and hard-fails over the limit, warning past
90%. Failing pre-push is the whole point: at that moment nothing has been published, so the run
aborts with the feed untouched.

### D5 — A release that does not happen produces a signal, calibrated on cadence

`scripts/ci/check-publish-liveness.sh` runs on `push: main` and weekly, and reports two states:

- **Staged, never cut** — `<PackageVersion>` names a version whose tag does not exist. Fails
  immediately, with no grace period. The fix is one command and on the intended path the window is
  minutes; one red run on a release already being cut costs a maintainer 20 minutes of annoyance,
  and the silent version of this state cost three days.
- **Shipped, then drifted** — the tag exists and consumer-visible content has landed on top of it.
  **Reported always, failed only past a budget**: the oldest unreleased shipped change is more than
  14 days old, or the `[Unreleased]` section has passed 90% of the release-body cap.

That budget is the load-bearing part, and it is where this diverges from Sdk.Pro's script of the
same name rather than copying it. Pro's `release.yml` runs on push to main and creates the tag
itself, so unreleased content exists for minutes and any accumulation at all is worth failing on.
This repo cuts tags by hand roughly monthly, so unreleased content is the **normal** state — a check
that failed on its mere existence would be red most of every month, and a signal that is red most of
the time protects nothing. Replayed against the cycle above, the check is quiet on 2026-08-02
(5 days) and fails on 2026-08-15 (18 days), ten days before the tag actually went up.

The same reasoning rules out the obvious alternative of keying on a non-empty `[Unreleased]`
section: that counts recorded work rather than shippable work, so it fires on docs-only and
test-only merges. Its **size** is used instead, which is a statement about the release body rather
than about whether work happened.

What counts as consumer-visible is filtered for the same reason. `src/**` always; but
`Directory.Packages.props` only when a bumped package is actually referenced non-privately from a
`src/` project — central package management keeps xunit and NSubstitute in the same file as shipped
dependencies, and without that filter every Dependabot test bump would start the clock. And
`Directory.Build.props` only when something other than `PackageValidationBaselineVersion` moved —
without which the ratchet in D6 would start the clock the moment it was satisfied.

**This is a notification, not a merge gate.** It runs after the merge, on `main`, because gating a
PR on "a release is overdue" would stop unrelated work to punish a maintenance task. GitHub emails
the actor when a workflow fails on a default-branch push, and that is the intended delivery.

### D6 — The ApiCompat baseline ratchets to the newest published tag

`scripts/ci/check-apicompat-baseline.sh` fails when `PackageValidationBaselineVersion` is not the
highest stable `v*` tag, and also when the baseline it names is not resolvable on nuget.org — a tag
can exist without a successful publish, and package validation cannot download a baseline that is
not there, which surfaces as a restore error naming nothing relevant.

The check reads **git tags**, not `<PackageVersion>`, and that ordering is deliberate: the baseline
moves *after* a release is tagged, never as part of preparing one. Bumping it in the release PR
would point ApiCompat at a package that does not exist yet.

The baseline is moved to `2.5.0` here. All 29 packages pack clean against it.

### D7 — Both guards are themselves tested, on the PR that edits them

`scripts/tests/test_release_hygiene.sh` covers 39 cases across the two scripts — every state in both
directions, both consumer-visibility filters, the budget in both directions, and the two
"cannot tell" exits — and runs in CI's always-run `Coverage Script Tests` job, next to the docs-only
classifier's harness and for the same reason.

The reason is specific rather than a general preference for tests. Both guards fire on states that
only occur when nobody is looking. A broken guard therefore fails in exactly the way the bug it
guards against fails: silently, and green. Nothing else in the repo would notice. The tests also
have to live in a PR-blocking job rather than in `release-hygiene.yml` itself, which runs post-merge
and so cannot block the PR that breaks them.

## Consequences

- The release path is now roughly seven times its previous size, and most of that is refusal
  conditions and the reasoning behind them. A release takes longer to fail and fails earlier —
  before the push, where failure is free.
- Two workflow failures now arrive by email rather than blocking anything. If they are ignored they
  degrade to noise, and the 14-day budget is the only thing keeping the volume proportionate. It is
  an `env`-overridable knob (`STALE_DAYS`) precisely because it is a judgement, not a fact.
- The 14-day budget will make `main` red roughly mid-cycle at the current cadence. That is the
  intended reading — "you are halfway to a month, cut a release" — and not an incident.
- Cutting releases more often is now the cheapest way to keep both this and the release-body cap
  quiet. That is the behaviour the near-miss argues for.

## What this does not claim

- **It does not make the release authorised.** D2's ruleset is still absent; until it exists,
  anyone who can push a tag can publish, and no amount of workflow content changes that.
- **The feed poll covers one package, not 29.** `Verbara.Sdk` indexed on attempt 8 (~4 minutes) at
  the 2.5.0 cut; the other 28 took 2–5 minutes more, `verbara.sdk.push.aspnetcore` last. The gate
  confirms the release is reaching the feed, not that all 29 are visible.
- **Liveness silence is not a claim that the repo is tidy.** It says no consumer-visible path has
  moved, or that the drift is inside budget. It says nothing about whether the CHANGELOG describes
  what shipped.
- **Package validation catches binary breaks, not behavioural ones.** 2.5.0 carried 11 `BREAKING`
  entries; most of them are meaning changes that no API diff can see.

## Alternatives considered

- **Mirror the test suites into `publish.yml`.** Rejected: it would drag a Docker Hub pull into the
  release path, and the merge-queue run already tested that exact tree. D1's provenance gate asserts
  that evidence instead of re-manufacturing it.
- **Copy Sdk.Pro's liveness script.** Rejected — see D5. The scripts answer the same question for
  two release models, and Pro's calibration is wrong here in the direction that destroys the signal.
- **`environment: nuget-release` with required reviewers.** Deferred: it is only meaningful once the
  repo-level `NUGET_API_KEY` is deleted, and for a single maintainer a wait timer is the useful half
  of it. Recorded so the omission is a decision rather than an oversight.
- **Split the CHANGELOG.** Rejected once the cap was measured correctly — see D4. It is per section,
  so there is nothing to split.
