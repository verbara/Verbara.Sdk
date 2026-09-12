#!/usr/bin/env bash
# test_release_provenance.sh — unit tests for scripts/ci/check-release-provenance.sh (ADR-0055
# addendum, 2026-09-12).
#
# The gate runs only when a tag is pushed, so no PR ever exercises it, and a broken one is found while
# a release waits on it: blocked by a check that should not count — which is how v2.5.1 found the
# first version — or, the worse direction, waved past one that should. So every rule gets a case here
# in both directions, and this runs in the ALWAYS-RUN `Coverage Script Tests` job beside the
# release-hygiene harness, for ADR-0055 D7's reason.
#
# Fixtures are the two API documents in the shape publish.yml hands over: one check run, or one
# workflow run, per JSON value — what `gh api --paginate --jq '.check_runs[]'` prints. The check suite
# ids, check names and workflow paths in the 2.5.1 cases are the real ones from commit 019fb697.
# Pure bash + jq, no network, no gh, ~2s.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$SCRIPT_DIR/../ci/check-release-provenance.sh"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

SHA=019fb697f654aecbfe70d298aadc7f1c678cadc5
HYGIENE=.github/workflows/release-hygiene.yml

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
CHECKS="$WORK/check-runs.json"
RUNS="$WORK/workflow-runs.json"

# The check suites on 019fb697, and one that exists on no real commit.
CI_MQ=93987543312        # .github/workflows/ci.yml, merge_group
CODEQL_MQ=93987543288    # .github/workflows/codeql.yml, merge_group
CODEQL_PUSH=93990069653  # .github/workflows/codeql.yml, push
AOT_PUSH=93990069617     # .github/workflows/aot-validate.yml, push
HYGIENE_PUSH=93990069616 # .github/workflows/release-hygiene.yml, push
PUBLISH=93990198232      # .github/workflows/publish.yml, push (the tag)
OTHER=555
DEPENDABOT=dynamic/dependabot/dependabot-updates # GitHub-assigned: no workflow file can have it
DEPENDABOT_SUITE=666     # a Dependabot version-update run, attached to whatever commit is main's HEAD

fresh() { : > "$CHECKS"; : > "$RUNS"; }

# check <suite-id> <name> <status> [conclusion] — appends one check run; no conclusion means null.
check() {
  jq -cn --argjson suite "$1" --arg name "$2" --arg status "$3" --arg conclusion "${4:-}" '
    { name: $name, status: $status,
      conclusion: (if $conclusion == "" then null else $conclusion end),
      check_suite: { id: $suite }, app: { slug: "github-actions" },
      html_url: "https://github.invalid/runs/\($suite)/\($name | @uri)" }' >> "$CHECKS"
}

# workflow <suite-id> <path> — appends one workflow run owning that check suite.
workflow() {
  jq -cn --argjson suite "$1" --arg path "$2" '{ check_suite_id: $suite, path: $path }' >> "$RUNS"
}

# Built once and copied per case: each suite owned by one workflow run, and the twelve merge-queue
# check runs that landed 019fb697 (ci.yml's ten, codeql.yml's two), all green.
fresh
workflow "$CI_MQ" .github/workflows/ci.yml
workflow "$CODEQL_MQ" .github/workflows/codeql.yml
workflow "$CODEQL_PUSH" .github/workflows/codeql.yml
workflow "$AOT_PUSH" .github/workflows/aot-validate.yml
workflow "$HYGIENE_PUSH" "$HYGIENE"
workflow "$PUBLISH" .github/workflows/publish.yml
for name in "AOT Trim Check" "Audit Test Asserts" "Coverage Ratchet" "Coverage Script Tests" \
            "Docs-only gate" "Functional Tests (Testcontainers) (22)" \
            "Functional Tests (Testcontainers) (23)" "OpenSpec Validate" "Pack Warnings Gate" "Unit Tests"; do
  check "$CI_MQ" "$name" completed success
done
check "$CODEQL_MQ" "Analyze (C#)" completed success
check "$CODEQL_MQ" "Docs-only gate (CodeQL)" completed success
cp "$RUNS" "$WORK/suites.json"
cp "$CHECKS" "$WORK/merge-queue.json"

landed() { cp "$WORK/suites.json" "$RUNS"; cp "$WORK/merge-queue.json" "$CHECKS"; }

# release_2_5_1 <liveness> <baseline> — the check runs as attempt 1 of the 2.5.1 publish run saw them
# at 14:19:55Z: fifteen completed, three still running (the gate's own job among them).
release_2_5_1() {
  landed
  check "$CODEQL_PUSH" "Docs-only gate (CodeQL)" completed success
  check "$CODEQL_PUSH" "Analyze (C#)" in_progress
  check "$AOT_PUSH" "aot-check" in_progress
  check "$HYGIENE_PUSH" "Publish Liveness" completed "$1"
  check "$HYGIENE_PUSH" "ApiCompat Baseline" completed "$2"
  check "$PUBLISH" "Pack and push to nuget.org" in_progress
}

# run <expected-exit> <description> [args...] — runs the gate (by default on the current fixtures),
# asserts the exit status and leaves the output in $OUT.
run() {
  local expected="$1" desc="$2" actual=0
  shift 2
  [ "$#" -gt 0 ] || set -- "$SHA" "$CHECKS" "$RUNS"
  OUT="$(bash "$GATE" "$@" 2>&1)" || actual=$?
  if [ "$actual" -eq "$expected" ]; then ok; else
    bad "$desc — expected exit $expected, got $actual"
    printf '%s\n' "$OUT" | sed 's/^/      | /'
  fi
}

says()  { case "$OUT" in *"$1"*) ok ;; *) bad "$2 — output did not mention '$1'" ;; esac; }
lacks() { case "$OUT" in *"$1"*) bad "$2 — output mentioned '$1'" ;; *) ok ;; esac; }

# errors_name / no_error_names <needle> <description> — about the ::error:: lines only.
errors_name() {
  if grep '^::error::' <<<"$OUT" | grep -F -- "$1" >/dev/null; then ok; else
    bad "$2 — no ::error:: line mentioned '$1'"
    printf '%s\n' "$OUT" | sed 's/^/      | /'
  fi
}
no_error_names() {
  if grep '^::error::' <<<"$OUT" | grep -F -- "$1" >/dev/null; then bad "$2 — an ::error:: line mentioned '$1'"; else ok; fi
}

# =============================================================================================
# contract
# =============================================================================================
[ -x "$GATE" ] && ok || bad "check-release-provenance.sh must be committed executable (mode 100755)"

# The exclusions are exact paths. The hygiene path names a file in this repository, so renaming it must
# fail HERE, on the PR that renames it, rather than silently ending the exclusion at the next release.
[ -f "$SCRIPT_DIR/../../$HYGIENE" ] && ok || bad "$HYGIENE must exist — the gate ignores check runs by that exact path"
grep -qxF "HYGIENE_WORKFLOW='$HYGIENE'" "$GATE" && ok || bad "the gate's HYGIENE_WORKFLOW must be '$HYGIENE'"
grep -qxF "DEPENDABOT_WORKFLOW='$DEPENDABOT'" "$GATE" && ok || bad "the gate's DEPENDABOT_WORKFLOW must be '$DEPENDABOT'"

# =============================================================================================
# green — and the notice appears only when something was ignored
# =============================================================================================
landed
run 0 "every completed check run green"
says "12 completed check run(s), all green (12 success)" "a pass reports what it judged"
lacks "::notice::" "nothing ignored, so nothing announced"
lacks "::error::" "a pass reports no error"

# =============================================================================================
# the 2.5.1 block, and its mirror
# =============================================================================================
release_2_5_1 failure success
run 0 "2.5.1 exactly: only release-hygiene.yml's Publish Liveness failed"
says "::notice::release-provenance: ignored 2 check run(s) from $HYGIENE" "the exclusion is announced, with its workflow"
says '"Publish Liveness" (failure)' "the notice names the failure it ignored"
says "13 completed check run(s), all green" "everything else is still judged"
says '"Pack and push to nuget.org" (in_progress)' "runs in flight are listed, not judged"
lacks "::error::" "the ignored failure is not an error"

release_2_5_1 success failure
run 0 "the mirror race: only release-hygiene.yml's ApiCompat Baseline failed"
says '"ApiCompat Baseline" (failure)' "the notice names the mirror failure"

release_2_5_1 failure failure
run 0 "both release-hygiene.yml checks red"

release_2_5_1 success success
run 0 "the 2.5.1 commit once release-hygiene.yml was re-run green"
says '"Publish Liveness" (success)' "green hygiene runs are ignored too, and still announced"

# ...and it hides nothing next to it.
landed
check "$HYGIENE_PUSH" "Publish Liveness" completed failure
check "$AOT_PUSH" "aot-check" completed failure
run 1 "a release-hygiene.yml failure beside a real failure"
errors_name '"aot-check" concluded failure — .github/workflows/aot-validate.yml' "the real failure is named with its workflow"
errors_name "has 1 non-green check run(s) of 13 completed" "the count leaves out the ignored run (twelve landed, plus aot-check)"
no_error_names "Publish Liveness" "the ignored failure is not given as the reason"
says '"Publish Liveness" (failure)' "the ignored failure is still announced"

# =============================================================================================
# by workflow path, never by name
# =============================================================================================
for name in "Publish Liveness" "ApiCompat Baseline"; do
  landed
  workflow "$OTHER" .github/workflows/other.yml
  check "$OTHER" "$name" completed failure
  run 1 "a check called '$name' in another workflow still counts"
  errors_name "\"$name\" concluded failure — .github/workflows/other.yml" "'$name' is named with its own workflow"
  lacks "::notice::" "nothing was ignored for '$name'"
done

landed
workflow "$OTHER" .github/workflows/other.yml
check "$HYGIENE_PUSH" "Publish Liveness" completed failure
check "$OTHER" "Publish Liveness" completed success
run 0 "one name in two suites — red in release-hygiene.yml's, green in the other: judged by suite"

landed
workflow "$OTHER" .github/workflows/other.yml
check "$HYGIENE_PUSH" "Publish Liveness" completed success
check "$OTHER" "Publish Liveness" completed failure
run 1 "one name in two suites — green in release-hygiene.yml's, red in the other: judged by suite"
errors_name '"Publish Liveness" concluded failure — .github/workflows/other.yml' "the red one is named by its workflow"
no_error_names "$HYGIENE" "the green one is not blamed"

# The real duplicate on 019fb697: codeql.yml's checks run in the queue and again on the push.
landed
check "$CODEQL_PUSH" "Analyze (C#)" completed failure
run 1 "a check green in one suite does not cover the same name red in another"
errors_name '"Analyze (C#)" concluded failure — .github/workflows/codeql.yml' "the red suite's run is named"

# Only the exact path. A renamed, moved or re-spelled workflow is not the one ADR-0055 calls a
# notification, and a path format GitHub has not used must fail closed rather than match loosely.
for path in .github/workflows/release-hygiene.yaml .github/workflows/Release-Hygiene.yml \
            .github/workflows/nested/release-hygiene.yml release-hygiene.yml \
            "$HYGIENE@refs/heads/main" " $HYGIENE"; do
  landed
  workflow "$OTHER" "$path"
  check "$OTHER" "Publish Liveness" completed failure
  run 1 "only the exact path is ignored — not '$path'"
done

# A suite no workflow run owns is someone else's check, whatever it is called.
landed
check 424242 "Publish Liveness" completed failure
run 1 "a check suite with no workflow run is not ignored"
errors_name '"Publish Liveness" concluded failure — no workflow run, reported by app github-actions' "it is named as unowned"

landed
workflow "$HYGIENE_PUSH" .github/workflows/other.yml
check "$HYGIENE_PUSH" "Publish Liveness" completed failure
run 1 "a check suite claimed by release-hygiene.yml and another path is not ignored"
errors_name "check suite claimed by several workflows: .github/workflows/other.yml, $HYGIENE" "the ambiguity is named"

landed
workflow "$HYGIENE_PUSH" "$HYGIENE"
check "$HYGIENE_PUSH" "Publish Liveness" completed failure
run 0 "the same workflow run listed twice still owns its suite"

# With no workflow runs to map to, nothing can be ignored.
landed
: > "$RUNS"
run 0 "an empty workflow-runs document ignores nothing and judges everything"
lacks "::notice::" "nothing ignored without workflow runs"
check "$HYGIENE_PUSH" "Publish Liveness" completed failure
run 1 "an empty workflow-runs document does not ignore a release-hygiene.yml failure"
errors_name '"Publish Liveness" concluded failure — no workflow run' "the unmapped failure is named"

# =============================================================================================
# Dependabot's update runs — a GitHub-assigned path, attached to main's HEAD, often cancelled
# =============================================================================================
landed
workflow "$DEPENDABOT_SUITE" "$DEPENDABOT"
check "$DEPENDABOT_SUITE" "Dependabot" completed cancelled
run 0 "a cancelled Dependabot update run on the release commit (v2.5.0's commit carries one)"
says "::notice::release-provenance: ignored 1 check run(s) from $DEPENDABOT" "the Dependabot exclusion is announced, with its path"
says '"Dependabot" (cancelled)' "the notice names the run it ignored"
lacks "::error::" "the ignored cancellation is not an error"

release_2_5_1 failure success
workflow "$DEPENDABOT_SUITE" "$DEPENDABOT"
check "$DEPENDABOT_SUITE" "Dependabot" completed failure
run 0 "both exclusions at once: release-hygiene.yml red and Dependabot red"
says "ignored 2 check run(s) from $HYGIENE" "the hygiene exclusion is announced on its own line"
says "ignored 1 check run(s) from $DEPENDABOT" "the Dependabot exclusion is announced on its own line"

landed
workflow "$DEPENDABOT_SUITE" "$DEPENDABOT"
check "$DEPENDABOT_SUITE" "Dependabot" completed cancelled
check "$AOT_PUSH" "aot-check" completed failure
run 1 "a Dependabot run hides nothing next to it"
errors_name '"aot-check" concluded failure — .github/workflows/aot-validate.yml' "the real failure is named"
no_error_names "Dependabot" "the ignored run is not given as the reason"

fresh
cp "$WORK/suites.json" "$RUNS"
workflow "$DEPENDABOT_SUITE" "$DEPENDABOT"
check "$DEPENDABOT_SUITE" "Dependabot" completed success
check "$PUBLISH" "Pack and push to nuget.org" in_progress
run 1 "only a Dependabot run completed — no evidence, even green"
errors_name "No completed check runs for $SHA outside $DEPENDABOT — nothing proves this commit was ever built." "the evidence error names what was set aside"

# Only that exact GitHub-assigned path: a repository workflow named after Dependabot, a sibling
# dynamic path, and other dynamic workflows (CodeQL's default setup) all still count.
for path in .github/workflows/dependabot-updates.yml dynamic/dependabot/other \
            "$DEPENDABOT/extra" dynamic/github-code-scanning/codeql " $DEPENDABOT"; do
  landed
  workflow "$OTHER" "$path"
  check "$OTHER" "Dependabot" completed cancelled
  run 1 "only Dependabot's exact path is ignored — not '$path'"
done

# =============================================================================================
# only completed runs are evidence — in both directions
# =============================================================================================
for status in queued in_progress waiting requested pending; do
  landed
  check "$AOT_PUSH" "aot-check" "$status"
  run 0 "a $status check run is not evidence against the commit"
  says "\"aot-check\" ($status)" "the $status run is listed as not completed"
done

for conclusion in neutral skipped; do
  landed
  check "$AOT_PUSH" "aot-check" completed "$conclusion"
  run 0 "$conclusion is green"
done

for conclusion in failure cancelled timed_out action_required stale; do
  landed
  check "$AOT_PUSH" "aot-check" completed "$conclusion"
  run 1 "$conclusion is not green"
  errors_name "\"aot-check\" concluded $conclusion" "the $conclusion run is named with its conclusion"
done

landed
check "$AOT_PUSH" "aot-check" completed some_future_conclusion
run 1 "a conclusion this gate has never heard of is not green"

landed
check "$AOT_PUSH" "aot-check" completed
run 1 "a completed check run with no conclusion is not green"
errors_name '"aot-check" completed with no conclusion' "the missing conclusion is named"

# =============================================================================================
# evidence is still required after the exclusion
# =============================================================================================
fresh
cp "$WORK/suites.json" "$RUNS"
check "$HYGIENE_PUSH" "Publish Liveness" completed success
check "$HYGIENE_PUSH" "ApiCompat Baseline" completed success
check "$PUBLISH" "Pack and push to nuget.org" in_progress
run 1 "only release-hygiene.yml's runs completed — no evidence, even all green"
errors_name "No completed check runs for $SHA outside $HYGIENE — nothing proves this commit was ever built." "the evidence error says what was set aside"

fresh
cp "$WORK/suites.json" "$RUNS"
run 1 "no check runs at all"
errors_name "No completed check runs for $SHA — nothing proves this commit was ever built." "the evidence error is kept"

fresh
cp "$WORK/suites.json" "$RUNS"
check "$CI_MQ" "Unit Tests" in_progress
check "$PUBLISH" "Pack and push to nuget.org" queued
run 1 "only runs in flight — no evidence"

fresh
run 1 "both documents empty — no evidence"

# =============================================================================================
# malformed input is "cannot tell" (exit 2) — never a pass, even beside a passing shape
# =============================================================================================
release_2_5_1 failure success
printf '{"name": "Unit Tests", "status": ' >> "$CHECKS"
run 2 "truncated check-runs JSON"
errors_name "is not JSON" "the parse failure is named"

release_2_5_1 failure success
echo 'not json' >> "$RUNS"
run 2 "garbage in the workflow-runs document"

landed
echo '"a string"' >> "$CHECKS"
run 2 "a bare string among the check runs"
errors_name "value #13" "the offending value is located"

landed
echo '{"name": "x", "status": "completed", "conclusion": "success"}' >> "$CHECKS"
run 2 "a check run with no check suite"

landed
echo '{"name": "x", "status": "completed", "conclusion": "success", "check_suite": {"id": "93987543312"}}' >> "$CHECKS"
run 2 "a check suite id that is a string"

landed
echo '{"name": "x", "status": "completed", "conclusion": 1, "check_suite": {"id": 1}}' >> "$CHECKS"
run 2 "a conclusion that is not a string"

landed
echo '{"check_suite": {"id": 1}, "status": "completed", "conclusion": "success"}' >> "$CHECKS"
run 2 "a check run with no name"

landed
echo "{\"check_suite_id\": $HYGIENE_PUSH}" >> "$RUNS"
run 2 "a workflow run with no path"

landed
echo "{\"path\": \"$HYGIENE\"}" >> "$RUNS"
run 2 "a workflow run with no check suite id"

landed
jq -cs '{ total_count: length, check_runs: . }' "$CHECKS" > "$WORK/page.json"
cp "$WORK/page.json" "$CHECKS"
run 2 "a raw check-runs page (--paginate without --jq) is the wrong shape"
errors_name "--jq '.check_runs[]'" "the error says which shape was expected"

landed
jq -cs '{ total_count: length, workflow_runs: . }' "$RUNS" > "$WORK/page.json"
cp "$WORK/page.json" "$RUNS"
run 2 "a raw workflow-runs page is the wrong shape"

landed
jq -cs '.' "$CHECKS" > "$WORK/array.json"
cp "$WORK/array.json" "$CHECKS"
run 2 "one array of check runs (--slurp) is the wrong shape, not guessed at"

landed
cp "$CHECKS" "$WORK/swap.json"; cp "$RUNS" "$CHECKS"; cp "$WORK/swap.json" "$RUNS"
run 2 "the two documents swapped"

landed
run 2 "a missing check-runs file" "$SHA" "$WORK/nope.json" "$RUNS"
run 2 "a missing workflow-runs file" "$SHA" "$CHECKS" "$WORK/nope.json"
run 2 "two arguments instead of three" "$SHA" "$CHECKS"
run 2 "an empty sha" "" "$CHECKS" "$RUNS"

# =============================================================================================
# pagination — a stream of runs, so page boundaries leave no trace
# =============================================================================================
# `gh api --paginate --jq '.check_runs[]'` applies the filter to each page as it arrives and prints
# the runs one JSON value each. Build two pages the size the API returns at per_page=100, the way it
# returns them, apply exactly that per-page filter, and put what matters on page 2.
page() { # page <key> <first> <last> <jq-body-for-one-run> — one API page of runs <first>..<last>
  jq -cn --arg key "$1" --argjson first "$2" --argjson last "$3" \
    "{ total_count: 151, (\$key): [ range(\$first; \$last + 1) | $4 ] }"
}
paginate() { # paginate <key> <page-file>... — what gh --paginate --jq '.<key>[]' writes
  local key="$1"; shift
  for p in "$@"; do jq -c --arg key "$key" '.[$key][]' "$p"; done
}

CHECK_RUN='{ name: "job \(.)", status: "completed", check_suite: { id: (7000 + .) },
             conclusion: (if . == 151 then "failure" else "success" end) }'
page check_runs 1 100 "$CHECK_RUN" > "$WORK/checks-p1.json"
page check_runs 101 151 "$CHECK_RUN" > "$WORK/checks-p2.json"
page workflow_runs 1 100 '{ check_suite_id: (7000 + .), path: ".github/workflows/w\(.).yml" }' > "$WORK/runs-p1.json"
page workflow_runs 101 151 '{ check_suite_id: (7000 + .), path: ".github/workflows/w\(.).yml" }' > "$WORK/runs-p2.json"

fresh
paginate check_runs "$WORK/checks-p1.json" "$WORK/checks-p2.json" > "$CHECKS"
paginate workflow_runs "$WORK/runs-p1.json" "$WORK/runs-p2.json" > "$RUNS"
run 1 "a failure on the second page of check runs is found"
errors_name '"job 151" concluded failure — .github/workflows/w151.yml' "the page-2 failure is named"
errors_name "of 151 completed" "every run on both pages is judged"

page workflow_runs 101 151 \
  "{ check_suite_id: (7000 + .), path: (if . == 151 then \"$HYGIENE\" else \".github/workflows/w\\(.).yml\" end) }" \
  > "$WORK/runs-p2.json"
paginate workflow_runs "$WORK/runs-p1.json" "$WORK/runs-p2.json" > "$RUNS"
run 0 "a release-hygiene.yml run on the second page of workflow runs still owns its suite"
says "150 completed check run(s), all green" "the rest of both pages is judged"
says '"job 151" (failure)' "the page-2 exclusion is announced"

cat "$WORK/checks-p1.json" "$WORK/checks-p2.json" > "$CHECKS"
run 2 "the concatenated raw pages themselves are rejected, not half-read"

# Independent of how gh lays values out: pretty-printed values are the same stream.
release_2_5_1 failure success
jq '.' "$CHECKS" > "$WORK/pretty.json"
cp "$WORK/pretty.json" "$CHECKS"
run 0 "pretty-printed check runs — one value per run, not one per line"
says "13 completed check run(s), all green" "multi-line values are all read"

# =============================================================================================
# a check run's name cannot forge a workflow command
# =============================================================================================
landed
check "$AOT_PUSH" $'aot-check\n::notice::all green, ship it' completed failure
run 1 "a newline inside a check-run name"
if grep -q '^::notice::all green' <<<"$OUT"; then bad "a check-run name started a workflow command of its own"; else ok; fi
says 'aot-check%0A::notice::all green, ship it' "the newline is escaped for the workflow-command parser"

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"
