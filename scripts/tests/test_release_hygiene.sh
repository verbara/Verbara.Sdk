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

# Every throwaway repo and feed lives under one root that is removed on exit, pass or fail, so a run
# leaves nothing behind in the temp directory.
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT
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
  WORK="$(mktemp -d -p "$TMP_ROOT")"
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

silent_on() { # silent_on <needle> <description> — the output must NOT contain the needle
  case "$OUT" in *"$1"*) bad "$2 — output mentioned '$1'" ;; *) ok ;; esac
}

says_line() { # says_line <exact line> <description> — GitHub reads a workflow command only at the start of a line
  if printf '%s\n' "$OUT" | command grep -xF -- "$1" >/dev/null; then ok; else bad "$2 — no line exactly '$1'"; fi
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
silent_on "::notice" "fresh drift within budget asks nothing"

new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
echo x > "$WORK/docs-note.txt"; g add -A
commit_at '2026-02-01T12:00:00' "chore: much later"
# Past the time budget is a QUESTION to the owner, not a failure: a date is an estimate, never a
# deadline. Exit 0, with a notice annotation asking whether to cut.
run "$LIVENESS" 0 "unreleased content past the 14-day budget asks, it does not fail"
says_line "::notice title=Release cadence::1 consumer-visible path(s) waiting 30 days since v1.0.0 — cut a release?" "aged drift raises the notice on its own line, with the path count"
says "cut a release?" "aged drift is phrased as a question"
says "30 days" "aged drift reports the measured age"
silent_on "FAILED" "aged drift alone is not a failure"

# The budget is a judgement, so it stays adjustable — it now sets when the question is asked.
run "$LIVENESS" 0 "a widened budget keeps the same tree quiet" STALE_DAYS=60
silent_on "::notice" "a widened budget asks nothing"
run "$LIVENESS" 0 "a narrowed budget still only asks" STALE_DAYS=0
says "::notice title=Release cadence::" "a narrowed budget raises the notice"
says "cut a release?" "a narrowed budget phrases it as a question"

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
# Past 90% of the cap is a notice, not a failure: publish.yml publishes a shortened body (BREAKING
# entries, headings and a link to CHANGELOG.md) when the section is over the cap, so the cap no
# longer blocks a release. It is still worth saying, once, on its own annotation line.
run "$LIVENESS" 0 "an [Unreleased] section past 90% of the cap is a notice, not a failure"
says_line "::notice title=Release notes size::[Unreleased] is 120002 bytes, 96% of GitHub's 125000-character release-body cap — past the cap the release page gets a shortened body with a link to CHANGELOG.md; condense the section or cut a release." "the size notice is on its own line, with the measured bytes and percentage"
says "release-body cap" "the size report names the cap"
silent_on "FAILED" "the size half alone is not a failure"
silent_on "Release cadence" "a large section with no aged content asks no time question"

# Both halves at once: both notices, each on its own line, and still exit 0.
new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
changelog 120000
g add -A
commit_at '2026-02-01T12:00:00' "docs: a very large changelog section, much later"
run "$LIVENESS" 0 "past the cap AND past the time budget prints both notices and exits 0"
says "release-body cap" "the combined report names the cap"
says_line "::notice title=Release cadence::1 consumer-visible path(s) waiting 30 days since v1.0.0 — cut a release?" "the combined report asks the time question, on its own line"
says_line "::notice title=Release notes size::[Unreleased] is 120002 bytes, 96% of GitHub's 125000-character release-body cap — past the cap the release page gets a shortened body with a link to CHANGELOG.md; condense the section or cut a release." "the combined report raises the size notice, on its own line"
says "30 days" "the combined report reports the measured age"
silent_on "FAILED" "the combined report is not a failure"

# A shipped change that was later reverted leaves nothing to release, so there is nothing to ask
# about — even when the size half fires and the reverted commit is old.
new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
g checkout -q HEAD~1 -- src/Foo/Foo.cs
commit_at '2026-01-03T12:00:00' "revert: ship something"
changelog 120000
g add -A
commit_at '2026-02-01T12:00:00' "docs: a very large changelog section, much later"
run "$LIVENESS" 0 "a reverted change with a large section still reports the size"
says_line "::notice title=Release notes size::[Unreleased] is 120002 bytes, 96% of GitHub's 125000-character release-body cap — past the cap the release page gets a shortened body with a link to CHANGELOG.md; condense the section or cut a release." "a reverted change with a large section still raises the size notice"
silent_on "Release cadence" "a reverted change leaves no path waiting, so nothing is asked"

new_repo
changelog 1000
g add -A
commit_at '2026-01-02T12:00:00' "docs: a small changelog section"
run "$LIVENESS" 0 "a small [Unreleased] section is not a trigger"

# =============================================================================================
# liveness — a long path list must not break its own report
# =============================================================================================
# The path list used to go through `printf … | head -N`. Once the list outgrows the pipe, head
# exits after N lines, printf takes SIGPIPE, and set -euo pipefail turns a green report into a
# crash (exit 1, "printf: write error: Broken pipe"; main's release-hygiene runs, 2026-10, at
# 218-240 paths). 3000 paths of ~100 bytes is ~300 KB, past any pipe buffer, so the old form fails
# every run rather than by race.
new_repo
long="$(printf 'x%.0s' $(seq 1 80))"
for i in $(seq 1 3000); do echo '// new' > "$WORK/src/Foo/F${i}_${long}.cs"; done
g add -A
commit_at '2026-01-02T12:00:00' "feat: ship many files"
run "$LIVENESS" 0 "a long path list within budget is reported, not crashed on"
says "… and 2990 more" "a long path list is cut at 10 with the remainder counted"
says "Within budget" "a long path list still reaches the end of the report"
silent_on "Broken pipe" "a long path list never breaks the pipe"

changelog 120000
g add -A
commit_at '2026-01-03T12:00:00' "docs: a very large changelog section"
run "$LIVENESS" 0 "a long path list past the cap reports the cap and exits 0"
says "release-body cap" "a long path list past the cap names the cap"
says "::notice title=Release notes size::" "a long path list past the cap raises the size notice"
says "Fix: condense the [Unreleased] section" "a long path list past the cap still reaches the fix line"
silent_on "Broken pipe" "a long path list past the cap never breaks the pipe"
[ "$(printf '%s\n' "$OUT" | command grep -c '^    src/Foo/')" -eq 30 ] && ok \
  || bad "a long path list past the cap is cut at 30 lines"

# The same shape hid inside two `grep -q` tests: grep -q exits on its first match, and the grep
# feeding it takes SIGPIPE once its output outgrows the pipe — under pipefail that reads as "no
# match", silently dropping a shipped change from the list.
new_repo
{ echo '<Project Sdk="Microsoft.NET.Sdk">'; echo '  <ItemGroup>'
  for i in $(seq 1 3000); do echo "    <PackageReference Include=\"Shipped.Pkg\" Condition=\"'\$(N)' == '$i'\" />"; done
  echo '  </ItemGroup>'; echo '</Project>'; } > "$WORK/src/Foo/Foo.csproj"
g add -A
commit_at "$BASE_DATE" "chore: many references"
g tag -f v1.0.0 >/dev/null
sed -i 's|Include="Shipped.Pkg" Version="1.0.0"|Include="Shipped.Pkg" Version="2.0.0"|' "$WORK/Directory.Packages.props"
commit_at '2026-01-02T12:00:00' "chore: bump a shipped dependency"
run "$LIVENESS" 0 "a shipped dependency referenced many times is still seen"
says "Directory.Packages.props (ships: Shipped.Pkg)" "a long reference list does not hide a shipped bump"

new_repo
{ echo '<Project>'; echo '  <PropertyGroup>'; echo '    <PackageVersion>1.0.0</PackageVersion>'
  for i in $(seq 1 3000); do echo "    <Prop$i>value-$i-$long</Prop$i>"; done
  echo '  </PropertyGroup>'; echo '</Project>'; } > "$WORK/Directory.Build.props"
commit_at '2026-01-02T12:00:00' "chore: many build properties"
run "$LIVENESS" 0 "a long Directory.Build.props diff is still seen"
says "    Directory.Build.props" "a long Directory.Build.props diff does not hide itself"

# =============================================================================================
# liveness — state A, and the cannot-tell case
# =============================================================================================
# State A is a notice, not a failure: on every release it fires on the intended path, between the
# release PR's merge and the tag push, and a closing campaign can hold that window open for weeks.
new_repo
props 1.1.0 1.0.0
commit_at '2026-01-02T12:00:00' "chore: stage 1.1.0"
bump_sha="$(g rev-parse --short HEAD)"
echo x > "$WORK/docs-note.txt"; g add -A
commit_at '2026-01-05T12:00:00' "docs: after the bump"
run "$LIVENESS" 0 "a staged version with no tag is a notice, not a failure"
says_line "::notice title=Release staged::1.1.0 is staged since 2026-01-02 ($bump_sha), 1 commit(s) ago, and v1.1.0 does not exist yet — push the tag when the release is ready." "state A raises the notice on its own line, with date, sha and distance"
says "staged but never tagged" "state A is named in the report"
says "Staged since 2026-01-02" "state A reports when it was staged"
says "git tag v1.1.0" "state A keeps the one-line fix"
silent_on "FAILED" "state A is not a failure"

# While state A holds, state B is not evaluated: its diff base is the staged version's tag, which
# does not exist. A large section and old shipped content ask nothing until the tag is pushed.
new_repo
echo '// changed' >> "$WORK/src/Foo/Foo.cs"
commit_at '2026-01-02T12:00:00' "feat: ship something"
props 1.1.0 1.0.0
changelog 120000
g add -A
commit_at '2026-02-01T12:00:00' "chore: stage 1.1.0 with a very large section"
run "$LIVENESS" 0 "state A with state B's triggers present still exits 0"
says "::notice title=Release staged::1.1.0 is staged since 2026-02-01" "state A's notice is raised"
silent_on "Release notes size" "state B's size half is not evaluated while state A holds"
silent_on "Release cadence" "state B's time half is not evaluated while state A holds"

# A bump that is not committed yet has no commit to date: the notice says so without one.
new_repo
props 1.1.0 1.0.0
run "$LIVENESS" 0 "an uncommitted staged version is a notice too"
says_line "::notice title=Release staged::1.1.0 is staged and v1.1.0 does not exist yet — push the tag when the release is ready." "state A without a bump commit still raises a well-formed notice"

new_repo
echo '<Project />' > "$WORK/Directory.Build.props"
commit_at '2026-01-02T12:00:00' "chore: drop the version"
run "$LIVENESS" 2 "no <PackageVersion> is 'cannot tell', not 'fine'"

# A broken checkout must not read as "staged": state A is now a green notice, so a missing tag
# from a failed fetch, a shallow clone or no repository at all would otherwise pass silently.
new_repo
g tag -d v1.0.0 >/dev/null
run "$LIVENESS" 2 "a repository with no release tags at all is 'cannot tell', not 'staged'"
silent_on "Release staged" "no tags at all raises no staged notice"

new_repo
rm -rf "$WORK/.git"
run "$LIVENESS" 2 "outside a git repository is 'cannot tell', not 'staged'"

new_repo
SRC="$WORK"; WORK="$(mktemp -d -p "$TMP_ROOT")"; rmdir "$WORK"
git clone -q --depth 1 "file://$SRC" "$WORK" 2>/dev/null
g fetch -q --tags 2>/dev/null || true
run "$LIVENESS" 2 "a shallow clone is 'cannot tell', not 'staged' or 'fine'"

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
FEED="$(mktemp -d -p "$TMP_ROOT")"; printf '{"versions":["0.9.0","1.0.0"]}' > "$FEED/index.json"
run "$BASELINE" 0 "a feed listing the baseline is green" "FEED_INDEX_URL=file://$FEED/index.json"
says "live on nuget.org" "the feed-confirmed path says so"
printf '{"versions":["0.9.0"]}' > "$FEED/index.json"
run "$BASELINE" 1 "a feed missing the baseline fails" "FEED_INDEX_URL=file://$FEED/index.json"
says "is not on nuget.org" "the missing-baseline failure is named"

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"
