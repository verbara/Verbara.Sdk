#!/usr/bin/env bash
# check-release-provenance.sh — answers ONE question for publish.yml: do the check runs on the
# tagged commit prove it was built green?
#
# THE RULE. Every COMPLETED check run on the commit concludes success, skipped or neutral, and at
# least one exists — except the check runs of two workflows that say nothing about whether the
# commit was built, which are ignored: `.github/workflows/release-hygiene.yml`, and Dependabot's
# update runs (`dynamic/dependabot/dependabot-updates`). A run still in flight is evidence neither
# way: it reports `conclusion: null`, and publish.yml's own job is always one of them.
#
# WHY RELEASE-HYGIENE. release-hygiene.yml runs on `push: main`, and a release commit reaches main by
# a push like any other — so its checks run on the very commit about to be tagged, before the tag
# exists. Publish Liveness then fails by design ("staged but never tagged"); if the run happens after
# the tag instead, ApiCompat Baseline fails by design (the newest stable tag is above the baseline).
# Both judge the repository against its tags, not whether this commit was built, and ADR-0055 D5
# calls them notifications. Counted here, they were a hard block: v2.5.1 (2026-09-12) failed this
# gate with "1 failure, 14 success", naming nothing, and published only after both runs were re-run
# by hand.
#
# WHY DEPENDABOT. Dependabot's scheduled version-update jobs run as dynamic workflows that attach to
# whatever commit is the HEAD of main when they start, and they often end `cancelled`. They judge the
# dependency graph against the registries, not this commit's build — but counted, a cancelled one
# refuses a release that is fine: v2.5.0's commit d8fc879b carries one, which would refuse a re-run
# of that publish. GitHub assigns that path; a workflow file in this repository always has a
# `.github/workflows/` path, so none can claim it. No other dynamic workflow is ignored.
#
# BY WORKFLOW PATH, NEVER BY NAME. Check-run names are not unique — on the 2.5.1 commit, `Analyze (C#)`
# and `Docs-only gate (CodeQL)` each appear in two check suites — and any workflow can call a job
# `Publish Liveness`. So a check run is ignored only when its check suite belongs to a workflow run
# whose `path` is exactly one of the two above: check run -> check_suite.id -> the workflow run with
# that check_suite_id -> path. A suite with no workflow run (a third-party app's check) is NOT ignored,
# and neither is a suite that more than one path claims.
#
# FAIL CLOSED. Unreadable input, input that is not JSON and input of the wrong shape are "cannot
# tell", never a pass. Ignoring never manufactures evidence: if nothing completed is left, it fails.
#
# INPUTS — the two API documents publish.yml fetches, read here with no network so every rule above is
# unit-tested (scripts/tests/test_release_provenance.sh):
#   <check-runs>     GET repos/{repo}/commits/{sha}/check-runs     one check run per JSON value
#   <workflow-runs>  GET repos/{repo}/actions/runs?head_sha={sha}  one workflow run per JSON value
# which is what `gh api --paginate ... --jq '.check_runs[]'` (and '.workflow_runs[]') prints. One value
# per RUN rather than per page is what makes pagination a non-issue: `--paginate` concatenates pages,
# and in this shape a page boundary leaves no trace. A page document or a single array is rejected as
# the wrong shape rather than guessed at.
#
# NOT CHECKED: that any particular workflow ran — one green completed check run that neither ignored
# workflow produced is enough evidence, including one from an app with no workflow run at all. Nor a
# failure that is being re-run: the API's latest attempt replaces it as soon as the re-run starts, and
# a run in flight is not evidence. See the ADR-0055 addendum (2026-09-12).
#
# Usage: check-release-provenance.sh <sha> <check-runs-file> <workflow-runs-file>
# Exit 0 = proven green. Exit 1 = not proven (a non-green check run, or no evidence). Exit 2 = cannot tell.
set -euo pipefail

HYGIENE_WORKFLOW='.github/workflows/release-hygiene.yml'
DEPENDABOT_WORKFLOW='dynamic/dependabot/dependabot-updates'

cannot_tell() { # cannot_tell <reason>
  echo "::error::release-provenance: cannot tell — $1. Unreadable evidence is not evidence, so the release is refused."
  exit 2
}

# Shared jq definitions. `cmd` escapes a value for a workflow-command line (`%`, CR, LF): a check run's
# name is text any workflow author chooses, and it must neither break an annotation nor start a
# workflow command of its own on a new line.
JQ_DEFS='
  def is_check_run:
    type == "object"
    and (.name | type) == "string"
    and (.status | type) == "string"
    and (.conclusion == null or (.conclusion | type) == "string")
    and (.check_suite | type) == "object"
    and (.check_suite.id | type) == "number";
  def is_workflow_run:
    type == "object"
    and (.check_suite_id | type) == "number"
    and (.path | type) == "string";
  def green: .conclusion == "success" or .conclusion == "skipped" or .conclusion == "neutral";
  def cmd: tostring | gsub("%"; "%25") | gsub("\r"; "%0D") | gsub("\n"; "%0A");
'

[ "$#" -eq 3 ] || cannot_tell "usage: check-release-provenance.sh <sha> <check-runs-file> <workflow-runs-file>"
sha="$1"; checks_file="$2"; runs_file="$3"
[ -n "$sha" ] || cannot_tell "no commit sha was given"
command -v jq >/dev/null 2>&1 || cannot_tell "jq is not on PATH"
ignored_paths="$(jq -cn --arg h "$HYGIENE_WORKFLOW" --arg d "$DEPENDABOT_WORKFLOW" '[$h, $d]')" \
  || cannot_tell "the ignored workflow paths could not be assembled"

# validate <what> <file> <predicate> <gh-jq-filter> — the file is readable, every value in it parses,
# and every value satisfies <predicate>. Anything else ends the run as "cannot tell".
validate() {
  local what="$1" file="$2" predicate="$3" filter="$4" err pos
  if [ ! -f "$file" ] || [ ! -r "$file" ]; then
    cannot_tell "the $what file '$file' cannot be read"
  fi
  if ! err="$(jq empty "$file" 2>&1)"; then
    cannot_tell "the $what file '$file' is not JSON (${err%%$'\n'*})"
  fi
  pos="$(jq -rn "$JQ_DEFS"'
           [inputs] | (to_entries | map(select(.value | '"$predicate"' | not)) | .[0].key) as $k
           | if $k == null then 0 else $k + 1 end' "$file")" \
    || cannot_tell "the $what in '$file' could not be checked"
  if [ "$pos" != 0 ]; then
    cannot_tell "value #$pos in '$file' is not one of the $what — expected one object per JSON value, as gh api --paginate --jq '$filter' prints them"
  fi
}

validate "check runs" "$checks_file" is_check_run '.check_runs[]'
validate "workflow runs" "$runs_file" is_workflow_run '.workflow_runs[]'

# Classify every check run by its own check suite. The workflow paths are looked up per check run, so
# a name shared by two suites is judged twice, once per suite — never once for both. A check run is
# ignored only when exactly one path owns its suite and that path is one of the ignored two.
report="$(jq -cn --slurpfile runs "$runs_file" --argjson ignored "$ignored_paths" "$JQ_DEFS"'
  [ inputs as $c
    | ([ $runs[] | select(.check_suite_id == $c.check_suite.id) | .path ] | unique) as $paths
    | { name: $c.name, status: $c.status, conclusion: $c.conclusion, paths: $paths,
        ignored: (($paths | length) == 1 and ($paths[0] as $p | any($ignored[]; . == $p))),
        app: (($c.app | objects | .slug) // null),
        url: (($c.html_url | strings) // null) } ]
  | { pending:  map(select(.status != "completed")),
      excluded: map(select(.status == "completed" and .ignored)),
      judged:   map(select(.status == "completed" and (.ignored | not))) }
  | .nongreen = (.judged | map(select(green | not)))
  | .verdict = (if (.judged | length) == 0 then "no-evidence"
                elif (.nongreen | length) > 0 then "non-green"
                else "green" end)
' "$checks_file")" || cannot_tell "the check runs could not be evaluated"

printf '%s' "$report" | jq -r --arg sha "$sha" --arg hygiene "$HYGIENE_WORKFLOW" "$JQ_DEFS"'
  def where:
    if (.paths | length) == 1 then .paths[0]
    elif (.paths | length) == 0 then "no workflow run" + (if .app then ", reported by app \(.app)" else "" end)
    else "check suite claimed by several workflows: \(.paths | join(", "))" end;
  def item: "\"\(.name)\" (\(.conclusion // .status))";
  def why($s):
    if . == $hygiene
    then "they judge the repository against its tags (a release not cut, a baseline not moved), not whether \($s) was built"
    else "Dependabot update runs attach to whatever commit is the HEAD of main and judge dependencies, not whether \($s) was built"
    end;
  ($sha | cmd) as $s
  | (.excluded | group_by(.paths[0])[]
     | "::notice::release-provenance: ignored \(length) check run(s) from \(.[0].paths[0] | cmd) — \(.[0].paths[0] | why($s)) (ADR-0055 addendum): \(map(item) | join(", ") | cmd)"),
    (if (.pending | length) > 0 then
       "release-provenance: not completed, so not evidence either way: \(.pending | map(item) | join(", ") | cmd)"
     else empty end),
    (if .verdict == "no-evidence" then
       "::error::No completed check runs for \($s)\(if (.excluded | length) > 0 then " outside \(.excluded | map(.paths[0]) | unique | join(", ") | cmd)" else "" end) — nothing proves this commit was ever built."
     elif .verdict == "non-green" then
       "::error::\($s) has \(.nongreen | length) non-green check run(s) of \(.judged | length) completed:",
       (.nongreen[]
        | "::error::\"\(.name | cmd)\" \(if .conclusion then "concluded \(.conclusion | cmd)" else "completed with no conclusion" end) — \(where | cmd)\(if .url then " — \(.url | cmd)" else "" end)")
     else
       "release-provenance: \($s) — \(.judged | length) completed check run(s), all green (\(.judged | group_by(.conclusion) | map("\(length) \(.[0].conclusion)") | join(", ")))."
     end)
' || cannot_tell "the verdict could not be rendered"

verdict="$(printf '%s' "$report" | jq -r '.verdict')" || cannot_tell "the verdict could not be read"
if [ "$verdict" = "green" ]; then
  exit 0
fi
exit 1
