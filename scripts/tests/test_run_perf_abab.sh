#!/usr/bin/env bash
# test_run_perf_abab.sh — unit tests for scripts/ci/run-perf-abab.sh (the in-job ABAB runner).
#
# `dotnet` and `timeout` are stubbed onto PATH and record every invocation, and a throwaway git
# repository with an annotated tag stands in for HEAD and the reference, so the order of the
# invocations, the filters, the timeout wrapping, the keep-going-after-a-failure loop and the
# manifest are exercised rather than assumed.
#
# Pure bash + git + python3, a few seconds. Runs in the always-run, required `Coverage Script
# Tests` job next to the perf-baseline notifier's tests.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNNER="$SCRIPT_DIR/../ci/run-perf-abab.sh"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

T1="Verbara.Sdk.Benchmarks.FakeBenchmark"
T2="Verbara.Sdk.Benchmarks.OtherBenchmark"

# One repository, two commits: the tag (the reference) and HEAD. HEAD changes one benchmark
# source, one compiled src file and the README of the benchmark project.
REPO="$TMP_ROOT/repo"
mkdir -p "$REPO/Tests/Verbara.Sdk.Benchmarks" "$REPO/src/Lib"
git -C "$REPO" init -q
git -C "$REPO" config user.email t@example.invalid
git -C "$REPO" config user.name t
git -C "$REPO" config commit.gpgsign false
git -C "$REPO" config tag.gpgsign false
echo 'class FakeBenchmark {}' > "$REPO/Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs"
echo 'readme' > "$REPO/Tests/Verbara.Sdk.Benchmarks/README.md"
echo 'class A {}' > "$REPO/src/Lib/A.cs"
echo 'docs' > "$REPO/src/Lib/README.md"
git -C "$REPO" add -A && git -C "$REPO" commit -qm one
git -C "$REPO" tag -a v1.2.3 -m 'release'
git -C "$REPO" tag -a v1.10.0-rc.1 -m 'pre-release'
TAG_COMMIT="$(git -C "$REPO" rev-parse 'v1.2.3^{commit}')"
TAG_OBJECT="$(git -C "$REPO" rev-parse v1.2.3)"
echo 'class FakeBenchmark { int x; }' > "$REPO/Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs"
echo 'readme 2' > "$REPO/Tests/Verbara.Sdk.Benchmarks/README.md"
echo 'class A { int y; }' > "$REPO/src/Lib/A.cs"
echo 'docs 2' > "$REPO/src/Lib/README.md"
git -C "$REPO" add -A && git -C "$REPO" commit -qm two
git -C "$REPO" tag -a v1.3.0 -m 'newer'
git -C "$REPO" remote add origin "$REPO"
HEAD_COMMIT="$(git -C "$REPO" rev-parse HEAD)"
REF_DIR="$TMP_ROOT/perf-ref"
git -C "$REPO" worktree add -q --detach "$REF_DIR" "$TAG_COMMIT"

BASELINE="$TMP_ROOT/baseline.json"
cat > "$BASELINE" <<JSON
{"sources": {"_comment": "x", "fake": "$T1", "other": "$T2"},
 "abab": {"reference_tag": "v1.2.3", "order": ["R", "H", "H", "R"], "invocation_timeout_s": 900,
          "launch_count": 2}}
JSON

setup() {
  WORK="$(mktemp -d -p "$TMP_ROOT")"
  BIN="$WORK/bin"; mkdir -p "$BIN"
  CALLS="$WORK/calls.log"; : > "$CALLS"
  OUT="$WORK/out"
  cat > "$BIN/dotnet" <<'STUB'
#!/usr/bin/env bash
printf 'dotnet cwd=%s args=%s\n' "$PWD" "$*" >> "$CALLS"
artifacts=""; list=0; prev=""
for a in "$@"; do
  [ "$prev" = "--artifacts" ] && artifacts="$a"
  [ "$a" = "--list" ] && list=1
  prev="$a"
done
if [ "$list" = 1 ]; then
  echo "Verbara.Sdk.Benchmarks.FakeBenchmark.Alpha"
  exit 0
fi
label="$(basename "$artifacts")"
mkdir -p "$artifacts/results"
if [ -n "${FAKE_FAIL_AT:-}" ] && [ "$label" = "$FAKE_FAIL_AT" ]; then exit "${FAKE_FAIL_RC:-1}"; fi
echo '{}' > "$artifacts/results/x-report-full-compressed.json"
exit 0
STUB
  cat > "$BIN/timeout" <<'STUB'
#!/usr/bin/env bash
opts=()
while [ "${1#--}" != "$1" ]; do opts+=("$1"); shift; done
duration="$1"; shift
printf 'timeout opts=%s duration=%s cmd=%s\n' "${opts[*]}" "$duration" "$1" >> "$CALLS"
"$@"
STUB
  chmod +x "$BIN/dotnet" "$BIN/timeout"
  export CALLS
  unset FAKE_FAIL_AT FAKE_FAIL_RC PERF_ABAB_DRY_RUN PERF_ABAB_INVOCATION_TIMEOUT_S
}

run_abab() { # run_abab [extra args...] -> RC, LOG
  LOG="$(PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$BASELINE" --head "$REPO" \
         --reference "$REF_DIR" --reference-tag v1.2.3 --out "$OUT" "$@" 2>&1)"
  RC=$?
}

manifest() { python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2]))" "$OUT/manifest.json" "$1"; }

[ -x "$RUNNER" ] && ok || bad "runner must be committed executable (mode 100755)"

# --- S1: the dry run prints two lists and the four invocations in the declared order ---
setup; PERF_ABAB_DRY_RUN=1 run_abab
[ "$RC" -eq 0 ] && ok || bad "dry run should exit 0 (got $RC): $LOG"
plan="$(printf '%s\n' "$LOG" | command grep -E '^(list|run) ' | awk '{print $1, $2, $3}' | tr '\n' ';')"
[ "$plan" = "list R $REF_DIR;list H $REPO;run 1 R;run 2 H;run 3 H;run 4 R;" ] && ok \
  || bad "dry-run plan should be lists R,H then R H H R, got: $plan"
[ -s "$CALLS" ] && bad "dry run must not call dotnet" || ok

# --- S1 (real run): the same order reaches dotnet, each in its arm's benchmark directory ---
setup; run_abab
[ "$RC" -eq 0 ] && ok || bad "run should exit 0 (got $RC): $LOG"
dirs="$(command grep '^dotnet' "$CALLS" | sed -E 's/^dotnet cwd=([^ ]*) .*/\1/' | tr '\n' ';')"
exp="$REF_DIR/Tests/Verbara.Sdk.Benchmarks;$REPO/Tests/Verbara.Sdk.Benchmarks;$REF_DIR/Tests/Verbara.Sdk.Benchmarks;$REPO/Tests/Verbara.Sdk.Benchmarks;$REPO/Tests/Verbara.Sdk.Benchmarks;$REF_DIR/Tests/Verbara.Sdk.Benchmarks;"
[ "$dirs" = "$exp" ] && ok || bad "dotnet directories should be R H (lists) then R H H R, got: $dirs"
arts="$(command grep -o -- '--artifacts [^ ]*' "$CALLS" | awk '{print $2}' | xargs -n1 basename | tr '\n' ';')"
[ "$arts" = "1-R;2-H;3-H;4-R;" ] && ok || bad "artifacts directories should be 1-R 2-H 3-H 4-R, got: $arts"
[ "$(manifest 'd["order"]')" = "['R', 'H', 'H', 'R']" ] && ok || bad "manifest order"
[ "$(manifest '[(i["position"], i["arm"], i["dir"]) for i in d["invocations"]]')" \
  = "[(1, 'R', '1-R'), (2, 'H', '2-H'), (3, 'H', '3-H'), (4, 'R', '4-R')]" ] && ok \
  || bad "manifest invocations should follow the order"
[ "$(head -1 "$OUT/list-R.txt")" = "Verbara.Sdk.Benchmarks.FakeBenchmark.Alpha" ] && ok \
  || bad "list-R.txt should hold the --list flat output"
[ -s "$OUT/list-H.txt" ] && ok || bad "list-H.txt should be written"

# --- S1: the order comes from the baseline, not from the script ---
setup
ORDER6="$TMP_ROOT/baseline6.json"
sed 's/\["R", "H", "H", "R"\]/["R", "H", "H", "R", "R", "H"]/' "$BASELINE" > "$ORDER6"
LOG="$(PATH="$BIN:$PATH" PERF_ABAB_DRY_RUN=1 bash "$RUNNER" --baseline "$ORDER6" --head "$REPO" \
       --reference "$REF_DIR" --reference-tag v1.2.3 --out "$OUT" 2>&1)"
plan="$(printf '%s\n' "$LOG" | command grep -E '^run ' | awk '{print $3}' | tr -d '\n')"
[ "$plan" = "RHHRRH" ] && ok || bad "a declared six-position order should run RHHRRH, got: $plan"

# --- S1 (real run, three rounds): six invocations reach dotnet and the manifest lists six ---
setup
LOG="$(PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$ORDER6" --head "$REPO" \
       --reference "$REF_DIR" --reference-tag v1.2.3 --out "$OUT" 2>&1)"; RC=$?
[ "$RC" -eq 0 ] && ok || bad "a six-position run should exit 0 (got $RC): $LOG"
arts="$(command grep -o -- '--artifacts [^ ]*' "$CALLS" | awk '{print $2}' | xargs -n1 basename | tr '\n' ';')"
[ "$arts" = "1-R;2-H;3-H;4-R;5-R;6-H;" ] && ok \
  || bad "six-position artifacts directories should be 1-R 2-H 3-H 4-R 5-R 6-H, got: $arts"
runs="$(command grep -- '--exporters json' "$CALLS" | sed -E 's/^dotnet cwd=([^ ]*) .*/\1/' \
        | sed -e "s#^$REF_DIR/.*#R#" -e "s#^$REPO/.*#H#" | tr -d '\n')"
[ "$runs" = "RHHRRH" ] && ok || bad "each of the six invocations runs in its arm's tree, got: $runs"
[ "$(command grep -c '^timeout .*duration=900 cmd=dotnet' "$CALLS")" -eq 6 ] && ok \
  || bad "six invocations under timeout 900"
[ "$(manifest 'd["order"]')" = "['R', 'H', 'H', 'R', 'R', 'H']" ] && ok || bad "six-position manifest order"
[ "$(manifest '[(i["position"], i["arm"], i["dir"]) for i in d["invocations"]]')" \
  = "[(1, 'R', '1-R'), (2, 'H', '2-H'), (3, 'H', '3-H'), (4, 'R', '4-R'), (5, 'R', '5-R'), (6, 'H', '6-H')]" ] \
  && ok || bad "manifest should list six invocations in the declared order: $(manifest 'd["invocations"]')"
[ "$(manifest 'all(i["exit_code"] == 0 and not i["timed_out"] for i in d["invocations"])')" = "True" ] \
  && ok || bad "six clean invocations recorded"

# --- S2: one exact glob per sources type, and the same filters for both arms ---
setup; run_abab
filters="$(command grep '^dotnet' "$CALLS" | sed -E 's/.*--filter (.*)$/\1/' | sed -E 's/ --exporters.*//' | sort -u)"
[ "$filters" = "$T1.* $T2.*" ] && ok || bad "every call should filter on exactly '$T1.* $T2.*', got: $filters"
command grep -q '_comment' "$CALLS" && bad "an underscore key of sources must not become a filter" || ok
[ "$(command grep -c -- '--exporters json' "$CALLS")" -eq 4 ] && ok || bad "four invocations export json"
[ "$(command grep -c -- '--list flat' "$CALLS")" -eq 2 ] && ok || bad "two --list flat calls"
[ "$(command grep -c -- '-c Release --no-build' "$CALLS")" -eq 6 ] && ok || bad "every call is Release --no-build"

# --- S3: every invocation is wrapped in timeout with the declared seconds ---
[ "$(command grep -c '^timeout .*duration=900 cmd=dotnet' "$CALLS")" -eq 4 ] && ok \
  || bad "four invocations under timeout 900: $(cat "$CALLS")"
command grep -q '^timeout opts=--kill-after=30 ' "$CALLS" && ok || bad "timeout must carry --kill-after"
setup; PERF_ABAB_INVOCATION_TIMEOUT_S=60 run_abab
[ "$(command grep -c '^timeout .*duration=60 cmd=dotnet' "$CALLS")" -eq 4 ] && ok \
  || bad "the environment override should set the invocation timeout"
setup; PERF_ABAB_INVOCATION_TIMEOUT_S=abc run_abab
[ "$RC" -eq 2 ] && ok || bad "a non-integer timeout is bad usage (got $RC)"

# --- S4: a failing or timed-out position does not stop the later ones ---
for rc in 1 124; do
  setup; FAKE_FAIL_AT=2-H FAKE_FAIL_RC=$rc run_abab
  [ "$RC" -eq 0 ] && ok || bad "a failing invocation (rc $rc) still exits 0 (got $RC)"
  [ "$(command grep -c -- '--exporters json' "$CALLS")" -eq 4 ] && ok \
    || bad "positions 3 and 4 must still run after position 2 exits $rc"
  [ "$(manifest 'd["invocations"][1]["exit_code"]')" = "$rc" ] && ok || bad "exit code $rc recorded"
  want="False"; [ "$rc" -eq 124 ] && want="True"
  [ "$(manifest 'd["invocations"][1]["timed_out"]')" = "$want" ] && ok || bad "timed_out for rc $rc"
  [ "$(manifest 'd["invocations"][2]["exit_code"]')" = "0" ] && ok || bad "position 3 ran clean"
done
printf '%s\n' "$LOG" | command grep -q '::group::' && ok || bad "invocations are wrapped in ::group::"

# --- S5: the manifest records the commits and the changed paths ---
setup; run_abab
[ "$(manifest 'd["reference_sha"]')" = "$TAG_COMMIT" ] && ok || bad "reference_sha is the tag's commit"
[ "$(manifest 'd["reference_sha"]')" != "$TAG_OBJECT" ] && ok || bad "reference_sha must not be the tag object"
[ "$(manifest 'd["reference_dir_sha"]')" = "$TAG_COMMIT" ] && ok || bad "reference_dir_sha read from the reference directory"
[ "$(manifest 'd["head_sha"]')" = "$HEAD_COMMIT" ] && ok || bad "head_sha"
[ "$(manifest 'd["reference_tag"]')" = "v1.2.3" ] && ok || bad "reference_tag"
[ "$(manifest 'd["schema"]')" = "1" ] && ok || bad "schema 1"
[ "$(manifest 'd["changed_benchmark_paths"]')" \
  = "['Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs', 'Tests/Verbara.Sdk.Benchmarks/README.md']" ] && ok \
  || bad "changed_benchmark_paths: $(manifest 'd["changed_benchmark_paths"]')"
[ "$(manifest 'd["changed_compiled_paths"]')" \
  = "['Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs', 'src/Lib/A.cs']" ] && ok \
  || bad "changed_compiled_paths: $(manifest 'd["changed_compiled_paths"]')"
[ "$(manifest 'd["newest_tag"]')" = "v1.3.0" ] && ok || bad "newest_tag ignores pre-releases: $(manifest 'd["newest_tag"]')"
[ "$(manifest 'd["lists"]["R"]["exit_code"]')" = "0" ] && ok || bad "list exit codes recorded"

# --- S5: an unknown tag still yields a manifest, with nulls, and exit 0 ---
setup
LOG="$(PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$BASELINE" --head "$REPO" \
       --reference "$TMP_ROOT/missing" --reference-tag v1.2.3 --out "$OUT" 2>&1)"; RC=$?
[ "$RC" -eq 0 ] && ok || bad "a missing reference directory still exits 0 (got $RC)"
[ "$(manifest 'd["reference_dir_sha"]')" = "None" ] && ok || bad "reference_dir_sha null when the directory is missing"
[ "$(manifest 'd["invocations"][0]["exit_code"] != 0')" = "True" ] && ok || bad "a missing directory records a failed invocation"

# --- S7: the declared launch count reaches every in-job invocation, and nothing else ---
setup; PERF_ABAB_DRY_RUN=1 run_abab
[ "$(printf '%s\n' "$LOG" | command grep -c '^run .* launchCount=2 ')" -eq 4 ] && ok \
  || bad "the dry run should print launchCount=2 on each of the four positions: $LOG"
setup; run_abab
[ "$RC" -eq 0 ] && ok || bad "a run with launch_count 2 should exit 0 (got $RC): $LOG"
[ "$(command grep -- '--exporters json' "$CALLS" | command grep -c -- ' --launchCount 2 ')" -eq 4 ] && ok \
  || bad "each of the four invocations should carry --launchCount 2: $(cat "$CALLS")"
[ "$(command grep -- '--exporters json' "$CALLS" | command grep -o -- '--launchCount' | wc -l)" -eq 4 ] && ok \
  || bad "--launchCount should appear once per invocation"
command grep -- '--list flat' "$CALLS" | command grep -q -- '--launchCount' \
  && bad "the --list calls must not carry --launchCount" || ok
[ "$(manifest 'd["launch_count"]')" = "2" ] && ok || bad "the manifest should record launch_count 2"
ONE="$TMP_ROOT/baseline-one.json"
sed 's/"launch_count": 2/"launch_count": 1/' "$BASELINE" > "$ONE"
setup
LOG="$(PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$ONE" --head "$REPO" \
       --reference "$REF_DIR" --reference-tag v1.2.3 --out "$OUT" 2>&1)"; RC=$?
[ "$RC" -eq 0 ] && ok || bad "a run with launch_count 1 should exit 0 (got $RC)"
[ "$(command grep -- '--exporters json' "$CALLS" | command grep -c -- ' --launchCount 1 ')" -eq 4 ] && ok \
  || bad "launch_count 1 should pass --launchCount 1, read from the field: $(cat "$CALLS")"
[ "$(manifest 'd["launch_count"]')" = "1" ] && ok || bad "the manifest should record launch_count 1"

# --- S7: a missing or invalid launch count fails closed, before anything runs ---
for value in MISSING 0 -1 '"2"' 2.0 2.5 true null; do
  BAD="$TMP_ROOT/baseline-launch.json"
  python3 - "$BASELINE" "$BAD" "$value" <<'PY2'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
if sys.argv[3] == "MISSING":
    del doc["abab"]["launch_count"]
else:
    doc["abab"]["launch_count"] = json.loads(sys.argv[3])
json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"))
PY2
  setup
  PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$BAD" --head "$REPO" --reference "$REF_DIR" \
    --reference-tag v1.2.3 --out "$OUT" > /dev/null 2>&1; RC=$?
  [ "$RC" -eq 2 ] && ok || bad "launch_count $value should exit 2 (got $RC)"
  [ -s "$CALLS" ] && bad "launch_count $value must run nothing: $(cat "$CALLS")" || ok
done

# --- S6: bad usage exits 2 ---
setup
for args in "" "--baseline $BASELINE" "--bogus x" \
            "--baseline $TMP_ROOT/none.json --head $REPO --reference $REF_DIR --reference-tag v1.2.3 --out $OUT" \
            "--baseline $BASELINE --head $REPO --reference $REF_DIR --reference-tag v9.9.9 --out $OUT"; do
  # shellcheck disable=SC2086
  PATH="$BIN:$PATH" bash "$RUNNER" $args > /dev/null 2>&1; RC=$?
  [ "$RC" -eq 2 ] && ok || bad "bad usage '$args' should exit 2 (got $RC)"
done
NOABAB="$TMP_ROOT/noabab.json"; echo '{"sources": {"fake": "X.Y"}}' > "$NOABAB"
PATH="$BIN:$PATH" bash "$RUNNER" --baseline "$NOABAB" --head "$REPO" --reference "$REF_DIR" \
  --reference-tag v1.2.3 --out "$OUT" > /dev/null 2>&1; RC=$?
[ "$RC" -eq 2 ] && ok || bad "a baseline with no abab block should exit 2 (got $RC)"

echo "run-perf-abab: $pass passed, $fails failed"
[ "$fails" -eq 0 ] || exit 1
