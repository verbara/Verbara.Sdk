#!/usr/bin/env bash
# report-perf-breach.sh — turn a perf-baseline breach into something assignable (ADR-0042 D5).
#
# WHY THIS EXISTS. `perf-regression.yml` runs weekly on a schedule. A scheduled red that nobody is
# told about is observational under another name: the run goes red on a Sunday, the tab nobody has
# open shows a cross, and by the time anyone looks the bisection window is a month of commits. D5
# therefore obliges a DURABLE artifact naming the benchmark, the baseline, the observed value and
# the band — so the signal outlives the run that produced it.
#
# WHAT IT DOES. One open issue, reused. The title is the identity: if an open issue with exactly
# that title exists, this comments on it; otherwise it files a new one. Comments rather than
# rewrites, so the issue accumulates the WEEK-BY-WEEK trend, which is the thing that tells a
# reviewer whether a benchmark stepped once or is drifting.
#
# Usage: report-perf-breach.sh <markdown-body-file>
#
# Environment:
#   GH_TOKEN                   required by `gh`; the workflow job needs `issues: write`
#   GITHUB_REPOSITORY          owner/repo (set by Actions); passed to `gh --repo`
#   GITHUB_SERVER_URL/RUN_ID   used to link back to the run (optional)
#   PERF_GATE_ENFORCE          'true' => a notification failure fails the step. Anything else =>
#                              warn and exit 0. THE SAME single flag that arms the comparison in
#                              check-perf-baseline.py, deliberately: while the gate is observing
#                              (openspec `enforce-unguarded-public-claims` §2.5) neither half of it
#                              may red the job, and the flip arms both at once. One line, one
#                              meaning.
#   PERF_BREACH_ISSUE_TITLE    override the issue title (tests use it; CI does not set it)
#   PERF_BREACH_ISSUE_LABEL    override the label (default: performance)
set -uo pipefail

BODY_FILE="${1:-}"
TITLE="${PERF_BREACH_ISSUE_TITLE:-Perf regression: benchmark outside its baseline band}"
LABEL="${PERF_BREACH_ISSUE_LABEL:-performance}"
ENFORCE="$(printf '%s' "${PERF_GATE_ENFORCE:-false}" | tr '[:upper:]' '[:lower:]')"

# In OBSERVING mode a notifier that cannot notify must not red the job; in ENFORCING mode it must,
# because an unreported breach is exactly the state D5 forbids.
give_up() { # give_up <message>
  if [ "$ENFORCE" = "true" ] || [ "$ENFORCE" = "1" ] || [ "$ENFORCE" = "yes" ]; then
    echo "::error::report-perf-breach: $1" >&2
    exit 1
  fi
  echo "::warning::report-perf-breach: $1 (observing mode — not failing the job)" >&2
  exit 0
}

[ -n "$BODY_FILE" ] || give_up "usage: report-perf-breach.sh <markdown-body-file>"
[ -f "$BODY_FILE" ] || give_up "body file '$BODY_FILE' does not exist — nothing to report."
[ -s "$BODY_FILE" ] || give_up "body file '$BODY_FILE' is empty — refusing to file a blank issue."
command -v gh >/dev/null 2>&1 || give_up "the 'gh' CLI is not on PATH."

REPO_ARGS=()
[ -n "${GITHUB_REPOSITORY:-}" ] && REPO_ARGS=(--repo "$GITHUB_REPOSITORY")

RUN_URL=""
if [ -n "${GITHUB_SERVER_URL:-}" ] && [ -n "${GITHUB_REPOSITORY:-}" ] && [ -n "${GITHUB_RUN_ID:-}" ]; then
  RUN_URL="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
COMPOSED="$WORK/body.md"
{
  cat "$BODY_FILE"
  echo
  echo "---"
  [ -n "$RUN_URL" ] && echo "Run: $RUN_URL"
  echo
  echo "Baselines move only by human-authored commit (ADR-0042 D6) — CI never writes back to"
  echo "\`Tests/Verbara.Sdk.Benchmarks/baseline.json\`. If this is a real regression, fix the code."
  echo "If the hosted-runner fleet moved under us, re-baseline per"
  echo "\`Tests/Verbara.Sdk.Benchmarks/baseline.README.md\`: a PR stating which benchmark moved, in"
  echo "which direction, by how much, and why."
} > "$COMPOSED"

# Exact-title match over OPEN issues. Deliberately not `--search`: GitHub's issue search index lags
# by seconds-to-minutes, and a lagging lookup files a duplicate every week.
existing="$(gh issue list "${REPO_ARGS[@]}" --state open --limit 100 --json number,title \
  --jq "map(select(.title == \"$TITLE\")) | .[0].number // empty" 2>/dev/null)" || \
  give_up "could not list issues (is GH_TOKEN set with issues: write?)."

if [ -n "$existing" ]; then
  if gh issue comment "$existing" "${REPO_ARGS[@]}" --body-file "$COMPOSED"; then
    echo "report-perf-breach: appended this run's breach to issue #$existing."
    exit 0
  fi
  give_up "could not comment on issue #$existing."
fi

CREATE_ARGS=(issue create "${REPO_ARGS[@]}" --title "$TITLE" --body-file "$COMPOSED")
# Attach the label only when it already exists — `gh issue create` hard-fails on an unknown label,
# and losing the whole notification over a missing triage label is a bad trade.
if gh label list "${REPO_ARGS[@]}" --limit 200 --json name \
     --jq "map(select(.name == \"$LABEL\")) | length" 2>/dev/null | grep -qx '1'; then
  CREATE_ARGS+=(--label "$LABEL")
fi

if gh "${CREATE_ARGS[@]}"; then
  echo "report-perf-breach: filed a new issue titled '$TITLE'."
  exit 0
fi
give_up "could not create the breach issue."
