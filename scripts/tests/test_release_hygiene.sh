#!/usr/bin/env bash
# test_release_hygiene.sh — unit tests for scripts/ci/check-publish-liveness.sh and
# scripts/ci/check-apicompat-baseline.sh (ADR-0055).
#
# Both scripts fire on states that, by construction, only occur when nobody is looking — a release
# nobody cut, a baseline nobody moved. There is no natural occasion to find out they are broken, and
# a broken one fails the way the bug it guards against fails: silently, green. So every state gets a
# case here, in both directions. The same precedent as the docs-only classifier's harness: the guard
# itself is tested, and it runs in the ALWAYS-RUN `Coverage Script Tests` job.
#
# Each case builds a throwaway repo with the shape the scripts actually read — a Directory.Build.props
# carrying both properties, a Directory.Packages.props, one shipped csproj, a CHANGELOG and a release
# tag. Commit dates are set explicitly, because the whole point of the liveness budget is that it is
# a function of time. Pure bash + git, no network (the feed lookup is redirected), ~3s.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIVENESS="$SCRIPT_DIR/../ci/check-publish-liveness.sh"
BASELINE="$SCRIPT_DIR/../ci/check-apicompat-baseline.sh"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

# A date far from "now" so cases are stable whenever they run; ages are relative to HEAD's own
# timestamp, never to the wall clock, so this harness cannot rot.
BASE_DATE='2026-01-01T12:00:00'

g() { git -C "$WORK" "$@"; }

commit_at() { # commit_at <iso-date> <message>
  GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" \
    g -c user.email=t@t.t -c user.name=t commit -q --allow-empty -am "$2"
}

props() { # props <version> <baseline>
  cat > "$WORK/Directory.Build.props" <<EOF
<Project>
  <PropertyGroup>
    <PackageVersion>$1</PackageVersion>
  </PropertyGroup>
  <PropertyGroup Condition="\$(MSBuildProjectDirectory.Contains('src'))">
    <PackageValidationBaselineVersion>$2</PackageValidationBaselineVersion>
  </PropertyGroup>
</Project>
EOF
}

changelog() { # changelog <unreleased-filler-bytes>
  { echo '# Changelog'; echo; echo '## [Unreleased]'; echo
    [ "${1:-0}" -gt 0 ] && head -c "$1" /dev/zero | tr '\0' 'x'
    echo; echo '## [1.0.0] - 2026-01-01'; echo; echo '- shipped'; } > "$WORK/CHANGELOG.md"
}

new_repo() { # new_repo — a released repo at v1.0.0 with nothing pending
  WORK="$(mktemp -d)"
  g init -q
  mkdir -p "$WORK/src/Foo"
  cat > "$WORK/src/Foo/Foo.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Shipped.Pkg" />
    <PackageReference Include="Analyzer.Pkg" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF
  echo '// code' > "$WORK/src/Foo/Foo.cs"
  cat > "$WORK/Directory.Packages.props" <<'EOF'
<Project>
  <ItemGroup>
    <PackageVersion Include="Shipped.Pkg" Version="1.0.0" />
    <PackageVersion Include="TestOnly.Pkg" Version="1.0.0" />
    <PackageVersion Include="Analyzer.Pkg" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
  props 1.0.0 1.0.0
  changelog 0
  g add -A
  commit_at "$BASE_DATE" "seed"
  g tag v1.0.0
}

# run <script> <expected-exit> <description> [env assignments...] — asserts the exit status and
# leaves the output in $OUT for the caller to grep.
run() {
  local script="$1" expected="$2" desc="$3"; shift 3
  local actual=0
  OUT="$(cd "$WORK" && env "$@" bash "$script" 2>&1)" || actual=$?
  if [ "$actual" -eq "$expected" ]; then ok; else
    bad "$desc — expected exit $expected, got $actual"
    printf '%s\n' "$OUT" | sed 's/^/      | /'
  fi
}

says() { # says <needle> <description>
  case "$OUT" in *"$1"*) ok ;; *) bad "$2 — output did not mention '$1'";; esac
}

# =============================================================================================
# contract
# =============================================================================================
[ -x "$LIVENESS" ] && ok || bad "check-publish-liveness.sh must be committed executable (mode 100755)"
[ -x "$BASELINE" ] && ok || bad "check-apicompat-baseline.sh must be committed executable (mode 100755)"

# =============================================================================================
# liveness — the silent case
# =============================================================================================
new_repo
run "$LIVENESS" 0 "released, nothing pending"
says "nothing consumer-visible has moved" "released, nothing pending"

# A docs/test-only merge is NOT a release trigger.
mkdir -p "$WORK/docs"; echo x > "$WORK/docs/note.md"; g add -A
commit_at '2026-06-01T12:00:00' "docs: unrelated"
run "$LIVENESS" 0 "docs-only merge, long after the tag"
says "nothing consumer-visible has moved" "docs-only merge stays silent even when old"

# =============================================================================================
# liveness — the two filters that keep the signal honest
# =============================================================================================
new_repo
sed -i 's|Include="TestOnly.Pkg" Version="1.0.0"|Include="TestOnly.Pkg" Version="2.0.0"|' "$WORK/Directory.Packages.props"
commit_at '2026-06-01T12:00:00' "chore: bump a test-only dependency"
run "$LIVENESS" 0 "test-only dependency bump does not demand a release"

# An analyzer referenced with PrivateAssets=all does not reach a consumer either.
sed -i 's|Include="Analyzer.Pkg" Version="1.0.0"|Include="Analyzer.Pkg" Version="2.0.0"|' "$WORK/Directory.Packages.props"
commit_at '2026-06-02T12:00:00' "chore: bump an analyzer"
run "$LIVENESS" 0 "PrivateAssets=all dependency bump does not demand a release"

new_repo
props 1.0.0 0.9.0   # only the ApiCompat baseline moves
commit_at '2026-06-01T12:00:00' "chore: ratchet the baseline"
run "$LIVENESS" 0 "a baseline-only change must not start the clock"
says "nothing consumer-visible has moved" "baseline-only change is filtered out of Directory.Build.props"

# =============================================================================================
# liveness — state B, drift inside and outside the budget
# =============================================================================================
new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
run "$LIVENESS" 0 "fresh unreleased content is reported, not failed"
says "Within budget" "fresh drift reports rather than fails"
says "src/Foo/Foo.cs" "fresh drift names the shipped path"

new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
echo x > "$WORK/docs-note.txt"; g add -A
commit_at '2026-02-01T12:00:00' "chore: much later"
run "$LIVENESS" 1 "unreleased content past the 14-day budget fails"
says "a release is overdue" "aged drift fails"
says "30 days old" "aged drift reports the measured age"

# The budget is a judgement, so it must be adjustable — and adjustable in both directions.
run "$LIVENESS" 0 "a widened budget makes the same tree pass" STALE_DAYS=60
run "$LIVENESS" 1 "a narrowed budget fails a tree that passed at 14 days" STALE_DAYS=0

# The clock starts at the first SHIPPING commit, not the first commit in the range: an old
# test-only bump must not age a young shipped change.
new_repo
sed -i 's|Include="TestOnly.Pkg" Version="1.0.0"|Include="TestOnly.Pkg" Version="2.0.0"|' "$WORK/Directory.Packages.props"
commit_at '2026-01-02T12:00:00' "chore: old test-only bump"
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-02-01T12:00:00' "feat: recent shipped change"
run "$LIVENESS" 0 "an old test-only commit must not age a recent shipped one"
says "0 day(s)" "age is measured from the first SHIPPING commit"

# =============================================================================================
# liveness — state B, the release-body cap
# =============================================================================================
new_repo
changelog 120000   # past 90% of the 125,000-character cap, with no shipped change at all
g add -A
commit_at '2026-01-02T12:00:00' "docs: a very large changelog section"
run "$LIVENESS" 1 "an [Unreleased] section past 90% of the cap fails on its own"
says "release-body cap" "the cap failure names the cap"

new_repo
changelog 1000
g add -A
commit_at '2026-01-02T12:00:00' "docs: a small changelog section"
run "$LIVENESS" 0 "a small [Unreleased] section is not a trigger"

# =============================================================================================
# liveness — state A, and the cannot-tell case
# =============================================================================================
new_repo
props 1.1.0 1.0.0
commit_at '2026-01-02T12:00:00' "chore: stage 1.1.0"
run "$LIVENESS" 1 "a staged version with no tag fails immediately"
says "staged but never tagged" "state A is named"
says "Staged since 2026-01-02" "state A reports when it was staged"

new_repo
echo '<Project />' > "$WORK/Directory.Build.props"
commit_at '2026-01-02T12:00:00' "chore: drop the version"
run "$LIVENESS" 2 "no <PackageVersion> is 'cannot tell', not 'fine'"

# =============================================================================================
# apicompat baseline
# =============================================================================================
UNREACHABLE='FEED_INDEX_URL=http://127.0.0.1:1/index.json' # closed port: the warning path

new_repo
run "$BASELINE" 0 "baseline equal to the newest tag is current" "$UNREACHABLE"
says "is current" "current baseline says so"

new_repo
props 1.0.0 0.9.0
commit_at '2026-01-02T12:00:00' "chore: leave the baseline behind"
run "$BASELINE" 1 "a baseline behind the newest published tag fails" "$UNREACHABLE"
says "the baseline is stale" "stale baseline is named"
says "v1.0.0" "stale baseline lists what shipped since"

# A pre-release tag is not something a consumer could have restored, so it is not a baseline.
new_repo
g tag v1.1.0-beta
run "$BASELINE" 0 "a pre-release tag does not become the expected baseline" "$UNREACHABLE"

# And a newer STABLE tag does.
g tag v1.1.0
run "$BASELINE" 1 "a newer stable tag does become the expected baseline" "$UNREACHABLE"

new_repo
props 1.0.0 ''
commit_at '2026-01-02T12:00:00' "chore: empty baseline"
run "$BASELINE" 2 "an unreadable baseline is 'cannot tell', not 'fine'" "$UNREACHABLE"

# The feed check only fires when the feed answers. A response that lists the baseline is green; one
# that does not is a failure, because ApiCompat cannot download a baseline that is not published.
new_repo
FEED="$(mktemp -d)"; printf '{"versions":["0.9.0","1.0.0"]}' > "$FEED/index.json"
run "$BASELINE" 0 "a feed listing the baseline is green" "FEED_INDEX_URL=file://$FEED/index.json"
says "live on nuget.org" "the feed-confirmed path says so"
printf '{"versions":["0.9.0"]}' > "$FEED/index.json"
run "$BASELINE" 1 "a feed missing the baseline fails" "FEED_INDEX_URL=file://$FEED/index.json"
says "is not on nuget.org" "the missing-baseline failure is named"

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"
