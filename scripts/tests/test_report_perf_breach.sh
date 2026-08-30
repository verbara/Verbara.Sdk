#!/usr/bin/env bash
# test_report_perf_breach.sh — unit tests for scripts/ci/report-perf-breach.sh (ADR-0042 D5).
#
# `gh` is stubbed onto PATH and records every invocation, so the find-then-create-or-comment
# branching is actually exercised rather than assumed. The notifier is the ONLY signal a breach
# happened while the gate is observing (openspec `enforce-unguarded-public-claims` §2.5), so its
# silent-failure paths are what these cases mostly pin.
#
# Pure bash, ~1s. Runs in the ALWAYS-RUN, REQUIRED `Coverage Script Tests` job.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NOTIFY="$SCRIPT_DIR/../ci/report-perf-breach.sh"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

setup() {
  WORK="$(mktemp -d)"
  BIN="$WORK/bin"; mkdir -p "$BIN"
  GH_LOG="$WORK/gh.log"; : > "$GH_LOG"
  BODY="$WORK/report.md"
  printf '## Perf regression\n\n- Alpha SLOWER by 40%%\n' > "$BODY"
  cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GH_LOG"
# Capture whatever body was handed to gh so the composition can be asserted.
prev=""
for a in "$@"; do
  [ "$prev" = "--body-file" ] && cp "$a" "$GH_CAPTURED_BODY"
  prev="$a"
done
case "$1 $2" in
  "issue list")
    [ "${GH_FAIL_LIST:-0}" = "1" ] && exit 1
    [ -n "${GH_EXISTING:-}" ] && echo "$GH_EXISTING"
    exit 0 ;;
  "label list")
    [ "${GH_LABEL:-0}" = "1" ] && { echo 1; exit 0; }
    echo 0; exit 0 ;;
  "issue comment")
    [ "${GH_FAIL_COMMENT:-0}" = "1" ] && exit 1
    echo "https://example.invalid/issues/${3}#comment"; exit 0 ;;
  "issue create")
    [ "${GH_FAIL_CREATE:-0}" = "1" ] && exit 1
    echo "https://example.invalid/issues/7"; exit 0 ;;
esac
exit 0
STUB
  chmod +x "$BIN/gh"
  export GH_LOG
  GH_CAPTURED_BODY="$WORK/captured-body.md"; export GH_CAPTURED_BODY
  unset GH_EXISTING GH_LABEL GH_FAIL_LIST GH_FAIL_COMMENT GH_FAIL_CREATE PERF_GATE_ENFORCE
  export GITHUB_REPOSITORY="verbara/Verbara.Sdk"
  export PERF_BREACH_ISSUE_TITLE="Perf regression: benchmark outside its baseline band"
}

run_notify() { # run_notify [body-path] -> sets RC / OUT
  local body="${1:-$BODY}"
  OUT="$(PATH="$BIN:$PATH" bash "$NOTIFY" "$body" 2>&1)"
  RC=$?
}

# --- contract ---
[ -x "$NOTIFY" ] && ok || bad "notifier must be committed executable (mode 100755)"

# --- files a new issue when none is open ---
setup; run_notify
[ "$RC" -eq 0 ] && ok || bad "create path should exit 0 (got $RC): $OUT"
grep -q '^issue create' "$GH_LOG" && ok || bad "should have called 'gh issue create'"
grep -q '^issue comment' "$GH_LOG" && bad "must not comment when no issue is open" || ok

# --- reuses the open issue rather than filing a duplicate every Sunday ---
setup; GH_EXISTING=42 run_notify
[ "$RC" -eq 0 ] && ok || bad "comment path should exit 0 (got $RC): $OUT"
grep -q '^issue comment 42' "$GH_LOG" && ok || bad "should have commented on #42"
grep -q '^issue create' "$GH_LOG" && bad "must not create a duplicate issue" || ok

# --- the body carries the report AND the re-baseline remedy (D6) ---
setup; run_notify
grep -q 'Alpha SLOWER by 40%' "$GH_CAPTURED_BODY" && ok || bad "body must include the report"
grep -q 'baseline.README.md' "$GH_CAPTURED_BODY" && ok || bad "body must point at the protocol"
grep -q 'CI never writes back' "$GH_CAPTURED_BODY" && ok || bad "body must state D6 (no write-back)"

# --- links back to the run when Actions supplies the coordinates ---
setup
OUT="$(PATH="$BIN:$PATH" GITHUB_SERVER_URL=https://github.com GITHUB_RUN_ID=99 \
       bash "$NOTIFY" "$BODY" 2>&1)"
grep -q 'actions/runs/99' "$GH_CAPTURED_BODY" && ok || bad "body must link the run when RUN_ID is set"

# --- the triage label is attached only when it exists (an unknown label hard-fails gh create) ---
setup; GH_LABEL=1 run_notify
grep -q '^issue create .*--label performance' "$GH_LOG" && ok || bad "should attach an existing label"
setup; GH_LABEL=0 run_notify
grep -q '^issue create .*--label' "$GH_LOG" && bad "must not attach a nonexistent label" || ok

# --- observing vs enforcing: the SAME flag that arms the comparison arms this (§2.5) ---
setup; run_notify "$WORK/does-not-exist.md"
[ "$RC" -eq 0 ] && ok || bad "missing body in observing mode must not fail the job (got $RC)"
case "$OUT" in *"::warning::"*) ok ;; *) bad "missing body should warn: $OUT" ;; esac

setup; PERF_GATE_ENFORCE=true run_notify "$WORK/does-not-exist.md"
[ "$RC" -eq 1 ] && ok || bad "missing body in enforcing mode must fail (got $RC)"
case "$OUT" in *"::error::"*) ok ;; *) bad "missing body should error when enforcing: $OUT" ;; esac

setup; : > "$WORK/empty.md"; run_notify "$WORK/empty.md"
[ "$RC" -eq 0 ] && ok || bad "empty body observing should exit 0"
grep -q '^issue create' "$GH_LOG" && bad "must refuse to file a blank issue" || ok

setup; run_notify ""
[ "$RC" -eq 0 ] && ok || bad "no argument, observing, should exit 0 with a warning"

# --- gh unavailable or failing ---
setup
rm -f "$BIN/gh"
for tool in mktemp cat rm grep tr; do ln -sf "$(command -v "$tool")" "$BIN/$tool"; done
OUT="$(PATH="$BIN" "$BASH" "$NOTIFY" "$BODY" 2>&1)"; RC=$?
[ "$RC" -eq 0 ] && ok || bad "gh absent, observing, should exit 0 (got $RC): $OUT"
OUT="$(PATH="$BIN" PERF_GATE_ENFORCE=true "$BASH" "$NOTIFY" "$BODY" 2>&1)"; RC=$?
[ "$RC" -eq 1 ] && ok || bad "gh absent, enforcing, should exit 1 (got $RC): $OUT"

setup; GH_FAIL_LIST=1 run_notify
[ "$RC" -eq 0 ] && ok || bad "list failure, observing, should exit 0 (got $RC)"
grep -q '^issue create' "$GH_LOG" && bad "must not create after a failed lookup (duplicate risk)" || ok
setup; GH_FAIL_LIST=1 PERF_GATE_ENFORCE=true run_notify
[ "$RC" -eq 1 ] && ok || bad "list failure, enforcing, should exit 1 (got $RC)"

setup; GH_FAIL_CREATE=1 PERF_GATE_ENFORCE=true run_notify
[ "$RC" -eq 1 ] && ok || bad "create failure, enforcing, should exit 1 (got $RC)"
setup; GH_EXISTING=42 GH_FAIL_COMMENT=1 PERF_GATE_ENFORCE=true run_notify
[ "$RC" -eq 1 ] && ok || bad "comment failure, enforcing, should exit 1 (got $RC)"

echo "report-perf-breach: $pass passed, $fails failed"
[ "$fails" -eq 0 ] || exit 1
