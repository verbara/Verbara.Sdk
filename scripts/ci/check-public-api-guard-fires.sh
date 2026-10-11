#!/usr/bin/env bash
# check-public-api-guard-fires.sh — the negative control: the undeclared-public-API diagnostic (RS0016) fails the
# build when a public member is added to a shipped package and not declared.
#
# WHY. A green build is what a working guard produces on a declared surface, and it is also exactly what a silenced
# guard produces on any surface. For months RS0016 sat in the repository-wide NoWarn under a comment saying it was
# active, and every build was green. The only observation that tells the two apart is a failure made to happen on
# purpose, so this script makes one on every pull request: an analyzer or SDK bump that stops the diagnostic being
# reported is caught by the pull request that brings it. check-public-api-guard-coverage.sh proves breadth (every
# package compiled with the diagnostic live); this proves depth (the diagnostic actually fires).
#
# WHAT IT DOES. It inserts `public int MainSessionProbe() => GetHashCode();` before the last line of
# src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs, which must be the closing `}` of the class. The probe
# references the instance, so CA1822 ("can be static") does not fail the build first, and it touches no field, so a
# rewrite of the class's state does not break it. It then builds that one project with the COMMITTED configuration:
# no -p:NoWarn and no -p:TreatWarningsAsErrors on the command line, because an override would prove the analyzer
# works, not that the repository lets it. Incremental on purpose: after Build Release only the probe's project
# recompiles.
#
# THE VERDICT is read from the build output, never from its exit code alone. Exit 0 only when the build failed AND
# its output carries `error RS0016` naming MainSessionProbe. Anything else is "not proven", with the cause it saw:
#   * the build was green (the diagnostic is silenced, demoted or not running);
#   * the build was red without RS0016 naming the probe (CA1822, a stale record entry reported as RS0017, a
#     restore failure, an interrupted build — the error lines are printed);
#   * the probe did not compile (`error CS` on the inserted line).
#
# THE TREE IS LEFT AS IT WAS FOUND. The file is copied before the first edit and copied back from that copy on every
# exit path, interruption included (`trap … EXIT`, with INT/TERM/HUP turned into an exit so the EXIT trap runs), and
# the restore is checked by sha256 against the copy. It is never restored with `git checkout`, `git restore` or
# `git stash`: on a tree carrying uncommitted work they would replace the developer's file with HEAD, and an empty
# `git status` afterwards is exactly what that accident looks like. Build outputs under bin/ and obj/ are outside
# this guarantee. If the probe is already in the file — the leftover of a run killed beyond any trap (SIGKILL, power
# loss) — the script refuses to start and names it: "declare it", the guard's own remedy, would turn it into API.
#
# Deliver interrupts with `timeout -s INT|TERM <n> bash scripts/ci/check-public-api-guard-fires.sh`, not with
# `bash … &` + `kill -INT`: bash ignores SIGINT in an asynchronous command started without job control, and a signal
# ignored on entry cannot be trapped. The file is still restored then, when the run completes.
#
# Usage: scripts/ci/check-public-api-guard-fires.sh   (from the repo root; CI runs it as the last step of Pack
#        Warnings Gate, after everything that consumes the build output)
# Exit 0 = the guard fired on the probe. Exit 1 = not proven. Exit 2 = cannot tell (refused to start, or the restore
# could not be verified). DOTNET_BIN is overridable so the unit tests can stand in for the build.
set -euo pipefail

DOTNET_BIN="${DOTNET_BIN:-dotnet}"
PROJECT="src/Verbara.Sdk.Sessions/Verbara.Sdk.Sessions.csproj"
FILE="src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs"
PROBE_NAME="MainSessionProbe"
PROBE_LINE="    public int ${PROBE_NAME}() => GetHashCode();"

say()         { echo "public-api-guard-fires: $*"; }
not_proven()  { echo "public-api-guard-fires: not proven: $*" >&2; exit 1; }
cannot_tell() { echo "public-api-guard-fires: $* — cannot tell." >&2; exit 2; }
shown()       { local s="${1//$'\r'/\\r}"; printf "'%s'" "${s//$'\t'/\\t}"; } # whitespace stays visible

[ -f "$PROJECT" ] || cannot_tell "no $PROJECT (run this from the repository root)"
[ -f "$FILE" ]    || cannot_tell "no $FILE (run this from the repository root)"

if grep -q -- "$PROBE_NAME" "$FILE"; then
  grep -n -- "$PROBE_NAME" "$FILE" | sed 's/^/    /' >&2
  cannot_tell "refusing to start: $PROBE_NAME is already in $FILE — the leftover of an interrupted run. Remove that \
line by hand; do not declare it"
fi

last="$(tail -n 1 -- "$FILE")"
[ "$last" = "}" ] || not_proven "probe not placed — the last line of $FILE is $(shown "$last"), not '}'"
probe_at="$(awk 'END { print NR }' "$FILE")" # the probe takes the last line's number; the `}` moves down one

orig="$(mktemp)"
cp -- "$FILE" "$orig"
want="$(sha256sum < "$orig")"; want="${want%% *}"
log=""
restore() {
  local rc=$? got=""
  trap - EXIT INT TERM HUP
  if cp -- "$orig" "$FILE" 2>/dev/null; then got="$(sha256sum < "$FILE")"; got="${got%% *}"; fi
  [ -z "$log" ] || rm -f -- "$log"
  if [ "$got" != "$want" ]; then
    echo "public-api-guard-fires: RESTORE FAILED: $FILE does not match the copy taken before the edit" \
         "(sha256 ${want}); that copy is kept at $orig — cannot tell." >&2
    exit 2
  fi
  rm -f -- "$orig"
  say "restored $FILE from the copy taken before the edit (sha256 ${want})"
  exit "$rc"
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

{ head -n "$((probe_at - 1))" -- "$orig"; printf '%s\n' "$PROBE_LINE"; tail -n 1 -- "$orig"; } > "$FILE"
say "probe inserted at $FILE:$probe_at: ${PROBE_LINE#    }"

log="$(mktemp)"
say "building $PROJECT with the committed configuration"
build_rc=0
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1 \
  "$DOTNET_BIN" build "$PROJECT" -c Release -nodeReuse:false >"$log" 2>&1 || build_rc=$?
sed 's/^/    /' "$log"
say "build exit code: $build_rc"

fired="$(grep -E "error RS0016\b.*\b${PROBE_NAME}\b" "$log" | head -1 || true)"
if [ "$build_rc" -ne 0 ] && [ -n "$fired" ]; then
  say "PROVEN: the build failed because the probe was not declared:"
  echo "    ${fired}"
  exit 0
fi

errors="$(grep -E ': (error|warning) [A-Z]+[0-9]+' "$log" | sed -E 's/^.*: ((error|warning) [A-Z]+[0-9]+:[^[]*).*$/\1/' \
            | LC_ALL=C sort -u | head -20 || true)"
if grep -Eq "CallSessionManager\.cs\(${probe_at},[0-9]+\): error CS[0-9]+" "$log"; then
  not_proven "the probe did not compile (error CS on the inserted line $FILE:$probe_at):
$(grep -E "CallSessionManager\.cs\(${probe_at},[0-9]+\): error CS[0-9]+" "$log" | LC_ALL=C sort -u | sed 's/^/    /')"
elif [ "$build_rc" -eq 0 ]; then
  not_proven "the build was green with an undeclared public member — RS0016 is silenced, demoted or not running${errors:+
  diagnostics it printed:
$(printf '%s\n' "$errors" | sed 's/^/    /')}"
else
  not_proven "the build was red, but not with RS0016 naming the probe (exit code $build_rc)${errors:+:
$(printf '%s\n' "$errors" | sed 's/^/    /')}"
fi
