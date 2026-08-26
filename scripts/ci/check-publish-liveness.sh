#!/usr/bin/env bash
# check-publish-liveness.sh — answers ONE question: is a release overdue?
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
# 125,000-character release-body cap — 94% of a limit that, once crossed, blocks the release itself.
#
# ADAPTED FROM Sdk.Pro's script of the same name, NOT copied, and the calibration is the reason.
# Pro's `release.yml` runs on push to main and creates the tag itself, so unreleased content exists
# for minutes and any accumulation at all is worth failing on. The SDK cuts tags by hand roughly
# monthly, so unreleased content is the NORMAL state — a check that fails on its mere existence
# would be red most of every month, and a signal that is red most of the time protects nothing.
# What is abnormal is unreleased content that has been waiting too long, or a CHANGELOG section
# heading for the cap. Those are what this fails on. See ADR-0055.
#
# THE STATES
#
#   A. STAGED, NEVER CUT — <PackageVersion> names a version whose tag does not exist. Fails
#      immediately, with no grace: the fix is one command, and on the intended path the window is
#      minutes. One red run on a release you are already cutting costs a maintainer 20 minutes of
#      annoyance; the silent version of this state cost three days.
#
#   B. SHIPPED, THEN DRIFTED — the tag exists and consumer-visible content has landed on top of it.
#      Reported always, failed only past a budget:
#        * the oldest unreleased consumer-visible commit is more than STALE_DAYS old (default 14 —
#          half the cycle above, while the trend is still correctable), or
#        * the `[Unreleased]` CHANGELOG section is past 90% of the release-body cap, the same
#          threshold publish.yml warns at, so the two agree rather than one surprising the other.
#      Within budget it prints the drift and exits 0. That number being visible every week is the
#      point; being red every week would not be.
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
# NOT here, and correctly ignored: Tests/, Examples/, docs/, openspec/, scripts/, .github/.
#
# Keying state B on a NON-EMPTY `[Unreleased]` section was rejected — it counts recorded work rather
# than shippable work, so it fires on docs-only merges. Its SIZE is used instead, which is a
# statement about the release body and not about whether work happened.
#
# VERSION-INVARIANT BY CONSTRUCTION: no version literal appears below; the tag is derived from
# Directory.Build.props at run time, so no future release edits this file.
#
# Exit 0 = nothing overdue (drift may still be reported). Exit 1 = a release is owed. Exit 2 = cannot tell.
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
    | grep -qv 'PrivateAssets="all"'
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
         | grep -qvE 'PackageValidationBaselineVersion'; then
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

version="$(sed -n 's:.*<PackageVersion>\(.*\)</PackageVersion>.*:\1:p' Directory.Build.props | head -1)"
if [ -z "$version" ]; then
  echo "publish-liveness: no <PackageVersion> in Directory.Build.props — cannot determine the tag" >&2
  exit 2
fi
tag="v$version"

# ---------------------------------------------------------------------------------------------
# State A — the version is staged and its tag was never pushed.
# ---------------------------------------------------------------------------------------------
if ! git rev-parse -q --verify "refs/tags/$tag" >/dev/null 2>&1; then
  echo "publish-liveness: FAILED — version $version is staged but never tagged."
  echo
  echo "  <PackageVersion> names $version and the tag $tag does not exist, so publish.yml has never"
  echo "  run for it. That workflow triggers on 'push: tags: v*' and nothing else, which is why this"
  echo "  state produces no failing run anywhere — only silence. This check is that missing signal."
  echo

  bump="$(git log --format='%H' -S"<PackageVersion>$version</PackageVersion>" \
            -- Directory.Build.props | tail -1 || true)"
  if [ -n "$bump" ]; then
    n="$(git rev-list --count "$bump..HEAD" 2>/dev/null || echo '?')"
    when="$(git log -1 --format='%ad' --date=short "$bump" 2>/dev/null || echo '?')"
    echo "  Staged since $when ($(git rev-parse --short "$bump")), $n commit(s) ago."
    echo
  fi

  echo "  Fix: push the tag — 'git tag $tag <sha-on-main> && git push origin $tag'. Annotated or"
  echo "  lightweight both work: publish.yml's provenance gate peels \$GITHUB_SHA to a commit before"
  echo "  it asks the check-runs API, which cannot resolve a tag object. See ADR-0055."
  exit 1
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

# How long has the oldest unreleased shipped change been waiting?
age_days=0
oldest="$(first_shipping_commit "$tag")"
if [ -n "$oldest" ]; then
  oldest_epoch="$(git log -1 --format='%at' "$oldest")"
  now_epoch="$(git log -1 --format='%at' HEAD)" # HEAD's own time, so the result is reproducible
  age_days=$(( (now_epoch - oldest_epoch) / 86400 ))
fi

overdue=0
reasons=()
if [ "$age_days" -gt "$STALE_DAYS" ]; then
  overdue=1
  reasons+=("unreleased shipped content is $age_days days old (budget: $STALE_DAYS)")
fi
if [ "$unreleased_bytes" -gt "$BODY_WARN" ]; then
  overdue=1
  reasons+=("the [Unreleased] CHANGELOG section is $unreleased_bytes bytes — past 90% of the ${BODY_LIMIT}-character release-body cap")
fi

if [ "$overdue" -eq 0 ]; then
  echo "publish-liveness: $tag is published; ${#changed[@]} consumer-visible path(s) have moved since."
  if [ "${#changed[@]}" -gt 0 ]; then
    printf '    %s\n' "${changed[@]}" | head -10
    [ "${#changed[@]}" -gt 10 ] && echo "    … and $(( ${#changed[@]} - 10 )) more"
  fi
  echo "  Oldest unreleased shipped change: $age_days day(s) (budget $STALE_DAYS)."
  echo "  [Unreleased] CHANGELOG section: $unreleased_bytes bytes (cap $BODY_LIMIT, warn $BODY_WARN)."
  echo "  Within budget — reported so the drift is visible, not because anything is wrong."
  exit 0
fi

echo "publish-liveness: FAILED — a release is overdue."
echo
for r in "${reasons[@]}"; do echo "  * $r"; done
echo
echo "  <PackageVersion> still names $version, whose tag $tag already exists. publish.yml only runs"
echo "  on a tag push and this version's tag has been used, so nothing below can reach a consumer"
echo "  until the version moves."
echo
echo "  Changed since $tag:"
echo
printf '    %s\n' "${changed[@]}" | head -30
echo
echo "  Fix: bump <PackageVersion> in Directory.Build.props and rename '## [Unreleased]' to the new"
echo "  version in CHANGELOG.md — publish.yml refuses to release without that section, and hard-fails"
echo "  pre-push if it exceeds $BODY_LIMIT characters. The bump moves this repo into the 'staged,"
echo "  never cut' state, which this same check reports until the tag is pushed."
exit 1
