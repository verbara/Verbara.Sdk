#!/usr/bin/env bash
# test_public_api_guard_fires.sh — unit tests for scripts/ci/check-public-api-guard-fires.sh, the negative control
# for the undeclared-public-API diagnostic (RS0016).
#
# The control edits a tracked source file on every pull request, so two things get a case each, in both directions:
# the verdict (exit 0 only on `error RS0016` naming the probe, never on a red build alone) and the restore (the file
# is byte for byte what it was, on every exit path — a stand-in killed mid-build and the script itself interrupted
# with INT and with TERM included — and the set of files git reports is the same, not merely empty).
#
# Each case is a stand-in checkout (a git repository with the probe's project and source file) and a stand-in
# `dotnet` that records what it was asked and what the file held when it was asked, then prints and exits as the
# case says. Pure bash + git + coreutils `timeout`, no .NET, a few seconds. Runs in the always-run
# `Coverage Script Tests` job; the real control runs in `Pack Warnings Gate`. The last cases read ci.yml: the control
# is the last step of that job, and this harness runs in Coverage Script Tests.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUARD="$SCRIPT_DIR/../ci/check-public-api-guard-fires.sh"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

CLEANUP=()
trap 'rm -rf "${CLEANUP[@]}"' EXIT
new_dir() { REPLY="$(mktemp -d)"; CLEANUP+=("$REPLY"); } # sets $REPLY; no subshell, so the cleanup list survives

GIT=(git -c user.name=harness -c user.email=harness@invalid -c init.defaultBranch=main -c commit.gpgsign=false)
REL="src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs"
PROBE="    public int MainSessionProbe() => GetHashCode();"

# A stand-in for `dotnet build <project> ...`. It appends its arguments to $FAKE_STATE/args and copies the source file
# as it found it to $FAKE_STATE/seen, then: FAKE_MODE=sleep sleeps 20 s (to be interrupted) and writes
# $FAKE_STATE/completed if it ever wakes; =kill kills itself with SIGKILL; otherwise prints $FAKE_OUT and exits $FAKE_RC.
new_dir; FAKE="$REPLY/dotnet"
cat > "$FAKE" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_STATE/args"
cp -- src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs "$FAKE_STATE/seen"
case "${FAKE_MODE:-}" in
  sleep) sleep 20; touch "$FAKE_STATE/completed"; exit 0 ;;
  kill)  kill -KILL $$ ;;
esac
[ -z "${FAKE_OUT:-}" ] || printf '%s\n' "$FAKE_OUT"
exit "${FAKE_RC:-0}"
EOF
chmod +x "$FAKE"

ORIGINAL='using System;

namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager
{
    public int Count => 0;
}'

# new_repo [content] — a committed checkout holding the probe's project and source file (default content above,
# ending in a newline). Sets WORK and STATE (the stand-in's record directory).
new_repo() {
  new_dir; WORK="$REPLY"
  new_dir; STATE="$REPLY"
  mkdir -p "$WORK/src/Verbara.Sdk.Sessions/Manager"
  echo '<Project Sdk="Microsoft.NET.Sdk" />' > "$WORK/src/Verbara.Sdk.Sessions/Verbara.Sdk.Sessions.csproj"
  printf '%s\n' "${1:-$ORIGINAL}" > "$WORK/$REL"
  ( cd "$WORK" && "${GIT[@]}" init -q && "${GIT[@]}" add -A && "${GIT[@]}" commit -q -m fixture )
}

snapshot() { # snapshot <name> — the file's sha256 and git's view of the tree, into $STATE/<name>.{sha,status}
  ( cd "$WORK" && sha256sum -- "$REL" > "$STATE/$1.sha" \
      && git status --porcelain=v1 --untracked-files=all > "$STATE/$1.status" )
}

# run <expected rc|nonzero> <description> [VAR=value ...] — the guard in $WORK with the stand-in, then the restore
# check: the file and git's view must be what they were before the run.
run() {
  local want="$1" desc="$2"; shift 2
  snapshot before
  local rc=0
  ( cd "$WORK" && env DOTNET_BIN="$FAKE" FAKE_STATE="$STATE" "$@" bash "$GUARD" ) >"$STATE/out" 2>&1 || rc=$?
  check_rc "$want" "$rc" "$desc"
  check_restored "$desc"
}

check_rc() {
  if [ "$1" = nonzero ]; then
    [ "$2" -ne 0 ] && ok || { bad "$3: exit $2, wanted non-zero"; sed 's/^/    /' "$STATE/out"; }
  else
    [ "$2" -eq "$1" ] && ok || { bad "$3: exit $2, wanted $1"; sed 's/^/    /' "$STATE/out"; }
  fi
}

check_restored() {
  snapshot after
  cmp -s "$STATE/before.sha" "$STATE/after.sha" && ok || bad "$1: the file was not restored byte for byte"
  cmp -s "$STATE/before.status" "$STATE/after.status" && ok || {
    bad "$1: git status changed"; diff "$STATE/before.status" "$STATE/after.status" | sed 's/^/    /' || true; }
}

expect_out() { grep -qF -- "$1" "$STATE/out" && ok || { bad "$2: output lacks '$1'"; sed 's/^/    /' "$STATE/out"; }; }
expect_not_out() { ! grep -qF -- "$1" "$STATE/out" && ok || bad "$2: output carries '$1'"; }
built()     { [ -f "$STATE/args" ] && ok || bad "$1: the build never ran"; }
not_built() { [ ! -f "$STATE/args" ] && ok || bad "$1: the build ran although the script should have refused"; }

RS0016_PROBE="/w/$REL(8,16): error RS0016: Symbol 'Verbara.Sdk.Sessions.Manager.CallSessionManager.MainSessionProbe() -> int' is not part of the declared public API (https://x) [/w/src/Verbara.Sdk.Sessions/Verbara.Sdk.Sessions.csproj]"

# --- the verdict -------------------------------------------------------------------------------------------------

new_repo
run 0 "RS0016 naming the probe" FAKE_RC=1 "FAKE_OUT=$RS0016_PROBE"
expect_out "PROVEN" "RS0016 naming the probe"
# What the stand-in was handed: the committed configuration, one project, Release, and the probe in place.
args="$(cat "$STATE/args")"
case "$args" in
  "build src/Verbara.Sdk.Sessions/Verbara.Sdk.Sessions.csproj -c Release"*) ok ;;
  *) bad "the build was not 'build <the probe's project> -c Release': $args" ;;
esac
case "$args" in *NoWarn*|*TreatWarningsAsErrors*|*WarningsAsErrors*|*-warnaserror*) \
  bad "the build overrode the committed configuration: $args" ;; *) ok ;; esac
# The probe sat right before the class's closing brace, and nowhere else changed.
expected_seen="$(printf '%s\n' "$ORIGINAL" | head -n -1; printf '%s\n}\n' "$PROBE")"
[ "$(cat "$STATE/seen")" = "$expected_seen" ] && ok || {
  bad "the build did not see the probe inserted before the last '}'"; diff <(echo "$expected_seen") "$STATE/seen" | sed 's/^/    /' || true; }
expect_out "probe inserted at $REL:8:" "the probe's line is reported"

new_repo
run 1 "CA1822 instead of RS0016" FAKE_RC=1 "FAKE_OUT=/w/$REL(8,16): error CA1822: Member 'MainSessionProbe' does not access instance data and can be marked as static [/w/p.csproj]"
expect_out "red, but not with RS0016" "CA1822 instead of RS0016"
expect_out "error CA1822" "CA1822 is named"

new_repo
run 1 "RS0017 (a stale record entry) instead of RS0016" FAKE_RC=1 "FAKE_OUT=/w/PublicAPI.Shipped.txt(3,1): error RS0017: Symbol 'Gone()' is part of the declared API, but is either not public or could not be found [/w/p.csproj]"
expect_out "red, but not with RS0016" "RS0017 instead of RS0016"

new_repo
run 1 "a green build" FAKE_RC=0 FAKE_OUT="Build succeeded."
expect_out "the build was green" "a green build"
expect_not_out "PROVEN" "a green build"

new_repo
run 1 "the probe did not compile" FAKE_RC=1 "FAKE_OUT=/w/$REL(8,35): error CS0103: The name 'GetHashCodeX' does not exist in the current context [/w/p.csproj]"
expect_out "the probe did not compile" "the probe did not compile"

new_repo
run 1 "a compile error elsewhere is a red build, not the probe" FAKE_RC=1 "FAKE_OUT=/w/$REL(3,1): error CS1002: ; expected [/w/p.csproj]"
expect_out "red, but not with RS0016" "a compile error elsewhere"

new_repo
run 1 "RS0016 naming another member" FAKE_RC=1 "FAKE_OUT=/w/$REL(5,16): error RS0016: Symbol 'Verbara.Sdk.Sessions.Manager.CallSessionManager.Other() -> int' is not part of the declared public API [/w/p.csproj]"
expect_out "red, but not with RS0016 naming the probe" "RS0016 naming another member"

new_repo
run 1 "RS0016 demoted to a warning" FAKE_RC=0 "FAKE_OUT=/w/$REL(8,16): warning RS0016: Symbol 'Verbara.Sdk.Sessions.Manager.CallSessionManager.MainSessionProbe() -> int' is not part of the declared public API [/w/p.csproj]"
expect_out "the build was green" "RS0016 demoted to a warning"

new_repo
run 1 "a red build carrying RS0016 for the probe only as a warning" FAKE_RC=1 "FAKE_OUT=/w/$REL(8,16): warning RS0016: Symbol 'Verbara.Sdk.Sessions.Manager.CallSessionManager.MainSessionProbe() -> int' is not part of the declared public API [/w/p.csproj]
/w/$REL(3,1): error CS1002: ; expected [/w/p.csproj]"
expect_not_out "PROVEN" "RS0016 only as a warning on a red build"

new_repo
run 1 "RS0016 naming the probe, but the build exited 0" FAKE_RC=0 "FAKE_OUT=$RS0016_PROBE"
expect_not_out "PROVEN" "RS0016 with exit 0"

new_repo
snapshot before; rc=0
( cd "$WORK" && env DOTNET_BIN="$STATE/no-such-dotnet" bash "$GUARD" ) >"$STATE/out" 2>&1 || rc=$?
check_rc 1 "$rc" "the build tool is missing"; check_restored "the build tool is missing"

# --- refusing to start -------------------------------------------------------------------------------------------

new_repo "$(printf '%s\n' "$ORIGINAL" | head -n -1; printf '%s\n}' "$PROBE")"
run 2 "the probe is already present"
expect_out "already in $REL" "the probe is already present"
expect_out "MainSessionProbe" "the leftover is named"
not_built "the probe is already present"

new_repo "$(printf '%s\n}\n// trailing comment' "$(printf '%s\n' "$ORIGINAL" | head -n -1)")"
run 1 "the last line is not '}'"
expect_out "not proven: probe not placed" "the last line is not '}'"
not_built "the last line is not '}'"

new_repo
rm -- "$WORK/$REL"; ( cd "$WORK" && "${GIT[@]}" commit -q -am "no file" )
rc=0; ( cd "$WORK" && env DOTNET_BIN="$FAKE" FAKE_STATE="$STATE" bash "$GUARD" ) >"$STATE/out" 2>&1 || rc=$?
check_rc 2 "$rc" "the probe's file is missing"; not_built "the probe's file is missing"

# --- the restore -------------------------------------------------------------------------------------------------

# An uncommitted edit to the same file survives: restored to the edited content, git still reports it modified.
new_repo
( cd "$WORK" && sed -i 's/=> 0;/=> 42; \/\/ uncommitted work/' "$REL" && echo "scratch" > untracked.txt )
run 0 "a tree with uncommitted work in the probe's file" FAKE_RC=1 "FAKE_OUT=$RS0016_PROBE"
grep -q 'uncommitted work' "$WORK/$REL" && ok || bad "the uncommitted edit was lost"
[ -s "$STATE/after.status" ] && ok || bad "git status is empty after a run on a dirty tree"

# A file whose last line has no newline is restored byte for byte, and the probe still lands before the `}`.
new_repo
printf '%s' "$ORIGINAL" > "$WORK/$REL"; ( cd "$WORK" && "${GIT[@]}" commit -q -am "no final newline" )
run 0 "a last line without a newline" FAKE_RC=1 "FAKE_OUT=$RS0016_PROBE"
[ "$(tail -n 2 "$STATE/seen")" = "$(printf '%s\n}' "$PROBE")" ] && ok || bad "no final newline: the probe was not placed before '}'"

# A proven run leaves no probe behind, so the next run is not refused.
new_repo
run 0 "a second run after a proven run" FAKE_RC=1 "FAKE_OUT=$RS0016_PROBE"
run 0 "a second run is not refused (the first left no probe)" FAKE_RC=1 "FAKE_OUT=$RS0016_PROBE"

new_repo
run nonzero "the build killed mid-run (SIGKILL)" FAKE_MODE=kill
expect_not_out "PROVEN" "the build killed mid-run"

# The script itself interrupted, as Actions cancels a job: INT, then TERM. Delivered with `timeout`, which signals
# the script and its process group in the foreground; `bash … &` + `kill -INT` would not (SIGINT is ignored in an
# asynchronous command without job control, and an ignored signal cannot be trapped).
probe_env="$(timeout -s INT 1 bash -c 'trap "exit 130" INT; sleep 5; echo completed' 2>/dev/null || true)"
if [ "$probe_env" = completed ]; then
  bad "SIGINT is ignored in this shell (started as an asynchronous command?) — the interrupt cases cannot be measured"
else
  for sig in INT TERM; do
    new_repo
    snapshot before; rc=0; start=$SECONDS
    ( cd "$WORK" && timeout -s "$sig" 2 env DOTNET_BIN="$FAKE" FAKE_STATE="$STATE" FAKE_MODE=sleep bash "$GUARD" ) \
      >"$STATE/out" 2>&1 || rc=$?
    check_rc nonzero "$rc" "the script interrupted with SIG$sig"
    check_restored "the script interrupted with SIG$sig"
    built "the script interrupted with SIG$sig"
    [ ! -f "$STATE/completed" ] && ok || bad "SIG$sig: the build ran to completion instead of being interrupted"
    [ $((SECONDS - start)) -lt 15 ] && ok || bad "SIG$sig: the script did not stop promptly"
    expect_not_out "PROVEN" "the script interrupted with SIG$sig"
    expect_out "restored $REL" "the script interrupted with SIG$sig"
  done
fi

# --- the wiring ----------------------------------------------------------------------------------------------------

# The control must run on every PR, LAST in Pack Warnings Gate (after the pack and both guards, so the mutated
# checkout feeds nothing else), and this harness in Coverage Script Tests. Read from the workflow, not remembered.
CI_YML="${CI_YML:-$SCRIPT_DIR/../../.github/workflows/ci.yml}"
job_runs() { # job_runs <job id> — the `run:` values of that job's steps, in order
  awk -v job="  $1:" '
    $0 == job { injob = 1; next }
    injob && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { injob = 0 }
    injob && /^[[:space:]]+run:[[:space:]]/ { sub(/^[[:space:]]+run:[[:space:]]+/, ""); print }' "$CI_YML"
}
if [ -f "$CI_YML" ]; then
  last_run="$(job_runs pack-check | tail -n 1)"
  [ "$last_run" = "bash scripts/ci/check-public-api-guard-fires.sh" ] && ok \
    || bad "ci.yml: the last step of pack-check runs '$last_run', not the negative control"
  job_runs coverage-scripts | grep -qxF "bash scripts/tests/test_public_api_guard_fires.sh" && ok \
    || bad "ci.yml: coverage-scripts does not run this harness"
else
  bad "ci.yml not found at $CI_YML"
fi

echo "---"; echo "passed=$pass failed=$fails"
[ "$fails" -eq 0 ] || exit 1
echo "OK"
