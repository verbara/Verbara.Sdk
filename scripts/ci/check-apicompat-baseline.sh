#!/usr/bin/env bash
# check-apicompat-baseline.sh — keeps <PackageValidationBaselineVersion> pointing at the newest
# published release.
#
# WHY. Package validation is the only thing in this repo that catches an unintended binary break:
# it downloads the baseline package and diffs the public surface of what is being packed against
# it. That guarantee is worth exactly as much as the baseline is current. Every release makes it one
# version stale, and nothing notices — the build stays green, because comparing against an older
# package is still a valid comparison. It just stops covering everything that shipped in between.
#
# This is not hypothetical either: the baseline sat at 2.1.0 while 2.2.x, 2.3.x and 2.4.0 shipped,
# and it was moved to 2.4.0 by hand during the 2.5.0 cut — where it immediately went stale again the
# moment v2.5.0 was tagged. A ratchet nobody turns is not a ratchet. See ADR-0055.
#
# THE RULE: baseline == the highest published stable tag, always. Note the ordering this implies —
# the baseline is bumped AFTER a release is tagged, never as part of preparing one. Bumping it in
# the release PR would point ApiCompat at a package that does not exist yet and break every restore
# under src/, so this check deliberately reads git tags rather than <PackageVersion>.
#
# Pre-release tags are excluded from "highest": a baseline must be something a consumer could
# actually have restored.
#
# Usage: scripts/ci/check-apicompat-baseline.sh
# Exit 0 = current. Exit 1 = stale, or points at a version nuget.org does not have. Exit 2 = cannot tell.
set -euo pipefail

baseline="$(sed -n 's:.*<PackageValidationBaselineVersion>\(.*\)</PackageValidationBaselineVersion>.*:\1:p' \
              Directory.Build.props | head -1)"
if [ -z "$baseline" ]; then
  echo "apicompat-baseline: no <PackageValidationBaselineVersion> in Directory.Build.props" >&2
  exit 2
fi

latest_tag="$(git tag --list 'v*' --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -1 || true)"
if [ -z "$latest_tag" ]; then
  echo "apicompat-baseline: no stable v*.*.* tag found — cannot determine the expected baseline" >&2
  exit 2
fi
expected="${latest_tag#v}"

if [ "$baseline" != "$expected" ]; then
  echo "apicompat-baseline: FAILED — the baseline is stale."
  echo
  echo "  <PackageValidationBaselineVersion> is $baseline; the newest published release is $expected."
  echo "  Package validation is still running, and still passing — it is just no longer comparing"
  echo "  against everything that has shipped. Any public-surface change made in $baseline..$expected"
  echo "  is outside what it can see."
  echo
  # Strictly-newer comparison rather than "everything after the baseline's own tag": the baseline
  # is not guaranteed to be a tag in this repo at all (it can name a version published from
  # elsewhere, or one whose tag was never pushed), and an anchor that is absent silently prints
  # nothing — which reads as "nothing shipped since", the opposite of the truth.
  echo "  Releases published since the baseline:"
  git tag --list 'v*' --sort=v:refname \
    | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' \
    | while read -r t; do
        v="${t#v}"
        [ "$v" != "$baseline" ] || continue
        [ "$(printf '%s\n%s\n' "$baseline" "$v" | sort -V | head -1)" = "$baseline" ] && echo "    $t"
      done
  echo
  echo "  Fix: set <PackageValidationBaselineVersion> to $expected in Directory.Build.props."
  echo "  Expect ApiCompat errors if anything broke — that is the check doing its job, and the"
  echo "  CHANGELOG entry for each break is where the justification belongs."
  exit 1
fi

# The baseline must be a package that actually exists, or every restore under src/ fails with a
# NuGet error that says nothing about this file. A tag can exist without a successful publish.
# FEED_INDEX_URL is overridable so the unit tests can exercise the version comparison without a
# network call; unreachable is the warning path, never a failure.
INDEX="${FEED_INDEX_URL:-https://api.nuget.org/v3-flatcontainer/verbara.sdk/index.json}"
if body="$(curl -fsS --max-time 20 "$INDEX" 2>/dev/null)"; then
  if printf '%s' "$body" | grep -q "\"$baseline\""; then
    echo "apicompat-baseline: $baseline is current ($latest_tag) and live on nuget.org."
  else
    echo "apicompat-baseline: FAILED — baseline $baseline is not on nuget.org."
    echo
    echo "  $latest_tag exists as a tag, so this repo believes $baseline is published, but the flat"
    echo "  container does not list it. Either the publish for that tag did not finish, or it is"
    echo "  still indexing. Package validation cannot download a baseline that is not there, so"
    echo "  every restore under src/ will fail until this resolves."
    echo
    echo "  Check the publish run for $latest_tag before changing anything here."
    exit 1
  fi
else
  echo "apicompat-baseline: $baseline is current ($latest_tag)."
  echo "::warning::Could not reach $INDEX — the baseline's version match was checked, its presence on nuget.org was not."
fi
