#!/usr/bin/env bash
# check-publish-liveness.sh — is a release waiting on something? It says so in notices; it fails only when it cannot tell.
#
# WHY THIS EXISTS. `publish.yml` triggers on `push: tags: v*` and nothing else; `ci.yml` runs on
# `pull_request` + `merge_group` and deliberately drops the post-merge push run. Between them is a
# blind spot exactly the shape of a release: after a PR lands, nothing looks at the repo again until
# somebody pushes a tag, so "a release is overdue" has no signal anywhere. Every run stays green,
# because no run is about the release.
#
# THE 2.4.0 → 2.5.0 CYCLE, which is what this is calibrated against:
#   2026-07-26  v2.4.0 tagged
#   2026-08-02  first shipped change lands on top of it (90aa8739) — content is now unreleasable
#   2026-08-22  <PackageVersion> bumped to 2.5.0 (fe666f66) — a release is now staged
#   2026-08-25  v2.5.0 tagged. 88 commits, 19 of them touching src/
# Nothing was broken and nothing was red. The cost showed up somewhere else: a month of entries
# accumulated into one CHANGELOG section, which measured 117,711 characters against GitHub's
# 125,000-character release-body cap — 94% of a limit that, at the time, blocked the release itself once crossed.
#
# ADAPTED FROM Sdk.Pro's script of the same name, NOT copied, and the calibration is the reason.
# Pro's `release.yml` runs on push to main and creates the tag itself, so unreleased content exists
# for minutes and any accumulation at all is worth failing on. The SDK cuts tags by hand roughly
# monthly, so unreleased content is the NORMAL state — a check that fails on its mere existence
# would be red most of every month, and a signal that is red most of the time protects nothing.
# What is abnormal is unreleased content that has been waiting too long, or a CHANGELOG section
# heading for the cap. Those are what this reports on, each as a GitHub Actions notice annotation
# on the run's summary page. See ADR-0055.
#
# THE STATES
#
#   A. STAGED, NEVER CUT — <PackageVersion> names a version whose tag does not exist. A notice
#      ("Release staged": the version, when and where it was staged, how many commits ago, and that
#      the tag does not exist yet), the report and the one-line fix, then exit 0. It failed until the
#      owner ruled on 2026-11-26 that it should not: measured over ten release cuts it fired on every
#      one, always on the intended path between the release PR's merge and the tag push, and never
#      caught a forgotten tag — while a closing campaign between the bump and the tag kept it red for
#      40 hours (2.10.0) and could keep it red for weeks. A signal that is red on every release
#      protects nothing; the notice keeps the state visible without that.
#      While state A holds, state B is NOT evaluated: the script stops after state A, because state
#      B diffs from the staged version's tag and that tag does not exist yet.
#
#   B. SHIPPED, THEN DRIFTED — the tag exists and consumer-visible content has landed on top of it.
#      Reported always, with two triggers that are deliberately NOT the same kind of signal:
#        * TIME — the oldest unreleased consumer-visible commit is more than STALE_DAYS old (default
#          14 — half the cycle above, while the trend is still correctable). This ASKS, it does not
#          fail: a GitHub Actions notice annotation, "cut a release?", plus the drift report, and
#          exit 0. The owner decides when to cut; STALE_DAYS only sets when the question appears.
#          It failed until the owner ruled on 2026-11-26 that a date is an estimate, not a deadline.
#        * SIZE — the `[Unreleased]` CHANGELOG section is past 90% of the release-body cap, the same
#          threshold publish.yml warns at, so the two agree rather than one surprising the other.
#          This is a notice too ("Release notes size"), plus the report, and exit 0: past the cap
#          publish.yml publishes a shortened release body (the BREAKING entries, the headings and a
#          link to CHANGELOG.md) instead of refusing, so the cap no longer blocks a release. It
#          failed until the same 2026-11-26 ruling.
#      When both fire it prints both notices and exits 0. Within both it prints the drift and exits
#      0. That number being visible every week is the point; being red every week would not be.
#
# WHAT COUNTS AS CONSUMER-VISIBLE — a check that fires when nothing can ship is a check reviewers
# learn to ignore, so the filtering is deliberate:
#
#   src/**                     always. This is what packs.
#   Directory.Packages.props   only when a bumped package is actually referenced from a src/
#                              project non-privately. Central package management keeps test-only
#                              versions (xunit, NSubstitute, FluentAssertions) in the same file as
#                              shipped ones, and a test-only bump changes nothing a consumer
#                              restores. Without this, every Dependabot test bump would start the
#                              clock.
#   Directory.Build.props      unless the only lines that moved are build-only knobs — today
#                              PackageValidationBaselineVersion, which is ApiCompat input and is
#                              never packed. Without this, the baseline ratchet added alongside
#                              this script would start the clock the moment it was satisfied.
#
# NOT here, and correctly ignored: Tests/, Examples/, docs/, scripts/, .github/.
#
# Keying state B on a NON-EMPTY `[Unreleased]` section was rejected — it counts recorded work rather
# than shippable work, so it fires on docs-only merges. Its SIZE is used instead, which is a
# statement about the release body and not about whether work happened.
#
# VERSION-INVARIANT BY CONSTRUCTION: no version literal appears below; the tag is derived from
# Directory.Build.props at run time, so no future release edits this file.
#
# Exit 0 = the state was read; whatever it found is reported as notices and the report, never as a
#          failure (no state above exits non-zero).
# Exit 2 = cannot tell (no <PackageVersion> to derive the tag from) — the run goes red, because an
#          unreadable state is not a fine one.
# No path exits 1 on purpose. A command that fails unexpectedly still ends the run under `set -e`
# with its own status, which may be 1: that is a broken check, not a finding.
set -euo pipefail

STALE_DAYS="${STALE_DAYS:-14}"
BODY_LIMIT="${BODY_LIMIT:-125000}" # GitHub's release-body cap, per release section
BODY_WARN=$((BODY_LIMIT * 90 / 100))

# ---------------------------------------------------------------------------------------------
# Is this package referenced from a shipped project, in a way a consumer would see? Matches
# `PackageReference` lines carrying exactly Include="<name>" — the closing quote makes it exact, so
# Logging does not match Logging.Abstractions — and ignores attribute order, so reordering
# Include/PrivateAssets in a csproj cannot silently turn this off.
# ---------------------------------------------------------------------------------------------
reaches_consumers() {
  grep -rh --include='*.csproj' 'PackageReference' src/ 2>/dev/null \
    | grep -F "Include=\"$1\"" \
    | grep -v 'PrivateAssets="all"' >/dev/null
}

# consumer_visible_changes <base> [head] — one path per line, empty if nothing ships.
consumer_visible_changes() {
  local base="$1" head="${2:-HEAD}" range pkg
  range="$base..$head"
  git diff --name-only "$range" -- src/

  if ! git diff --quiet "$range" -- Directory.Packages.props 2>/dev/null; then
    while read -r pkg; do
      [ -n "$pkg" ] || continue
      if reaches_consumers "$pkg"; then
        echo "Directory.Packages.props (ships: $pkg)"
      fi
    done < <(git diff -U0 "$range" -- Directory.Packages.props \
               | grep -E '^\+[^+]' \
               | sed -n 's/.*Include="\([^"]*\)".*/\1/p' \
               | sort -u)
  fi

  if ! git diff --quiet "$range" -- Directory.Build.props 2>/dev/null; then
    if git diff -U0 "$range" -- Directory.Build.props \
         | grep -E '^[+-][^+-]' \
         | grep -vE 'PackageValidationBaselineVersion' >/dev/null; then
      echo "Directory.Build.props"
    fi
  fi
}

# The first commit after <tag> whose OWN diff ships something. Walks only commits that touched the
# candidate paths, then applies the same filter per commit — so a test-only dependency bump does not
# get to start the clock just because it sits earliest in the range.
first_shipping_commit() {
  local tag="$1" c
  while read -r c; do
    [ -n "$c" ] || continue
    if [ -n "$(consumer_visible_changes "$c^" "$c")" ]; then
      echo "$c"
      return 0
    fi
  done < <(git rev-list --reverse "$tag..HEAD" -- src/ Directory.Packages.props Directory.Build.props)
  return 0
}

changelog_section_bytes() {
  awk -v h="## [$1]" '
    index($0, h) == 1 { grab = 1; next }
    grab && index($0, "## [") == 1 { exit }
    grab { print }
  ' CHANGELOG.md | wc -c | tr -d ' '
}

version="$(sed -n '\:<PackageVersion>.*</PackageVersion>:{s:.*<PackageVersion>\(.*\)</PackageVersion>.*:\1:p;q;}' Directory.Build.props)"
if [ -z "$version" ]; then
  echo "publish-liveness: no <PackageVersion> in Directory.Build.props — cannot determine the tag" >&2
  exit 2
fi
tag="v$version"

# State A is a green notice, so a checkout that cannot see tags must not read as "staged": a failed
# tag fetch, a shallow clone or no repository at all would otherwise pass silently. Cannot tell.
if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "publish-liveness: not inside a git repository — cannot tell" >&2
  exit 2
fi
if [ "$(git rev-parse --is-shallow-repository)" = "true" ]; then
  echo "publish-liveness: shallow clone — tags and history are incomplete, cannot tell (checkout needs fetch-depth: 0)" >&2
  exit 2
fi
if [ -z "$(git tag -l 'v*')" ]; then
  echo "publish-liveness: no v* tags in this checkout — the tag fetch likely failed, cannot tell" >&2
  exit 2
fi

# ---------------------------------------------------------------------------------------------
# State A — the version is staged and its tag was never pushed.
# ---------------------------------------------------------------------------------------------
if ! git rev-parse -q --verify "refs/tags/$tag" >/dev/null 2>&1; then
  bump="$(git log --format='%H' -S"<PackageVersion>$version</PackageVersion>" \
            -- Directory.Build.props | tail -1 || true)"
  if [ -n "$bump" ]; then
    n="$(git rev-list --count "$bump..HEAD" 2>/dev/null || echo '?')"
    when="$(git log -1 --format='%ad' --date=short "$bump" 2>/dev/null || echo '?')"
    short="$(git rev-parse --short "$bump")"
    echo "::notice title=Release staged::$version is staged since $when ($short), $n commit(s) ago, and $tag does not exist yet — push the tag when the release is ready."
  else
    echo "::notice title=Release staged::$version is staged and $tag does not exist yet — push the tag when the release is ready."
  fi

  echo "publish-liveness: version $version is staged but never tagged."
  echo
  echo "  <PackageVersion> names $version and the tag $tag does not exist, so publish.yml has never"
  echo "  run for it. That workflow triggers on 'push: tags: v*' and nothing else, which is why this"
  echo "  state produces no run anywhere — only silence. This notice is that missing signal. It is"
  echo "  expected between the release PR's merge and the tag push, however long that takes."
  echo
  if [ -n "$bump" ]; then
    echo "  Staged since $when ($short), $n commit(s) ago."
    echo
  fi

  echo "  Fix: push the tag — 'git tag $tag <sha-on-main> && git push origin $tag'. Annotated or"
  echo "  lightweight both work: publish.yml's provenance gate peels \$GITHUB_SHA to a commit before"
  echo "  it asks the check-runs API, which cannot resolve a tag object. See ADR-0055."
  echo "  (Drift since the previous release is not reported until this tag exists: it is measured from it.)"
  exit 0
fi

# ---------------------------------------------------------------------------------------------
# State B — the version shipped, and consumer-visible content has landed on top of it.
# ---------------------------------------------------------------------------------------------
mapfile -t changed < <(consumer_visible_changes "$tag")
unreleased_bytes="$(changelog_section_bytes 'Unreleased')"

if [ "${#changed[@]}" -eq 0 ] && [ "$unreleased_bytes" -le "$BODY_WARN" ]; then
  echo "publish-liveness: $tag is published and nothing consumer-visible has moved since."
  echo "  (Silence means no shipped path changed — not that the repo is tidy. Test-only dependency"
  echo "  bumps and build-only property changes are filtered out by design; see the header.)"
  exit 0
fi

# The path lists below are sliced, never piped into `head`: under pipefail a reader that stops
# early hands the writer SIGPIPE, and a list past the pipe buffer turned a green report into a crash.
# The `grep -v … >/dev/null` tests above read to the end for the same reason (`grep -q` stops early).

# How long has the oldest unreleased shipped change been waiting?
age_days=0
oldest="$(first_shipping_commit "$tag")"
if [ -n "$oldest" ]; then
  oldest_epoch="$(git log -1 --format='%at' "$oldest")"
  now_epoch="$(git log -1 --format='%at' HEAD)" # HEAD's own time, so the result is reproducible
  age_days=$(( (now_epoch - oldest_epoch) / 86400 ))
fi

time_due=0
size_due=0
# Only when something is actually waiting: a shipped change that was later reverted still starts
# the clock, but leaves no path to release and so nothing to ask about.
if [ "${#changed[@]}" -gt 0 ] && [ "$age_days" -gt "$STALE_DAYS" ]; then
  time_due=1
fi
if [ "$unreleased_bytes" -gt "$BODY_WARN" ]; then
  size_due=1
fi

# Both halves are notices, never failures: one annotation line each, at the start of a line, where
# GitHub reads a workflow command. `%` is left literal: the runner only decodes %25, %0D and %0A.
if [ "$time_due" -eq 1 ]; then
  echo "::notice title=Release cadence::${#changed[@]} consumer-visible path(s) waiting $age_days days since $tag — cut a release?"
fi
if [ "$size_due" -eq 1 ]; then
  echo "::notice title=Release notes size::[Unreleased] is $unreleased_bytes bytes, $(( unreleased_bytes * 100 / BODY_LIMIT ))% of GitHub's $BODY_LIMIT-character release-body cap — past the cap the release page gets a shortened body with a link to CHANGELOG.md; condense the section or cut a release."
fi

if [ "$size_due" -eq 0 ]; then
  echo "publish-liveness: $tag is published; ${#changed[@]} consumer-visible path(s) have moved since."
  if [ "${#changed[@]}" -gt 0 ]; then
    printf '    %s\n' "${changed[@]:0:10}"
    [ "${#changed[@]}" -gt 10 ] && echo "    … and $(( ${#changed[@]} - 10 )) more"
  fi
  echo "  Oldest unreleased shipped change: $age_days day(s) (notice after $STALE_DAYS)."
  echo "  [Unreleased] CHANGELOG section: $unreleased_bytes bytes (cap $BODY_LIMIT, warn $BODY_WARN)."
  if [ "$time_due" -eq 1 ]; then
    echo "  Waiting longer than $STALE_DAYS days — a question for the owner, not a failure. The date is"
    echo "  an estimate, not a deadline; cut when the content is ready."
  else
    echo "  Within budget — reported so the drift is visible, not because anything is wrong."
  fi
  exit 0
fi

echo "publish-liveness: $tag is published; the [Unreleased] section is near the release-body cap."
echo
echo "  * the [Unreleased] CHANGELOG section is $unreleased_bytes bytes — past 90% of the ${BODY_LIMIT}-character release-body cap"
if [ "$time_due" -eq 1 ]; then
  echo "  * unreleased shipped content is $age_days days old (notice after $STALE_DAYS)"
fi
echo
echo "  Not a failure: past the cap publish.yml publishes a shortened release body (the BREAKING"
echo "  entries, the headings and a link to CHANGELOG.md) instead of the whole section. Under the cap"
echo "  the release page carries the section in full."
echo
echo "  <PackageVersion> still names $version, whose tag $tag already exists. publish.yml only runs"
echo "  on a tag push and this version's tag has been used, so nothing below can reach a consumer"
echo "  until the version moves."
if [ "${#changed[@]}" -gt 0 ]; then
  echo
  echo "  Changed since $tag:"
  echo
  printf '    %s\n' "${changed[@]:0:30}"
fi
echo
echo "  Fix: condense the [Unreleased] section, or cut a release — bump <PackageVersion> in"
echo "  Directory.Build.props and rename '## [Unreleased]' to the new version in CHANGELOG.md"
echo "  (publish.yml refuses to release without that section). The bump moves this repo into the"
echo "  'staged, never cut' state, which this same check reports until the tag is pushed."
exit 0
